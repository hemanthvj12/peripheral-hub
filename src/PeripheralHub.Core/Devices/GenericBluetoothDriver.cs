using PeripheralHub.Transport;

namespace PeripheralHub.Devices;

/// <summary>
/// Any other Bluetooth peripheral that reports a battery to Windows: earphones and headsets (hands-free battery),
/// and mice, keyboards and gamepads (LE Battery Service). No vendor code: everything comes from Windows' device
/// properties, so there is no charging state.
/// A device first appears once it is connected with a level (so old pairings never show up). After that it stays
/// until the app restarts, shown as Disconnected with its last level while away, like the controllers.
/// Devices a plugin claims in its manifest (e.g. the Kreo's Bluetooth ID) are left to that plugin, which knows more.
/// </summary>
/// <param name="claimedByPlugin">(vendor ID, product ID) → true if a plugin handles that Bluetooth device.</param>
internal sealed class GenericBluetoothDriver(Func<ushort, ushort?, bool>? claimedByPlugin = null) : IDeviceDriver
{
    private readonly Func<ushort, ushort?, bool> _claimedByPlugin = claimedByPlugin ?? ((_, _) => false);
    private readonly object _lock = new();
    // Devices seen connected during this run, in the order they first appeared.
    private readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];

    public event Action? Changed;

    private sealed class Known(string mac, string name, DeviceKind kind, int order)
    {
        public string Mac { get; } = mac;
        public string Name { get; set; } = name;
        public DeviceKind Kind { get; } = kind;
        public int Order { get; } = order;
        public int? Battery { get; set; }
        public DateTimeOffset? BatteryAt { get; set; }
        public DateTimeOffset? SeenAt { get; set; }
    }

    private sealed record Seen(string Mac, string? Name, bool Connected, int? Battery, DateTimeOffset? BatteryAt,
        IReadOnlyList<string>? Category);

    public void Update(ScanContext context)
    {
        var seen = context.BluetoothLe
            .Where(d => !HasOwnDriver(d.VendorId, d.ProductId))
            .Select(d => new Seen(d.Mac, d.Name, d.Connected, d.BatteryPercent, d.BatteryUpdatedAt, d.Category))
            .Concat(context.BluetoothClassic
                .Where(d => !(d.VendorId is { } vid && HasOwnDriver(vid, d.ProductId)))
                .Select(d => new Seen(d.Mac, d.Name, d.Connected, d.BatteryPercent, d.BatteryUpdatedAt, d.Category)))
            .ToList();

        bool changed;
        lock (_lock)
        {
            var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in seen)
            {
                // Paired-but-off devices keep a stale level, so only connected ones count.
                if (!d.Connected) continue;
                if (!_known.TryGetValue(d.Mac, out var k))
                {
                    if (d.Battery is null || KindFor(d.Category) is not { } kind) continue;
                    k = _known[d.Mac] = new Known(d.Mac, DisplayName(d.Name), kind, _known.Count);
                }
                k.Name = DisplayName(d.Name);
                if (d.Battery is { } level) { k.Battery = Math.Clamp(level, 0, 100); k.BatteryAt = d.BatteryAt; }
                k.SeenAt = context.Now;
                online.Add(d.Mac);
            }

            var list = _known.Values.OrderBy(k => k.Order).Select(k => online.Contains(k.Mac)
                    ? new DeviceSnapshot($"bt:{k.Mac}", k.Name, k.Kind, ConnectionType.Bluetooth, LinkState.Online,
                        k.Battery, ChargeState.Unknown, k.BatteryAt, k.SeenAt, BluetoothMac: k.Mac)
                    : new DeviceSnapshot($"bt:{k.Mac}", k.Name, k.Kind, ConnectionType.None, LinkState.Disconnected,
                        k.Battery, ChargeState.Unknown, k.BatteryAt, k.SeenAt, BluetoothMac: k.Mac))
                .ToList();
            changed = list.Count != _snapshots.Count || list.Where((s, i) => s.DiffersFrom(_snapshots[i])).Any();
            _snapshots = list;
        }
        if (changed) Changed?.Invoke();
    }

    private static string DisplayName(string? name) => string.IsNullOrWhiteSpace(name) ? "Bluetooth device" : name;

    /// <summary>Devices handled by their own drivers (which know more, e.g. charging and the receiver link).</summary>
    private bool HasOwnDriver(ushort vendorId, ushort? productId) => _claimedByPlugin(vendorId, productId);

    /// <summary>Maps Windows' device categories to a tile kind; null for anything that isn't a peripheral we show.</summary>
    internal static DeviceKind? KindFor(IReadOnlyList<string>? category)
    {
        if (category is null) return null;
        foreach (var c in category)
        {
            if (c.StartsWith("Input.Keyboard", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
            if (c.StartsWith("Input.Mouse", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Mouse;
            if (c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Controller;
            if (c.Contains("Headset", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("Earbud", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Audio;
        }
        return null;
    }

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock) return _snapshots;
    }

    public void Dispose() { }
}
