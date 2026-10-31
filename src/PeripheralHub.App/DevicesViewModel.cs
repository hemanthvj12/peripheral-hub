using System.Collections.ObjectModel;

namespace PeripheralHub.App;

/// <summary>
/// Keeps one <see cref="DeviceViewModel"/> per device in sync with the hub, on the UI thread. The panel and the widget
/// show the same list. Pinned devices are "kept": they stay on the list while disconnected and across restarts, until
/// unpinned or unpaired in Windows (<see cref="KeptDevices"/>).
/// </summary>
public sealed class DevicesViewModel
{
    private readonly AppSettings _settings;
    private readonly KeptDevices _kept;
    private IReadOnlyList<DeviceSnapshot> _live = [];

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    /// <summary>Raised after any change (device data or pins).</summary>
    public event Action? Changed;

    public DevicesViewModel(AppSettings settings)
    {
        _settings = settings;
        _kept = new KeptDevices(settings.Kept);
        Refresh(); // kept devices show right away, before the hub's first scan
    }

    /// <summary>
    /// A Sony controller (DualSense, Edge, DualShock 4) is paired over Bluetooth in Windows, connected or not: the menu's
    /// "Show Sony controller battery over Bluetooth" switch only matters then, so it's only shown then (owner, 2026-10-29).
    /// </summary>
    public bool SonyControllerPaired { get; private set; }

    private static readonly HashSet<ushort> SonyControllerPids = [0x0CE6, 0x0DF2, 0x05C4, 0x09CC];

    public void Apply(IReadOnlyList<DeviceSnapshot> snapshots, IReadOnlyDictionary<string, PairedDevice>? pairedBluetooth)
    {
        _live = snapshots;
        if (pairedBluetooth is not null)
            SonyControllerPaired = pairedBluetooth.Values.Any(p => p.VendorId == 0x054C && p.ProductId is { } pid && SonyControllerPids.Contains(pid));
        Refresh(pairedBluetooth);
    }

    /// <param name="paired">Only passed when the hub sent a fresh paired list, so "missing" counts advance once per scan.</param>
    private void Refresh(IReadOnlyDictionary<string, PairedDevice>? paired = null)
    {
        // Virtual controllers can't be kept; drop pins left from earlier versions.
        bool unpinned = false;
        foreach (var id in _live.Where(s => s.Connection == ConnectionType.Virtual).Select(s => s.Id)
                     .Concat(_settings.Kept.Keys.Where(k => k.StartsWith("sony:virtual-", StringComparison.Ordinal))).ToList())
            unpinned |= _kept.Release(id);

        var snapshots = _kept.Merge(_live, paired, out bool keptChanged);
        if (!_settings.ShowVirtualControllers)
            snapshots = snapshots.Where(s => s.Connection != ConnectionType.Virtual).ToList();

        // A device that disconnects (or drops off the list) folds its drawer and stays folded when it comes back
        // (owner, 2026-10-28). Only devices seen connected this run count, so a restart keeps drawers left open.
        var present = snapshots.Where(s => s.State != LinkState.Disconnected).Select(s => s.Id).ToHashSet();
        _seenConnected.UnionWith(present);
        bool folded = false;
        foreach (var id in _settings.NoiseExpanded.Where(id => _seenConnected.Contains(id) && !present.Contains(id)).ToList())
        {
            _settings.NoiseExpanded.Remove(id);
            folded = true;
        }

        snapshots = KeepLeavingTiles(snapshots);

        var byId = Devices.ToDictionary(d => d.Id);
        bool remembered = keptChanged || unpinned || folded;
        for (int i = 0; i < snapshots.Count; i++)
        {
            var s = RememberBattery(snapshots[i], ref remembered);
            if (byId.Remove(s.Id, out var vm))
            {
                vm.Update(s);
                int at = Devices.IndexOf(vm);
                if (at != i) Devices.Move(at, i);
            }
            else
            {
                // Devices found in the first seconds (the startup scan) just appear; later ones grow in.
                vm = new DeviceViewModel(s) { AddedAt = _started.Elapsed > TimeSpan.FromSeconds(5) ? DateTime.UtcNow : null };
                Devices.Insert(i, vm);
            }
            vm.IsPinned = _kept.IsKept(s.Id);
            vm.IsExpanded = _settings.NoiseExpanded.Contains(s.Id);
            // Online but no level: charging (F75: the stored level is the last one before the cable went in), or a
            // level that can't be read right now (a Bluetooth PlayStation controller left in basic reports).
            vm.LevelBeforeCharging = s is { State: LinkState.Online, BatteryPercent: null }
                                     && (s.Charging == ChargeState.Charging || s.BatteryUnavailable)
                                     && _settings.LastBattery.TryGetValue(s.Id, out var was) ? was : null;
        }
        foreach (var gone in byId.Values)
        {
            Devices.Remove(gone);
            _leaving.Remove(gone.Id);
        }
        if (remembered) _settings.Save();
        if (folded) WatchedChanged?.Invoke(WatchedIds);
        Changed?.Invoke();
    }

