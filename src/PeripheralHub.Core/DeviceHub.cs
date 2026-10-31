using PeripheralHub.Devices;
using PeripheralHub.Plugins;
using PeripheralHub.Transport;

namespace PeripheralHub;

/// <summary>A device paired in Windows: its name, its kind when Windows' category says, and its USB-style IDs when known.</summary>
public sealed record PairedDevice(string? Name, DeviceKind? Kind, ushort? VendorId = null, ushort? ProductId = null);

public sealed class DevicesChangedEventArgs(IReadOnlyList<DeviceSnapshot> devices, IReadOnlyList<DeviceSnapshot> changed,
    IReadOnlyDictionary<string, PairedDevice>? pairedBluetooth = null) : EventArgs
{
    /// <summary>The full current list.</summary>
    public IReadOnlyList<DeviceSnapshot> Devices { get; } = devices;
    /// <summary>Only the devices that were added or changed since the last event.</summary>
    public IReadOnlyList<DeviceSnapshot> Changed { get; } = changed;
    /// <summary>
    /// Bluetooth addresses (12 lower-case hex digits) of every device paired in Windows, connected or not, from the
    /// latest scan, with Windows' name and kind. Set on exactly one update per scan (null on the others), so a device can be
    /// judged unpaired ("forgotten") only after missing from several scans in a row.
    /// </summary>
    public IReadOnlyDictionary<string, PairedDevice>? PairedBluetooth { get; } = pairedBluetooth;
}

/// <summary>
/// Entry point of the data layer. Start it once; read <see cref="Devices"/> or subscribe to <see cref="DevicesChanged"/>.
/// </summary>
public sealed class DeviceHub : IDisposable
{
    private readonly PeripheralHubOptions _options;
    // Drivers of active plugins (grows as plugins are turned on; replaced, never mutated), then the generic Bluetooth driver.
    private volatile IReadOnlyList<IDeviceDriver> _pluginDrivers = [];
    private readonly IReadOnlyList<IDeviceDriver> _otherDrivers;
    private readonly ActivatedPlugins? _activated;
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HidEnumerator _hid = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _publishLock = new();
    private IReadOnlyList<DeviceSnapshot> _devices = [];
    private Dictionary<string, PairedDevice>? _scanPaired; // paired list from the latest scan, until published (loop thread only)
    private Task? _loop;

    /// <param name="pluginDirectories">Where to look for plugins; defaults to <see cref="PluginLoader.DefaultDirectories"/>.</param>
    public DeviceHub(PeripheralHubOptions? options = null, IEnumerable<string>? pluginDirectories = null)
        : this(options ?? new PeripheralHubOptions(), pluginDirectories ?? PluginLoader.DefaultDirectories, ActivatedPlugins.DefaultPath) { }

    internal DeviceHub(PeripheralHubOptions options, IEnumerable<string> pluginDirectories, string activatedPluginsPath)
    {
        _options = options;
        var (available, errors) = PluginLoader.ReadManifests(pluginDirectories);
        Plugins = new PluginSet(available, errors);
        foreach (var e in errors) _options.Log?.Invoke($"Plugin in {e.Directory} skipped: {e.Message}");

        // Plugins stay dormant (no code loaded) until their device shows up; ones seen before start right away.
        _activated = new ActivatedPlugins(activatedPluginsPath);
        var remembered = _activated.Load();
        foreach (var p in available.Where(p => remembered.Contains(p.Manifest.Id))) Activate(p, "turned on earlier");
        // Plugins whose devices the scan can't see (e.g. Xbox controllers via XInput) start with the app and look themselves.
        foreach (var p in available.Where(p => p.Manifest.AlwaysOn && !Plugins.IsActive(p.Manifest.Id))) Activate(p, "always on");

        // Last, so devices that no active plugin claims still show with Windows' Bluetooth battery level.
        _otherDrivers = [new GenericBluetoothDriver(Plugins.ClaimsBluetooth)];
        foreach (var d in _otherDrivers) d.Changed += Signal;
    }

    internal DeviceHub(PeripheralHubOptions options, IReadOnlyList<IDeviceDriver> drivers)
    {
        _options = options;
        Plugins = PluginSet.Empty;
        _pluginDrivers = drivers;
        _otherDrivers = [];
        foreach (var d in drivers) d.Changed += Signal;
    }

    /// <summary>
    /// Plugins found on disk, which of them are active (their device was detected), and any that couldn't be used.
    /// Also answers artwork and vendor-app lookups.
    /// </summary>
    public PluginSet Plugins { get; }

    /// <summary>Raised (on the hub's thread) when a plugin is turned on because its device was detected.</summary>
    public event Action<LoadedPlugin>? PluginActivated;

