namespace PeripheralHub;

/// <summary>What the UI remembers about a kept (pinned) device, so it can show it while the hub doesn't report it.</summary>
public sealed class KeptDevice
{
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public string? Model { get; set; }
    public string? BluetoothMac { get; set; }
    public int? BatteryPercent { get; set; }
    public List<KeptPart>? Parts { get; set; }
    /// <summary>Seen in Windows' paired Bluetooth list at least once; only then does leaving it mean "forgotten".</summary>
    public bool WasPaired { get; set; }
}

public sealed class KeptPart
{
    public string Label { get; set; } = "";
    public int? Percent { get; set; }
}

/// <summary>
/// "Pinned" devices (owner's design, 2026-10-28): a kept device stays on the list while it's disconnected and after a
/// restart, shown as Disconnected with its last levels, until the user unpins it or unpairs ("forgets") it in Windows.
/// Also hides any device that was unpaired, kept or not. Pure logic: the caller stores <see cref="Kept"/>.
/// </summary>
public sealed class KeptDevices(Dictionary<string, KeptDevice> kept)
{
    /// <summary>A device must be missing from the paired list this many updates in a row before it counts as forgotten,
    /// so one odd enumeration can't wipe the pins.</summary>
    public const int MissesBeforeForgotten = 2;

    private readonly Dictionary<string, int> _misses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenPaired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forgotten = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kept devices by id. The caller persists it after <see cref="Merge"/> reports a change.</summary>
    public Dictionary<string, KeptDevice> Kept { get; } = kept;

    public bool IsKept(string id) => Kept.ContainsKey(id);

    public void Keep(DeviceSnapshot s) { Kept[s.Id] = new KeptDevice(); Remember(Kept[s.Id], s); }

    public bool Release(string id) => Kept.Remove(id);