    // ---- Tiles leaving the list shrink away first (owner, 2026-10-28) ----

    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(220);
    private readonly Dictionary<string, DateTime> _leaving = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A device dropping off the list stays (at its place, marked <see cref="DeviceViewModel.IsLeaving"/>, so its tiles
    /// shrink away) for <see cref="LeaveDuration"/>, then a refresh removes it. Coming back in the meantime cancels it.
    /// </summary>
    private List<DeviceSnapshot> KeepLeavingTiles(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        var ids = snapshots.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = snapshots.ToList();
        for (int k = 0; k < Devices.Count; k++)
        {
            var d = Devices[k];
            if (ids.Contains(d.Id))
            {
                if (_leaving.Remove(d.Id)) d.IsLeaving = false; // came back
                continue;
            }
            if (_leaving.TryGetValue(d.Id, out var since))
            {
                if (DateTime.UtcNow - since >= LeaveDuration) continue; // done: let the refresh remove it
            }
            else
            {
                _leaving[d.Id] = DateTime.UtcNow;
                d.IsLeaving = true;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = LeaveDuration + TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) => { timer.Stop(); Refresh(); };
                timer.Start();
            }
            list.Insert(Math.Min(k, list.Count), d.Snapshot);
        }
        return list;
    }

    private readonly HashSet<string> _seenConnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Diagnostics.Stopwatch _started = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Stores each live battery reading, and fills in the stored one when a device isn't online and has no reading
    /// yet (e.g. the app started while the mouse was asleep), so the tile keeps showing a level, greyed out.
    /// </summary>
    private DeviceSnapshot RememberBattery(DeviceSnapshot s, ref bool changed)
    {
        if (s.BatteryPercent is { } p)
        {
            if (s.State == LinkState.Online && (!_settings.LastBattery.TryGetValue(s.Id, out var old) || old != p))
            {
                _settings.LastBattery[s.Id] = p;
                changed = true;
            }
            return s;
        }
        return s.State != LinkState.Online && _settings.LastBattery.TryGetValue(s.Id, out var last)
            ? s with { BatteryPercent = last }
            : s;
    }

    public bool ShowVirtualControllers
    {
        get => _settings.ShowVirtualControllers;
        set
        {
            if (_settings.ShowVirtualControllers == value) return;
            _settings.ShowVirtualControllers = value;
            _settings.Save();
            Refresh();
        }
    }


    /// <summary>Opens or folds away a device's drawer (remembered; panel and widget follow each other).</summary>
    public void ToggleExpanded(DeviceViewModel vm)
    {
        if (!_settings.NoiseExpanded.Remove(vm.Id)) _settings.NoiseExpanded.Add(vm.Id);
        _settings.Save();
        vm.IsExpanded = _settings.NoiseExpanded.Contains(vm.Id);
        WatchedChanged?.Invoke(WatchedIds);
    }

    /// <summary>Devices whose controls are open: the hub follows them live (e.g. a mode changed from the phone shows at once).</summary>
    public IReadOnlyCollection<string> WatchedIds => [.. _settings.NoiseExpanded];
    public event Action<IReadOnlyCollection<string>>? WatchedChanged;

    /// <summary>Pin = keep on the list while disconnected and after restarts. Unpinning a disconnected device removes it.</summary>
    public void TogglePin(DeviceViewModel vm)
    {
        if (!_kept.Release(vm.Id)) _kept.Keep(vm.Snapshot);
        _settings.Save();
        Refresh();
    }
}