    /// <summary>Turns on every dormant plugin with a device in this scan. Runs before the drivers see the scan.</summary>
    internal void ActivateDetected(ScanContext context)
    {
        bool added = false;
        foreach (var p in Plugins.Available)
        {
            if (Plugins.IsActive(p.Manifest.Id) || _failed.Contains(p.Manifest.Id)) continue;
            if (PluginMatcher.Detected(p.Manifest, context) is not { } device) continue;
            if (Activate(p, $"detected {device}") is { } loaded)
            {
                added = true;
                try { PluginActivated?.Invoke(loaded); }
                catch (Exception ex) { _options.Log?.Invoke($"PluginActivated handler threw: {ex}"); }
            }
        }
        if (added && _activated?.Save(Plugins.Active.Select(p => p.Manifest.Id)) is { } error)
            _options.Log?.Invoke($"Couldn't remember active plugins in {_activated.FilePath}: {error}");
    }

    private LoadedPlugin? Activate(LoadedPlugin p, string reason)
    {
        try
        {
            var loaded = PluginLoader.LoadCode(p);
            var drivers = loaded.Plugin!.CreateDrivers(new PluginContext(_options, p.Manifest, p.Directory)).ToList();
            foreach (var d in drivers) d.Changed += Signal;
            _pluginDrivers = [.. _pluginDrivers, .. drivers];
            Plugins.MarkActive(loaded);
            _options.Log?.Invoke($"Plugin {p.Manifest.Id} {p.Manifest.Version} turned on ({reason})");
            return loaded;
        }
        catch (Exception ex)
        {
            _failed.Add(p.Manifest.Id); // don't retry every scan; a restart tries again
            Plugins.AddError(new PluginError(p.Directory, $"{p.Manifest.Id} failed to start: {ex.Message}"));
            _options.Log?.Invoke($"Plugin {p.Manifest.Id} failed to start: {ex}");
            return null;
        }
    }

    /// <summary>Latest snapshot of every known device.</summary>
    public IReadOnlyList<DeviceSnapshot> Devices => Volatile.Read(ref _devices);

    /// <summary>Raised on a background thread whenever any device's visible state changes.</summary>
    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private IReadOnlySet<string> _watched = new HashSet<string>();

