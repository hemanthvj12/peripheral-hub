using PeripheralHub.Transport;

namespace PeripheralHub.Devices;

/// <summary>
/// Shared plumbing for a single mouse/keyboard model that may be on a cable, a 2.4G receiver and/or Bluetooth LE:
/// keeps one polled session per vendor HID collection and combines everything through <see cref="ConnectionResolver"/>.
/// </summary>
public abstract class ReceiverDeviceDriver<TSession> : IDeviceDriver where TSession : PolledHidSession
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openFailed = new(StringComparer.OrdinalIgnoreCase);
    private BleDeviceInfo? _ble;
    private bool _wiredPresent;

    protected PeripheralHubOptions Options { get; }
    /// <summary>The device maker's app (from the plugin manifest). While it runs, sessions stop sending requests.</summary>
    protected VendorApp? VendorApp { get; }
    public event Action? Changed;

    protected ReceiverDeviceDriver(PeripheralHubOptions options, VendorApp? vendorApp)
    {
        Options = options;
        VendorApp = vendorApp;
    }

    protected abstract string Id { get; }
    protected abstract string Name { get; }
    protected abstract DeviceKind Kind { get; }

    /// <summary>Vendor collections this driver should open and poll.</summary>
    protected abstract bool IsVendorCollection(HidDeviceInfo hid);
    /// <summary>True if this vendor collection belongs to the cable connection (otherwise it is a receiver).</summary>
    protected abstract bool IsWiredCollection(HidDeviceInfo hid);
    /// <summary>Any collection proving the device is on its cable (for devices with no wired vendor protocol).</summary>
    protected virtual bool IsWiredPresence(HidDeviceInfo hid) => false;
    protected abstract bool IsBle(BleDeviceInfo ble);
    protected abstract TSession CreateSession(HidConnection connection);

    protected abstract WiredObservation? ObserveWired(TSession? wiredSession, bool wiredPresent);
    protected abstract DongleObservation ObserveDongle(TSession session);

    public void Update(ScanContext context)
    {
        bool changed = false;
        lock (_lock)
        {
            var wanted = context.Hid.Where(IsVendorCollection).ToDictionary(h => h.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, session) in _sessions.ToList())
            {
                if (wanted.ContainsKey(path) && !session.IsClosed) continue;
                session.Dispose();
                _sessions.Remove(path);
                changed = true;
            }
            foreach (var hid in wanted.Values)
            {
                if (_sessions.ContainsKey(hid.Path)) continue;
                var conn = HidConnection.TryOpen(hid);
                if (conn is null)
                {
                    // Retried every scan: log once, until it opens again.
                    if (_openFailed.Add(hid.Path)) Options.Log?.Invoke($"{Name}: cannot open {hid.Path}");
                    continue;
                }
                _openFailed.Remove(hid.Path);
                var session = CreateSession(conn);
                session.Changed += OnSessionChanged;
                _sessions[hid.Path] = session;
                session.Start();
                changed = true;
            }

            bool wiredPresent = context.Hid.Any(IsWiredPresence);
            var ble = context.BluetoothLe.FirstOrDefault(IsBle);
            if (wiredPresent != _wiredPresent || ble != _ble) changed = true;
            _wiredPresent = wiredPresent;
            _ble = ble;

            // While the maker's app is open, stop sending on the shared channel (sessions keep listening).
            bool vendorOpen = VendorApp?.IsRunning() ?? false;
            if (vendorOpen != _vendorOpen) changed = true;
            _vendorOpen = vendorOpen;
            foreach (var s in _sessions.Values) s.Paused = vendorOpen;
        }
        if (changed) Changed?.Invoke();
    }

    private bool _vendorOpen;

    private void OnSessionChanged() => Changed?.Invoke();

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock)
        {
            TSession? wired = null, dongle = null;
            foreach (var s in _sessions.Values)
            {
                if (s.IsClosed) continue;
                if (IsWiredCollection(s.Info)) wired ??= s; else dongle ??= s;
            }
            var snapshot = ConnectionResolver.Resolve(Id, Name, Kind,
                ObserveWired(wired, _wiredPresent),
                dongle is null ? null : ObserveDongle(dongle),
                _ble, now, Options.OfflineAfter);
            if (snapshot is null) return [];
            snapshot = snapshot with { Model = Id };
            // Paused only matters when we'd be talking to the device ourselves (cable or receiver session);
            // Bluetooth readings come from Windows and keep updating.
            bool usesSession = snapshot.Connection switch
            {
                ConnectionType.Wired => wired is not null,
                ConnectionType.Dongle => dongle is not null,
                _ => false,
            };
            if (_vendorOpen && usesSession)
                snapshot = snapshot with { Paused = true, Note = $"Paused while the {VendorApp!.DisplayName} is open" };
            return [snapshot];
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }
}