    /// <summary>
    /// The hub's devices with unpaired ones removed and kept-but-missing ones added (as Disconnected). A placeholder
    /// goes after the devices of its kind (mice, keyboards, controllers, audio: the hub's order), so a device keeps
    /// roughly the same place whether it's connected or not.
    /// </summary>
    /// <param name="paired">Windows' paired Bluetooth addresses, or null when not known yet (then nothing is "forgotten").</param>
    /// <param name="changed">True when <see cref="Kept"/> changed (details updated or a forgotten device removed).</param>
    public List<DeviceSnapshot> Merge(IReadOnlyList<DeviceSnapshot> live, IReadOnlyDictionary<string, PairedDevice>? paired,
        out bool changed)
    {
        changed = false;
        if (paired is not null)
        {
            changed |= FillFromWindows(paired);
            changed |= TrackPairing(live, paired);
        }

        // Unpinned devices leave the list as soon as they disconnect (owner, 2026-10-28); Asleep/Offline devices stay,
        // since their receiver is still there. Unpaired ("forgotten") devices go whether pinned or not.
        var result = live.Where(s => (s.BluetoothMac is null || !_forgotten.Contains(s.BluetoothMac)) &&
                                     (s.State != LinkState.Disconnected || Kept.ContainsKey(s.Id))).ToList();
        foreach (var s in result)
            if (Kept.TryGetValue(s.Id, out var k)) changed |= Remember(k, s);

        var present = result.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, k) in Kept)
        {
            if (present.Contains(id)) continue;
            int at = result.FindIndex(s => DeviceOrder.Of(s.Kind) > DeviceOrder.Of(k.Kind));
            result.Insert(at < 0 ? result.Count : at, Placeholder(id, k));
            present.Add(id);
        }
        return result;
    }

    /// <summary>Updates "seen paired" / "missing" counts; forgets devices that left the paired list.</summary>
    private bool TrackPairing(IReadOnlyList<DeviceSnapshot> live, IReadOnlyDictionary<string, PairedDevice> paired)
    {
        bool changed = false;
        var macs = live.Select(s => s.BluetoothMac).Concat(Kept.Values.Select(k => k.BluetoothMac))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var mac in macs)
        {
            if (paired.ContainsKey(mac))
            {
                _seenPaired.Add(mac);
                _misses.Remove(mac);
                _forgotten.Remove(mac); // paired again
                foreach (var k in Kept.Values.Where(k => Same(k.BluetoothMac, mac) && !k.WasPaired)) { k.WasPaired = true; changed = true; }
                continue;
            }
            bool knewIt = _seenPaired.Contains(mac) || Kept.Values.Any(k => Same(k.BluetoothMac, mac) && k.WasPaired);
            if (!knewIt) continue; // e.g. a controller only ever used on its cable
            if ((_misses[mac] = _misses.GetValueOrDefault(mac) + 1) < MissesBeforeForgotten) continue;

            _forgotten.Add(mac);
            foreach (var id in Kept.Where(kv => Same(kv.Value.BluetoothMac, mac)).Select(kv => kv.Key).ToList())
            {
                Kept.Remove(id);
                changed = true;
            }
        }
        return changed;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kept entries without details (pins carried over from before 2026-10-28, not seen since) borrow Windows' name and
    /// kind; the Bluetooth address comes from ids like "bt:&lt;mac&gt;" / "sony:&lt;mac&gt;".
    /// </summary>
    private bool FillFromWindows(IReadOnlyDictionary<string, PairedDevice> paired)
    {
        bool changed = false;
        foreach (var (id, k) in Kept)
        {
            if (k.BluetoothMac is null && MacFromId(id) is { } mac) { k.BluetoothMac = mac; changed = true; }
            bool noName = string.IsNullOrEmpty(k.Name) || k.Name == id;
            if (!noName || k.BluetoothMac is null || !paired.TryGetValue(k.BluetoothMac, out var p)) continue;
            if (!string.IsNullOrWhiteSpace(p.Name)) { k.Name = p.Name; changed = true; }
            var kind = id.StartsWith("sony:", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Controller : p.Kind;
            if (kind is { } kd && kd != k.Kind) { k.Kind = kd; changed = true; }
        }
        return changed;
    }

    internal static string? MacFromId(string id)
    {
        int colon = id.IndexOf(':');
        var tail = colon < 0 ? "" : id[(colon + 1)..];
        return tail.Length == 12 && tail.All(Uri.IsHexDigit) ? tail.ToLowerInvariant() : null;
    }

    /// <summary>Copies what a placeholder needs from a live snapshot; true if anything changed.</summary>
    private static bool Remember(KeptDevice k, DeviceSnapshot s)
    {
        bool changed = false;
        void Set<T>(T current, T value, Action<T> apply) { if (!EqualityComparer<T>.Default.Equals(current, value)) { apply(value); changed = true; } }

        Set(k.Name, s.Name, v => k.Name = v);
        Set(k.Kind, s.Kind, v => k.Kind = v);
        Set(k.Model, s.Model ?? k.Model, v => k.Model = v);
        Set(k.BluetoothMac, s.BluetoothMac ?? k.BluetoothMac, v => k.BluetoothMac = v);
        if (s.BatteryPercent is { } p) Set(k.BatteryPercent, p, v => k.BatteryPercent = v);
        if (s.Parts is { Count: > 0 } parts)
        {
            // Keep each part's last known level (a closed case reports nothing, so keep its older value).
            k.Parts ??= [];
            foreach (var part in parts)
            {
                var kp = k.Parts.FirstOrDefault(x => x.Label == part.Label);
                if (kp is null) { k.Parts.Add(kp = new KeptPart { Label = part.Label }); changed = true; }
                if (part.Percent is { } pct && kp.Percent != pct) { kp.Percent = pct; changed = true; }
            }
        }
        return changed;
    }

    private static DeviceSnapshot Placeholder(string id, KeptDevice k) =>
        new(id, k.Name, k.Kind, ConnectionType.None, LinkState.Disconnected, k.BatteryPercent, ChargeState.Unknown, null,
            Model: k.Model, BluetoothMac: k.BluetoothMac,
            Parts: k.Parts?.Select(p => new BatteryPart(p.Label, p.Percent, false, false)).ToList());
}
