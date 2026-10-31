using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins.Monitors;

/// <summary>
/// Monitors Windows is showing the desktop on, from its display configuration (checked every scan; cheap), with
/// brightness and speaker volume over DDC/CI. DDC/CI calls are slow (tens of ms each, sometimes more), so they run on
/// a worker thread: once a minute, every few seconds while the tile's drawer is open, and right after a monitor
/// appears. A monitor that is unplugged, switched off or turned off in Windows' display settings drops out of the
/// active list and shows as Disconnected for the rest of the run (the UI hides it unless pinned).
/// Verified 2026-10-29 with an Acer VG240Y S on DisplayPort: brightness (0x10) and volume (0x62) out of 100.
/// </summary>
internal sealed class MonitorDriver : IDeviceDriver, IMonitorControlDriver, IWatchableDriver
{
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WatchedReadInterval = TimeSpan.FromSeconds(3);
    /// <summary>After a change, wait before reading back: some monitors still answer with the old value right away.</summary>
    private static readonly TimeSpan ReadAfterWrite = TimeSpan.FromSeconds(2);
    /// <summary>A monitor that never answered DDC/CI is asked again this rarely (some only answer once they're awake).</summary>
    private static readonly TimeSpan NoDdcRetry = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public required string Id;
        public required string BaseName;
        public string Name = "";
        public required ConnectionType Connection;
        public string? Creator;            // for a virtual display: the app that made it
        public required ActiveDisplay Display;
        public bool Online;
        public int DdcIndex = -1;          // which physical monitor of its desktop source; -1 = no DDC/CI
        public MonitorLevel? Brightness, Volume;
        public bool? Muted;
        public bool DdcAnswered;           // DDC/CI ever answered
        public int DdcFailures;
        public DateTimeOffset NextRead;
    }

    private readonly PeripheralHubOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase); // by device id, in order seen
    private readonly Dictionary<string, (string Id, string Name, ConnectionType Connection, string? Creator)> _identity =
        new(StringComparer.OrdinalIgnoreCase); // by instance id: EDID and device-tree lookups are done once
    private readonly SemaphoreSlim _ddcGate = new(1, 1); // one DDC/CI conversation at a time
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];
    private HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLog = "";

    public event Action? Changed;

    public MonitorDriver(PeripheralHubOptions options)
    {
        _options = options;
        _worker = new Thread(DdcLoop) { IsBackground = true, Name = "Monitors DDC/CI" };
        _worker.Start();
    }

    public void Update(ScanContext context)
    {
        var displays = DisplayConfig.Active();
        var log = string.Join("; ", displays.Select(d => $"{d.InstanceId} \"{d.FriendlyName}\" tech {d.OutputTechnology} {d.Width}x{d.Height}@{d.RefreshHz:0.##} on {d.SourceName}"));
        if (log != _lastLog)
        {
            _lastLog = log;
            _options.Log?.Invoke($"Monitors: {(log.Length == 0 ? "none active" : log)}");
        }

        bool wake = false;
        lock (_lock)
        {
            var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in displays)
            {
                var who = IdentityOf(d);
                // Two monitors with the same EDID serial (some cheap ones use 0): fall back to the Windows instance id.
                var id = ids.Add(who.Id) ? who.Id : "monitor:" + d.InstanceId.ToLowerInvariant();
                ids.Add(id);
                if (!_entries.TryGetValue(id, out var e))
                {
                    e = new Entry { Id = id, BaseName = who.Name, Connection = who.Connection, Creator = who.Creator, Display = d };
                    _entries[id] = e;
                }
                if (!e.Online) { e.NextRead = context.Now; wake = true; } // new or back: read its settings now
                e.Online = true;
                e.Display = d;
                e.Connection = who.Connection;
                online.Add(id);
            }
            foreach (var e in _entries.Values) if (!online.Contains(e.Id)) e.Online = false;

            // DDC/CI handles come per desktop source; a source shown on several monitors (duplicate mode) has one handle
            // each, in the same order as its paths.
            foreach (var group in _entries.Values.Where(e => e.Online).GroupBy(e => e.Display.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                int i = 0;
                foreach (var e in group) e.DdcIndex = UsesDdc(e.Connection) ? i++ : -1;
            }
            NameDuplicates();
        }
        if (wake) _wake.Set();
        Publish();
    }

    private (string Id, string Name, ConnectionType Connection, string? Creator) IdentityOf(ActiveDisplay d)
    {
        if (_identity.TryGetValue(d.InstanceId, out var known)) return known;
        var pnp = PnpId(d.InstanceId);
        var edid = DisplayConfig.ReadEdid(d.InstanceId);
        var serial = edid is null ? null : EdidSerial(edid);
        var origin = d.InstanceId.Length > 0 ? DeviceTree.FindVirtualOrigin(d.InstanceId) : null;
        var connection = ConnectionFor(d.OutputTechnology, origin is not null);
        string? creator = origin?.ShortCreator is { Length: > 0 } c ? c : null;
        string id = pnp is not null && serial is not null
            ? $"monitor:{pnp}:{serial}".ToLowerInvariant()
            : "monitor:" + d.InstanceId.ToLowerInvariant();
        return _identity[d.InstanceId] = (id, NameFor(pnp, d.FriendlyName, connection), connection, creator);
    }

    /// <summary>Two connected monitors of the same model are numbered: "Dell U2720Q 1", "Dell U2720Q 2".</summary>
    private void NameDuplicates()
    {
        foreach (var e in _entries.Values) e.Name = e.BaseName;
        foreach (var group in _entries.Values.Where(e => e.Online).GroupBy(e => e.BaseName).Where(g => g.Count() > 1))
        {
            int n = 1;
            foreach (var e in group) e.Name = $"{e.BaseName} {n++}";
        }
    }

    private void Publish()
    {
        IReadOnlyList<DeviceSnapshot> list;
        bool changed;
        lock (_lock)
        {
            list = _entries.Values.Select(ToSnapshot).ToList();
            changed = list.Count != _snapshots.Count || list.Where((s, i) => s.DiffersFrom(_snapshots[i])).Any();
            _snapshots = list;
        }
        if (changed) Changed?.Invoke();
    }

    private static DeviceSnapshot ToSnapshot(Entry e)
    {
        if (!e.Online)
            return new DeviceSnapshot(e.Id, e.Name, DeviceKind.Monitor, ConnectionType.None, LinkState.Disconnected, null,
                ChargeState.Unknown, null);

        string? note = e.Connection == ConnectionType.Virtual
            ? $"Virtual display created by {e.Creator ?? "another app"}"
            : e.DdcIndex >= 0 && !e.DdcAnswered && e.DdcFailures > 0
                ? "This monitor doesn't answer DDC/CI (it may be turned off in its menu), so its brightness can't be changed from here"
                : null;
        var d = e.Display;
        return new DeviceSnapshot(e.Id, e.Name, DeviceKind.Monitor, e.Connection, LinkState.Online, null, ChargeState.Unknown,
            null, Note: note, Source: e.Creator,
            Monitor: new MonitorInfo(d.Width, d.Height, (int)Math.Round(d.RefreshHz), e.Brightness, e.Volume,
                e.Volume is null ? null : e.Muted)); // mute only means something with speakers
    }

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock) return _snapshots;
    }

    public void SetWatched(IReadOnlySet<string> deviceIds)
    {
        bool wake = false;
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            foreach (var id in deviceIds)
                if (!_watched.Contains(id) && _entries.TryGetValue(id, out var e) && e.DdcAnswered)
                {
                    e.NextRead = now; // drawer just opened: catch up with changes made in the monitor's own menu
                    wake = true;
                }
            _watched = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
        }
        if (wake) _wake.Set();
    }

    // ---- DDC/CI ----

    private void DdcLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne(TimeSpan.FromSeconds(1));
            if (_disposed) return;
            List<Entry> due;
            lock (_lock)
            {
                var now = DateTimeOffset.Now;
                due = _entries.Values.Where(e => e.Online && e.DdcIndex >= 0 && now >= e.NextRead).ToList();
            }
            foreach (var e in due)
            {
                string source; int index;
                lock (_lock) { source = e.Display.SourceName; index = e.DdcIndex; }
                (MonitorLevel? B, MonitorLevel? V, bool? M) levels = default;
                _ddcGate.Wait();
                try
                {
                    levels = Ddc.WithMonitors(source, hs => index < hs.Length
                        ? (Ddc.Read(hs[index], Ddc.Brightness), Ddc.Read(hs[index], Ddc.Volume), Ddc.ReadMute(hs[index]))
                        : default);
                }
                catch (Exception ex) { _options.Log?.Invoke($"Monitors: DDC/CI read of {e.Id} failed: {ex.Message}"); }
                finally { _ddcGate.Release(); }

                lock (_lock)
                {
                    var now = DateTimeOffset.Now;
                    bool answered = levels.B is not null || levels.V is not null;
                    if (answered)
                    {
                        if (!e.DdcAnswered) _options.Log?.Invoke($"Monitors: {e.Id} answers DDC/CI (brightness {Show(levels.B)}, volume {Show(levels.V)}, muted {levels.M?.ToString() ?? "unknown"})");
                        e.DdcAnswered = true;
                        e.DdcFailures = 0;
                        e.Brightness = levels.B;
                        e.Volume = levels.V;
                        e.Muted = levels.M;
                    }
                    else e.DdcFailures++; // keep the last values: DDC/CI sometimes misses a reply
                    e.NextRead = now + (!e.DdcAnswered && e.DdcFailures >= 3 ? NoDdcRetry
                        : _watched.Contains(e.Id) ? WatchedReadInterval : ReadInterval);
                }
            }
            if (due.Count > 0) Publish();
        }
    }

    private static string Show(MonitorLevel? l) => l is null ? "none" : $"{l.Value}/{l.Max}";

    public async Task<string?> SetMonitorLevelAsync(string deviceId, MonitorSetting setting, int percent, CancellationToken ct = default)
    {
        string source; int index; MonitorLevel? level; bool? muted;
        lock (_lock)
        {
            if (!_entries.TryGetValue(deviceId, out var e) || !e.Online) return "The monitor isn't connected.";
            source = e.Display.SourceName;
            index = e.DdcIndex;
            level = setting == MonitorSetting.Brightness ? e.Brightness : e.Volume;
            muted = e.Muted;
        }
        if (index < 0 || level is null) return "This monitor doesn't let other apps change that.";

        int raw = (int)Math.Round(Math.Clamp(percent, 0, 100) * level.Max / 100.0);
        byte code = setting == MonitorSetting.Brightness ? Ddc.Brightness : Ddc.Volume;
        // The Acer VG240Y S stays silent when unmuted while its volume is 0, even after the volume goes back up,
        // although it then reports "not muted" (owner, 2026-10-29; registers checked). Muting and unmuting again
        // brings the sound back, so do that whenever the volume rises from 0: it's silent at that moment anyway.
        bool rearmMute = setting == MonitorSetting.Volume && level.Value == 0 && raw > 0 && muted == false;
        await _ddcGate.WaitAsync(ct).ConfigureAwait(false);
        bool ok;
        try
        {
            ok = await Task.Run(() => Ddc.WithMonitors(source, hs =>
            {
                if (index >= hs.Length || !Ddc.Write(hs[index], code, raw)) return false;
                if (rearmMute)
                {
                    Thread.Sleep(60); // DDC/CI wants ~50 ms between commands
                    Ddc.Write(hs[index], Ddc.Mute, 1);
                    Thread.Sleep(60);
                    Ddc.Write(hs[index], Ddc.Mute, 2);
                }
                return true;
            }), ct).ConfigureAwait(false);
        }
        finally { _ddcGate.Release(); }
        if (!ok) return "The monitor didn't accept the change.";

        lock (_lock)
        {
            if (_entries.TryGetValue(deviceId, out var e))
            {
                if (setting == MonitorSetting.Brightness) e.Brightness = level with { Value = raw };
                else e.Volume = level with { Value = raw };
                var later = DateTimeOffset.Now + ReadAfterWrite;
                if (e.NextRead < later) e.NextRead = later;
            }
        }
        Publish();
        return null;
    }

    public async Task<string?> SetMonitorMuteAsync(string deviceId, bool muted, CancellationToken ct = default)
    {
        string source; int index; bool? current;
        lock (_lock)
        {
            if (!_entries.TryGetValue(deviceId, out var e) || !e.Online) return "The monitor isn't connected.";
            source = e.Display.SourceName;
            index = e.DdcIndex;
            current = e.Muted;
        }
        if (index < 0 || current is null) return "This monitor doesn't let other apps mute it.";

        await _ddcGate.WaitAsync(ct).ConfigureAwait(false);
        bool ok;
        try { ok = await Task.Run(() => Ddc.WithMonitors(source, hs => index < hs.Length && Ddc.Write(hs[index], Ddc.Mute, muted ? 1 : 2)), ct).ConfigureAwait(false); }
        finally { _ddcGate.Release(); }
        if (!ok) return "The monitor didn't accept the change.";

        lock (_lock)
        {
            if (_entries.TryGetValue(deviceId, out var e))
            {
                e.Muted = muted;
                var later = DateTimeOffset.Now + ReadAfterWrite;
                if (e.NextRead < later) e.NextRead = later;
            }
        }
        Publish();
        return null;
    }

    // ---- Pure helpers (tested) ----

    /// <summary>DDC/CI only works over a real video cable to an external monitor.</summary>
    internal static bool UsesDdc(ConnectionType c) =>
        c is ConnectionType.DisplayPort or ConnectionType.Hdmi or ConnectionType.UsbC or ConnectionType.Dvi or ConnectionType.Vga or ConnectionType.Wired;

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY → badge. USB-C usually reports as DisplayPort (alt mode); only USB4/Thunderbolt tunnels say so.</summary>
    internal static ConnectionType ConnectionFor(uint outputTechnology, bool createdBySoftware) => createdBySoftware
        ? ConnectionType.Virtual
        : outputTechnology switch
        {
            0 => ConnectionType.Vga,
            4 => ConnectionType.Dvi,
            5 => ConnectionType.Hdmi,
            10 => ConnectionType.DisplayPort,
            18 => ConnectionType.UsbC,                    // DisplayPort tunnelled over USB4/Thunderbolt
            15 => ConnectionType.Wireless,                // Miracast
            17 => ConnectionType.Virtual,                 // indirect display driver, virtual
            6 or 11 or 13 or 0x80000000 => ConnectionType.BuiltIn, // LVDS, embedded DisplayPort/UDI, internal
            _ => ConnectionType.Wired,                    // other cables, DisplayLink-style USB adapters (16)
        };

    /// <summary>"ACR0750" from DISPLAY\ACR0750\7&amp;315967f&amp;1&amp;UID512.</summary>
    internal static string? PnpId(string instanceId)
    {
        var parts = instanceId.Split('\\');
        return parts.Length >= 2 && parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) && parts[1].Length == 7 ? parts[1].ToUpperInvariant() : null;
    }

    /// <summary>The monitor's serial from its EDID: the serial-number text descriptor, else the 32-bit serial if set.</summary>
    internal static string? EdidSerial(byte[] edid)
    {
        if (edid.Length < 128) return null;
        for (int at = 54; at <= 108; at += 18)
        {
            if (edid[at] != 0 || edid[at + 1] != 0 || edid[at + 3] != 0xFF) continue;
            var text = new string(edid.Skip(at + 5).Take(13).TakeWhile(b => b != 0x0A && b != 0).Select(b => (char)b).ToArray()).Trim();
            if (text.Length > 0 && text.Any(ch => ch != '0')) return new string(text.Where(char.IsLetterOrDigit).ToArray());
        }
        uint number = BitConverter.ToUInt32(edid, 12);
        return number is 0 or 0x01010101 ? null : number.ToString();
    }

    /// <summary>"Acer VG240Y S" from the EDID name "VG240Y S" and PnP id ACR…; the maker is added when we know it.</summary>
    internal static string NameFor(string? pnpId, string friendlyName, ConnectionType connection)
    {
        string? maker = pnpId is { Length: >= 3 } && Makers.TryGetValue(pnpId[..3], out var m) ? m : null;
        // A laptop's own screen: its EDID name, when it has one, is a panel part number ("LP140WF7"), so it's left out.
        if (connection == ConnectionType.BuiltIn) return "Built-in display";
        var name = friendlyName.Trim();
        if (name.Length == 0) return maker is null ? "Monitor" : $"{maker} monitor";
        return maker is null || name.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? name : $"{maker} {name}";
    }

    /// <summary>EDID manufacturer (PnP) ids of common monitor makers.</summary>
    private static readonly Dictionary<string, string> Makers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer", ["ACI"] = "ASUS", ["AUS"] = "ASUS", ["AOC"] = "AOC", ["BNQ"] = "BenQ", ["DEL"] = "Dell",
        ["ENC"] = "EIZO", ["GBT"] = "Gigabyte", ["GSM"] = "LG", ["HPN"] = "HP", ["HWP"] = "HP", ["IVM"] = "iiyama",
        ["LEN"] = "Lenovo", ["MSI"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips", ["SAM"] = "Samsung", ["SNY"] = "Sony",
        ["VSC"] = "ViewSonic", ["XMI"] = "Xiaomi", ["HKC"] = "HKC", ["KTC"] = "KTC", ["MEI"] = "Panasonic", ["SHP"] = "Sharp",
        ["APP"] = "Apple", ["HSD"] = "HannStar", ["AOU"] = "AOC",
    };

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
    }
}