    /// <summary>
    /// Devices the user is looking at closely (e.g. tiles with their controls open): drivers that support it
    /// (<see cref="IWatchableDriver"/>) then follow them live. Applied now and again at every scan (plugins turned on later).
    /// </summary>
    public void SetWatched(IEnumerable<string> deviceIds)
    {
        var set = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _watched, set);
        IDeviceDriver[] drivers;
        while (true)
        {
            try { drivers = [.. _pluginDrivers, .. _otherDrivers]; break; }
            catch (InvalidOperationException) { }
        }
        foreach (var d in drivers.OfType<IWatchableDriver>())
        {
            try { d.SetWatched(set); }
            catch (Exception ex) { _options.Log?.Invoke($"{d.GetType().Name}.SetWatched failed: {ex}"); }
        }
    }

    /// <summary>Changes a device's noise control through the driver that reports it (see <see cref="INoiseControlDriver"/>).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    public async Task<string?> SetNoiseControlAsync(string deviceId, NoiseMode mode, NoiseStrength? strength)
    {
        if (DriverOf<INoiseControlDriver>(deviceId) is not { } nc) return "This device can't change noise control.";
        try
        {
            var error = await nc.SetNoiseControlAsync(deviceId, mode, strength, _cts.Token).ConfigureAwait(false);
            _options.Log?.Invoke($"Noise control {deviceId} → {mode}{(strength is { } s ? $" ({s})" : "")}: {error ?? "done"}");
            Signal();
            return error;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Noise control {deviceId} failed: {ex}");
            return "Couldn't change it.";
        }
    }

    /// <summary>Changes a monitor's brightness or volume (0-100) through its driver (see <see cref="IMonitorControlDriver"/>).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    public async Task<string?> SetMonitorLevelAsync(string deviceId, MonitorSetting setting, int percent)
    {
        if (DriverOf<IMonitorControlDriver>(deviceId) is not { } mc) return "This monitor can't change that.";
        try
        {
            var error = await mc.SetMonitorLevelAsync(deviceId, setting, percent, _cts.Token).ConfigureAwait(false);
            if (error is not null) _options.Log?.Invoke($"Monitor {deviceId} {setting} → {percent}: {error}");
            Signal();
            return error;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Monitor {deviceId} {setting} failed: {ex}");
            return "Couldn't change it.";
        }
    }

    /// <summary>Mutes or unmutes a monitor's speakers through its driver (see <see cref="IMonitorControlDriver"/>).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    public async Task<string?> SetMonitorMuteAsync(string deviceId, bool muted)
    {
        if (DriverOf<IMonitorControlDriver>(deviceId) is not { } mc) return "This monitor can't mute.";
        try
        {
            var error = await mc.SetMonitorMuteAsync(deviceId, muted, _cts.Token).ConfigureAwait(false);
            if (error is not null) _options.Log?.Invoke($"Monitor {deviceId} mute → {muted}: {error}");
            Signal();
            return error;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Monitor {deviceId} mute failed: {ex}");
            return "Couldn't change it.";
        }
    }

    /// <summary>Makes a sound device (USB headset) Windows' default output and mic (see <see cref="ISoundDeviceDriver"/>).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    public async Task<string?> MakeDefaultSoundDeviceAsync(string deviceId)
    {
        if (DriverOf<ISoundDeviceDriver>(deviceId) is not { } sd) return "This device isn't a sound device.";
        try
        {
            var error = await sd.MakeDefaultSoundDeviceAsync(deviceId, _cts.Token).ConfigureAwait(false);
            _options.Log?.Invoke($"Default sound device → {deviceId}: {error ?? "done"}");
            Signal();
            return error;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Default sound device {deviceId} failed: {ex}");
            return "Couldn't change it.";
        }
    }

    /// <summary>The driver reporting <paramref name="deviceId"/>, if it supports <typeparamref name="T"/>.</summary>
    private T? DriverOf<T>(string deviceId) where T : class
    {
        var now = DateTimeOffset.Now;
        IDeviceDriver[] drivers;
        while (true)
        {
            // The list only grows (a plugin turning on, on the scan thread); retry if it changed while copying.
            try { drivers = [.. _pluginDrivers, .. _otherDrivers]; break; }
            catch (InvalidOperationException) { }
        }
        foreach (var d in drivers)
        {
            if (d is not T wanted) continue;
            try { if (d.GetSnapshots(now).Any(s => s.Id == deviceId)) return wanted; }
            catch (Exception) { }
        }
        return null;
    }

    private void Signal()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var nextScan = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            if (now >= nextScan)
            {
                try { Scan(now); }
                catch (Exception ex) { _options.Log?.Invoke($"Scan failed: {ex}"); }
                nextScan = now + _options.ScanInterval;
            }
            Publish();

            var wait = nextScan - DateTimeOffset.Now;
            try { if (wait > TimeSpan.Zero) await _signal.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            // Coalesce bursts of changes (e.g. several sessions reporting at once).
            try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private void Scan(DateTimeOffset now)
    {
        var context = new ScanContext(_hid.Enumerate(), BluetoothDevices.EnumerateLe(),
            BluetoothDevices.EnumerateClassic(), now);

        // Windows' paired Bluetooth devices (connected or not): UIs drop devices that get unpaired ("forgotten").
        var paired = new Dictionary<string, PairedDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in context.BluetoothLe) paired.TryAdd(d.Mac.ToLowerInvariant(), new PairedDevice(d.Name, GenericBluetoothDriver.KindFor(d.Category), d.VendorId, d.ProductId));
        foreach (var d in context.BluetoothClassic) paired.TryAdd(d.Mac.ToLowerInvariant(), new PairedDevice(d.Name, GenericBluetoothDriver.KindFor(d.Category), d.VendorId, d.ProductId));
        // Sent with exactly one update per scan, so UIs can count "missing for N scans" (a single odd enumeration
        // shouldn't count as unpaired).
        _scanPaired = paired;

        ActivateDetected(context);
        var watched = Volatile.Read(ref _watched);
        foreach (var d in _pluginDrivers.Concat(_otherDrivers))
        {
            try
            {
                if (d is IWatchableDriver w) w.SetWatched(watched);
                d.Update(context);
            }
            catch (Exception ex) { _options.Log?.Invoke($"{d.GetType().Name}.Update failed: {ex}"); }
        }
    }

    private void Publish()
    {
        DevicesChangedEventArgs? args = null;
        lock (_publishLock)
        {
            var now = DateTimeOffset.Now;
            IEnumerable<DeviceSnapshot> From(IEnumerable<IDeviceDriver> drivers) => drivers.SelectMany(d =>
            {
                try { return d.GetSnapshots(now); }
                catch (Exception ex) { _options.Log?.Invoke($"{d.GetType().Name}.GetSnapshots failed: {ex}"); return []; }
            });
            // Grouped by type in DeviceOrder (monitors, keyboards, mice, audio, controllers). Within a type, plugin
            // devices come first, then other Bluetooth devices in the order they appeared (the sort is stable).
            var current = From(_pluginDrivers)
                .Concat(From(_otherDrivers))
                .OrderBy(s => DeviceOrder.Of(s.Kind))
                .ToList();

            var previous = _devices.ToDictionary(d => d.Id);
            var changed = current.Where(c => c.DiffersFrom(previous.GetValueOrDefault(c.Id))).ToList();
            bool removed = previous.Keys.Except(current.Select(c => c.Id)).Any();

            Volatile.Write(ref _devices, current);
            if (changed.Count > 0 || removed || _scanPaired is not null)
                args = new DevicesChangedEventArgs(current, changed, _scanPaired);
            _scanPaired = null;
        }
        if (args is not null)
        {
            try { DevicesChanged?.Invoke(this, args); }
            catch (Exception ex) { _options.Log?.Invoke($"DevicesChanged handler threw: {ex}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        foreach (var d in _pluginDrivers.Concat(_otherDrivers)) d.Dispose();
        _cts.Dispose();
    }
}
