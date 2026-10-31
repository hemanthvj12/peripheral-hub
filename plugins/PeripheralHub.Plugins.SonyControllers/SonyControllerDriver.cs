using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins.SonyControllers;

/// <summary>
/// DualSense / DualSense Edge / DualShock 4 over USB, Bluetooth, or Sony's wireless adapter.
/// Controllers stream input continuously, so battery is decoded passively from every report.
/// One controller is tracked across connections by its Bluetooth MAC address.
/// </summary>
internal sealed class SonyControllerDriver(PeripheralHubOptions options) : IDeviceDriver
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    // Controllers seen during this run, kept so they show as Disconnected after they go away.
    private readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SonyBattery? Battery, DateTimeOffset? BatteryAt, DateTimeOffset? SeenAt)> _lastSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, bool> _classicConnected = new Dictionary<string, bool>();
    private HashSet<string> _lastActive = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openFailed = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    /// <param name="Creator">For a virtual controller: the app that made it ("Punktfunk"), or "" when Windows doesn't say.</param>
    private sealed record Known(string Key, SonyModel Model, int Order, string? Creator = null)
    {
        public bool IsVirtual => Creator is not null;
    }


    public void Update(ScanContext context)
    {
        bool changed = false;
        lock (_lock)
        {
            _classicConnected = context.BluetoothClassicConnected;
            var wanted = context.Hid
                .Where(h => h.VendorId == SonyProtocol.SonyVid && SonyProtocol.ModelFor(h.ProductId) is not null
                            && h.UsagePage == 0x01 && h.Usage == 0x05)
                .ToDictionary(h => h.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, s) in _sessions.ToList())
            {
                // Still listed and readable isn't enough: when another app hides the controller (DSX's emulation modes),
                // our open handle keeps working, but a fresh open is refused. Drop it then, so the tile follows within
                // one scan; the open below brings it back once it's visible again.
                bool reachable = wanted.ContainsKey(path) && !s.IsClosed && s.CanReopen();
                if (reachable) continue;
                if (wanted.ContainsKey(path) && !s.IsClosed)
                    options.Log?.Invoke($"Sony: {path} can't be opened any more (hidden by another app); closing it");
                s.Dispose();
                _sessions.Remove(path);
                changed = true;
            }
            foreach (var hid in wanted.Values)
            {
                if (_sessions.ContainsKey(hid.Path)) continue;
                var conn = HidConnection.TryOpen(hid);
                if (conn is null)
                {
                    // Retried every scan (e.g. while another app hides it): log once, until it opens again.
                    if (_openFailed.Add(hid.Path)) options.Log?.Invoke($"Sony: cannot open {hid.Path} (hidden by another app or in use)");
                    continue;
                }
                _openFailed.Remove(hid.Path);
                var model = SonyProtocol.ModelFor(hid.ProductId)!.Value;
                var session = new Session(conn, model, () => options.SwitchBluetoothControllers);
                session.Changed += () => Changed?.Invoke();
                _sessions[hid.Path] = session;
                if (!_known.ContainsKey(session.Key))
                    _known[session.Key] = new Known(session.Key, model == SonyModel.DualShock4Dongle ? SonyModel.DualShock4 : model,
                        _known.Count, hid.Virtual?.ShortCreator);
                session.Start();
                changed = true;
            }

            // Silence (no reports) and Bluetooth disconnects aren't events, so detect transitions here.
            var active = ActiveSessions(context.Now).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!active.SetEquals(_lastActive)) changed = true;
            _lastActive = active;
        }
        if (changed) Changed?.Invoke();
    }

    private IEnumerable<Session> ActiveSessions(DateTimeOffset now) => _sessions.Values.Where(s =>
        !s.IsClosed &&
        // Real controllers stream reports non-stop, so silence means gone. A virtual one (punktfunk) only reports on
        // changes, so it counts as connected while its software device exists (the app removes it when done).
        (s.IsVirtual || now - s.LastReportAt < options.ControllerSilenceTimeout) &&
        !(s.IsBluetooth && s.Mac is { } mac && _classicConnected.TryGetValue(mac, out var c) && !c) &&
        s.DongleHasController != false);

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock)
        {
            var active = ActiveSessions(now).ToList();
            var result = new List<DeviceSnapshot>();
            foreach (var known in _known.Values.OrderBy(k => k.Order))
            {
                string name = DisplayName(known);
                // Prefer the cable, then the adapter, then Bluetooth when one controller is linked several ways.
                var s = active.Where(a => a.Key == known.Key)
                    .OrderBy(a => a.Connection switch { ConnectionType.Wired => 0, ConnectionType.Dongle => 1, _ => 2 })
                    .FirstOrDefault();
                if (s is null)
                {
                    // Keep the last reading so the UI can show "last seen ... at N%".
                    _lastSeen.TryGetValue(known.Key, out var last);
                    result.Add(new DeviceSnapshot($"sony:{known.Key}", name, DeviceKind.Controller,
                        ConnectionType.None, LinkState.Disconnected, last.Battery?.Percent, ChargeState.Unknown,
                        last.BatteryAt, last.SeenAt, Note: VirtualNote(known), Model: ModelId(known.Model),
                        BluetoothMac: MacOf(known.Key), Source: SourceOf(known)));
                    continue;
                }
                // Virtual controllers report a made-up level (punktfunk and DSX's emulation both read a fixed 100%),
                // so no battery is shown for them (owner, 2026-10-28).
                var b = known.IsVirtual ? null : s.Battery;
                var batteryAt = known.IsVirtual ? null : s.BatteryAt;
                _lastSeen[known.Key] = (b, batteryAt, s.LastReportAt);
                // Bluetooth in basic reports: in Basic mode (we may not switch it, and no other app has) there's no battery
                // to read; in Full mode it's a moment until the switch takes effect.
                bool noReading = !known.IsVirtual && s.IsBluetooth && b is null;
                bool unavailable = noReading && !options.SwitchBluetoothControllers;
                // Turned off while the controller is still in full mode: only a reconnect brings basic mode back.
                bool reconnect = !known.IsVirtual && s.InFullMode && !options.SwitchBluetoothControllers;
                string? note = b?.Charging == ChargeState.Error ? "Charging error (voltage or temperature)"
                    : unavailable ? BasicModeNote
                    : reconnect ? "Still in full mode, so browsers and some older games can't see its input. Turn the controller off and on to switch it back to basic mode."
                    : noReading ? "Waiting for the battery level…"
                    : VirtualNote(known);
                result.Add(new DeviceSnapshot($"sony:{known.Key}", name, DeviceKind.Controller, s.Connection, LinkState.Online,
                    b?.Percent, b?.Charging ?? ChargeState.Unknown, batteryAt, s.LastReportAt,
                    Note: note, Model: ModelId(known.Model), BluetoothMac: MacOf(known.Key), Source: SourceOf(known),
                    BatteryUnavailable: unavailable));
            }
            return result;
        }
    }

    /// <summary>The controller's Bluetooth address, when its key is one (not a "path-…"/"virtual-…" stand-in).</summary>
    private static string? MacOf(string key) => key.Length == 12 && key.All(Uri.IsHexDigit) ? key.ToLowerInvariant() : null;


    private static string ModelId(SonyModel m) => m switch
    {
        SonyModel.DualSense => "dualsense",
        SonyModel.DualSenseEdge => "dualsense-edge",
        _ => "dualshock4",
    };

    private string DisplayName(Known known)
    {
        string baseName = known.Model switch
        {
            SonyModel.DualSense => "DualSense",
            SonyModel.DualSenseEdge => "DualSense Edge",
            _ => "DualShock 4",
        };
        // Virtual ones keep the plain model name (the creator goes next to the "Virtual" badge, see SourceOf) and are
        // never numbered. Real ones are numbered only among real ones of that model.
        if (known.IsVirtual) return baseName;
        var same = _known.Values.Where(k => k.Model == known.Model && !k.IsVirtual).OrderBy(k => k.Order).ToList();
        return same.Count > 1 ? $"{baseName} {same.IndexOf(known) + 1}" : baseName;
    }

    /// <summary>Shown next to the "Virtual" badge: the app that made it, e.g. "Punktfunk".</summary>
    private static string? SourceOf(Known known) => known.IsVirtual ? (known.Creator is { Length: > 0 } c ? c : null) : null;

    private const string BasicModeNote =
        "Basic mode: the battery shows only while Steam, DSX or a game has switched the controller to full mode. " +
        "Turn on \"Show Sony controller battery over Bluetooth\" in the menu to always show it.";

    private static string? VirtualNote(Known known) => known.IsVirtual
        ? $"Virtual controller created by {(known.Creator is { Length: > 0 } c ? c : "another app")}. " +
          "Its battery isn't shown: virtual controllers report a made-up level."
        : null;

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly HidConnection _conn;
        private readonly SonyModel _model;
        private readonly Func<bool> _maySwitch;
        private long _lastEnableTicks;

        public event Action? Changed;
        public string Key { get; }
        public string? Mac { get; }
        public bool IsBluetooth => _conn.Info.IsBluetooth;
        public bool IsVirtual => _conn.Info.IsVirtual;
        public bool CanReopen() => _conn.CanReopen();
        public bool IsClosed => _conn.IsClosed;
        public ConnectionType Connection =>
            _conn.Info.IsVirtual ? ConnectionType.Virtual
            : IsBluetooth ? ConnectionType.Bluetooth
            : _model == SonyModel.DualShock4Dongle ? ConnectionType.Dongle : ConnectionType.Wired;
        public SonyBattery? Battery { get; private set; }
        public DateTimeOffset? BatteryAt { get; private set; }
        public DateTimeOffset LastReportAt { get; private set; } = DateTimeOffset.Now; // grace period after opening
        public bool? DongleHasController { get; private set; }
        /// <summary>Bluetooth and sending full reports (0x31 / DS4 0x11), whoever switched it; it stays so until it reconnects.</summary>
        public bool InFullMode { get; private set; }

        public Session(HidConnection conn, SonyModel model, Func<bool> maySwitch)
        {
            _conn = conn;
            _maySwitch = maySwitch;
            _model = model;
            if (conn.Info.Virtual is { } origin)
            {
                // A virtual controller's "MAC" is whatever its software reports, so key it by the software device and
                // the model it presents (e.g. SWD\…\PF_PAD_0 → "virtual-pf_pad_0-dualsense"). The model matters: DSX's
                // emulated DualShock 4 and DualSense both hang off ROOT\USB\0000 (seen 2026-10-28), and keying by the
                // node alone made the DualSense inherit the DualShock 4's tile and name.
                Key = "virtual-" + origin.InstanceId[(origin.InstanceId.LastIndexOf('\\') + 1)..].ToLowerInvariant() +
                      "-" + model.ToString().ToLowerInvariant();
            }
            else
            {
                Mac = ReadMac();
                Key = Mac ?? $"path-{(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(conn.Info.Path):x8}";
            }
            conn.ReportReceived += OnReport;
            conn.Closed += _ => Changed?.Invoke();
        }

        private string? ReadMac()
        {
            if (IsBluetooth)
            {
                // Bluetooth HID reports the controller's MAC as its serial number.
                var hex = new string((_conn.Info.SerialNumber ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
                return hex.Length == 12 ? hex : null;
            }
            var (id, len) = SonyProtocol.UsbMacFeature(_model == SonyModel.DualShock4Dongle ? SonyModel.DualShock4 : _model);
            return _conn.GetFeature(id, len) is { } f ? SonyProtocol.ParseUsbMac(f) : null;
        }

        public void Start()
        {
            _conn.Start();
            if (IsBluetooth && _maySwitch()) EnableFullReports();
        }

        /// <summary>
        /// Reading the calibration feature report makes a Bluetooth controller send full reports (with battery). It stays
        /// that way until it reconnects, and apps reading it as a plain gamepad (browsers, DirectInput games) then get no
        /// input, so it's only done while <see cref="PeripheralHubOptions.SwitchBluetoothControllers"/> allows it.
        /// </summary>
        private void EnableFullReports()
        {
            Interlocked.Exchange(ref _lastEnableTicks, Environment.TickCount64);
            var (id, len) = SonyProtocol.BluetoothEnableFeature(_model);
            _conn.GetFeature(id, len);
        }

        private void OnReport(byte[] report)
        {
            var now = DateTimeOffset.Now;
            LastReportAt = now;
            if (IsBluetooth && report.Length > 0) InFullMode = report[0] != 0x01;

            // Still getting short reports (e.g. after a reconnect): ask again, at most every 2 s.
            if (IsBluetooth && report[0] == 0x01 && _maySwitch() && Environment.TickCount64 - Interlocked.Read(ref _lastEnableTicks) > 2000)
                Task.Run(EnableFullReports);

            if (_model == SonyModel.DualShock4Dongle && SonyProtocol.DongleHasController(report) is { } has && has != DongleHasController)
            {
                DongleHasController = has;
                Changed?.Invoke();
            }

            var b = SonyProtocol.ParseBattery(_model, report, IsBluetooth);
            if (b is null) return;
            BatteryAt = now;
            if (b != Battery) { Battery = b; Changed?.Invoke(); }
        }

        public void Dispose() => _conn.Dispose();
    }
}
