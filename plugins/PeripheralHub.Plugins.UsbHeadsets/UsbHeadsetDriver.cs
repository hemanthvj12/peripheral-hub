using System.Text.RegularExpressions;
using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.UsbHeadsets;

/// <summary>A USB headset: Windows' speakers and mic endpoints of one physical USB device.</summary>
internal sealed record UsbHeadset(Guid Container, string Name, IReadOnlyList<string> OutputIds, IReadOnlyList<string> InputIds);

/// <summary>
/// USB headsets, from Windows' sound devices (checked every scan; cheap): a USB device that has both speakers and a
/// mic in Windows. Webcam mics (no speakers), USB DACs/speakers (no mic), Bluetooth headsets (other drivers show
/// them) and software devices (Steam Streaming, Punktfunk) don't count. No battery; the tile's drawer makes it
/// Windows' default output and mic (owner, 2026-10-29: the only control wanted; volume, mic mute and mic monitoring
/// were offered). Verified with a CB CryoCore (C-Media 0D8C:0012): speakers and mic share container
/// 36c59302-42e5-11f1-941f-806e6f6e6963, enumerator "USB", adapter name "4- CB CryoCore".
/// An unplugged headset shows as Disconnected for the rest of the run (the UI hides it unless pinned).
/// </summary>
internal sealed class UsbHeadsetDriver(PeripheralHubOptions options) : IDeviceDriver, ISoundDeviceDriver
{
    private sealed class Entry
    {
        public required string Id;
        public required UsbHeadset Headset;
        public bool Online;
        public bool IsDefault;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];
    private string _lastLog = "";

    public event Action? Changed;

    public void Update(ScanContext context)
    {
        var endpoints = CoreAudio.ActiveEndpoints();
        var (defaultOut, defaultIn) = CoreAudio.Defaults();
        var headsets = FindHeadsets(endpoints, GamepadContainers(context));

        var log = string.Join("; ", headsets.Select(h => $"{h.Name} ({h.Container})"));
        if (log != _lastLog)
        {
            _lastLog = log;
            options.Log?.Invoke($"USB headsets: {(log.Length == 0 ? "none" : log)}");
        }

        lock (_lock)
        {
            var online = new HashSet<string>();
            foreach (var h in headsets)
            {
                var id = $"usb-audio:{h.Container:N}";
                if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry { Id = id, Headset = h };
                e.Headset = h;
                e.Online = true;
                e.IsDefault = IsDefault(h, defaultOut, defaultIn);
                online.Add(id);
            }
            foreach (var e in _entries.Values) if (!online.Contains(e.Id)) e.Online = false;
        }
        Publish();
    }

    private void Publish()
    {
        bool changed;
        lock (_lock)
        {
            var list = _entries.Values.Select(ToSnapshot).ToList();
            changed = list.Count != _snapshots.Count || list.Where((s, i) => s.DiffersFrom(_snapshots[i])).Any();
            _snapshots = list;
        }
        if (changed) Changed?.Invoke();
    }

    private static DeviceSnapshot ToSnapshot(Entry e) => e.Online
        ? new DeviceSnapshot(e.Id, e.Headset.Name, DeviceKind.Audio, ConnectionType.Wired, LinkState.Online, null,
            ChargeState.Unknown, null,
            Sound: new SoundDevice(e.Headset.OutputIds.Count > 0, e.Headset.InputIds.Count > 0, e.IsDefault))
        : new DeviceSnapshot(e.Id, e.Headset.Name, DeviceKind.Audio, ConnectionType.None, LinkState.Disconnected, null,
            ChargeState.Unknown, null);

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock) return _snapshots;
    }

    public async Task<string?> MakeDefaultSoundDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        UsbHeadset headset;
        lock (_lock)
        {
            if (!_entries.TryGetValue(deviceId, out var e) || !e.Online) return "The headset isn't connected.";
            headset = e.Headset;
        }
        bool ok = await Task.Run(() => headset.OutputIds.Concat(headset.InputIds).Aggregate(true, (all, id) => CoreAudio.SetDefault(id) & all), ct)
            .ConfigureAwait(false);
        if (!ok) return "Windows didn't accept the change.";

        lock (_lock)
            if (_entries.TryGetValue(deviceId, out var e)) e.IsDefault = true; // shows at once; the next scan re-reads it
        Publish();
        return null;
    }

    public void Dispose() { }

    private readonly Dictionary<string, Guid?> _gamepadContainers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Containers of the wired game controllers present (HID usage Joystick/Gamepad), looked up once per controller.
    /// A DualSense or DualShock 4 on its cable is also a Windows sound device with speakers and a mic (its headphone
    /// jack), in the controller's container (owner's bug report 2026-10-29: they showed as "Wireless Controller" and
    /// "DualSense Wireless Controller" headsets).
    /// </summary>
    private HashSet<Guid> GamepadContainers(ScanContext context)
    {
        var result = new HashSet<Guid>();
        foreach (var h in context.Hid.Where(h => h.UsagePage == 0x01 && h.Usage is 0x04 or 0x05 && !h.IsBluetooth))
        {
            if (!_gamepadContainers.TryGetValue(h.Path, out var container))
                _gamepadContainers[h.Path] = container = Transport.DeviceTree.InstanceIdFromPath(h.Path) is { } id ? CoreAudio.ContainerOf(id) : null;
            if (container is { } c) result.Add(c);
        }
        return result;
    }

    // ---- Pure helpers (tested) ----

    /// <summary>
    /// USB devices with both speakers and a mic, in the order Windows lists them; game controllers (their containers in
    /// <paramref name="gamepadContainers"/>) don't count, nor does anything Windows calls a controller (an Xbox
    /// controller's headset adapter isn't HID).
    /// </summary>
    internal static List<UsbHeadset> FindHeadsets(IReadOnlyList<AudioEndpoint> endpoints, IReadOnlySet<Guid>? gamepadContainers = null) => endpoints
        .Where(e => e.Container != Guid.Empty && e.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase))
        .Where(e => gamepadContainers?.Contains(e.Container) != true && !e.AdapterName.Contains("Controller", StringComparison.OrdinalIgnoreCase))
        .GroupBy(e => e.Container)
        .Where(g => g.Any(e => e.IsOutput) && g.Any(e => !e.IsOutput))
        .Select(g => new UsbHeadset(g.Key, CleanName(g.First().AdapterName),
            g.Where(e => e.IsOutput).Select(e => e.Id).ToList(), g.Where(e => !e.IsOutput).Select(e => e.Id).ToList()))
        .ToList();

    /// <summary>"4- CB CryoCore" → "CB CryoCore": Windows numbers a device that was installed more than once.</summary>
    internal static string CleanName(string adapterName)
    {
        var name = Regex.Replace(adapterName.Trim(), @"^\d+\s*-\s*", "");
        return name.Length == 0 ? "USB headset" : name;
    }

    /// <summary>Default when Windows uses it for everything it has: its speakers for output (if any) and its mic for input (if any).</summary>
    internal static bool IsDefault(UsbHeadset h, string? defaultOutput, string? defaultInput) =>
        (h.OutputIds.Count == 0 || h.OutputIds.Contains(defaultOutput ?? "")) &&
        (h.InputIds.Count == 0 || h.InputIds.Contains(defaultInput ?? ""));
}
