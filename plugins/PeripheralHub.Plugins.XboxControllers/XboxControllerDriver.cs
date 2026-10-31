using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins.XboxControllers;

/// <summary>What one XInput slot is shown as.</summary>
/// <param name="Origin">The software that created it, for an emulated controller (DSX's Xbox 360 mode: "Nefarius").</param>
/// <param name="Guessed">Real and emulated Xbox devices are both present, so the slot↔device match is by order.</param>
public sealed record XboxSlotView(int Slot, ConnectionType Connection, int? BatteryPercent, string? BatteryLabel,
    VirtualOrigin? Origin, bool Guessed);

/// <summary>
/// Xbox controllers through XInput, polled every scan (cheap). XInput gives up to four slots, each with a battery
/// type (wired / batteries / unknown) and one of four levels; it doesn't say which device is in which slot, whether
/// a wireless one is on Bluetooth or the Xbox Wireless Adapter, or whether it's charging.
/// So: wired → "Wired", no battery. Wireless → "Bluetooth" when an Xbox controller is connected over Bluetooth, else
/// "2.4G" (the adapter); the level is shown by name (Empty/Low/Medium/Full), or as a percentage when Windows has the
/// controller's Bluetooth battery level and there's only one Bluetooth Xbox controller to match it to.
/// Emulated controllers (DSX's Xbox 360 mode hangs off "Nefarius Virtual Gamepad Emulation Bus G2") are found from the
/// XUSB device list with <see cref="DeviceTree.FindVirtualOrigin"/>.
/// Built 2026-10-28 without a real Xbox controller: only the emulated Xbox 360 controller has been seen (XInput slot 0,
/// battery type Wired, level Full; XUSB device USB\VID_045E&amp;PID_028E under ROOT\USB\0000).
/// </summary>
internal sealed class XboxControllerDriver(PeripheralHubOptions options) : IDeviceDriver
{
    public const string Model = "xbox";
    private const ushort MicrosoftVid = 0x045E;

    private readonly object _lock = new();
    private readonly Dictionary<int, (DeviceSnapshot Snapshot, DateTimeOffset SeenAt)> _known = [];
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];
    private string _lastXusb = "";
    private IReadOnlyList<VirtualOrigin?> _xusbOrigins = [];

    public event Action? Changed;

    public void Update(ScanContext context)
    {
        var slots = XInput.ConnectedSlots();

        // Which XUSB devices exist (and which are emulated) only changes on plug/unplug: re-read the tree only then.
        var xusb = XusbDevices.InstanceIds();
        var key = string.Join('|', xusb);
        if (key != _lastXusb)
        {
            _lastXusb = key;
            _xusbOrigins = xusb.Select(DeviceTree.FindVirtualOrigin).ToList();
            options.Log?.Invoke($"Xbox: XUSB devices {(xusb.Count == 0 ? "none" : string.Join(", ", xusb.Select((id, i) =>
                $"{id}{(_xusbOrigins[i] is { } o ? $" (virtual: {o.Name})" : "")}")))}");
        }

        var bluetoothXbox = context.BluetoothLe.Where(b => b.VendorId == MicrosoftVid && b.Connected && IsGamepad(b.Category)).ToList();
        bool bluetoothPresent = bluetoothXbox.Count > 0 ||
            context.Hid.Any(h => h.VendorId == MicrosoftVid && h.IsBluetooth && h.UsagePage == 0x01 && h.Usage == 0x05);
        int? bluetoothLevel = bluetoothXbox.Count == 1 ? bluetoothXbox[0].BatteryPercent : null;

        var views = Describe(slots, _xusbOrigins, bluetoothPresent, bluetoothLevel);
        bool changed;
        lock (_lock)
        {
            foreach (var v in views) _known[v.Slot] = (ToSnapshot(v, views, context.Now), context.Now);
            var online = views.Select(v => v.Slot).ToHashSet();
            var list = _known.OrderBy(k => k.Key).Select(k => online.Contains(k.Key)
                ? k.Value.Snapshot
                : k.Value.Snapshot with { Connection = ConnectionType.None, State = LinkState.Disconnected, Charging = ChargeState.Unknown })
                .ToList();
            changed = list.Count != _snapshots.Count || list.Where((s, i) => s.DiffersFrom(_snapshots[i])).Any();
            _snapshots = list;
        }
        if (changed) Changed?.Invoke();
    }

    private static bool IsGamepad(IReadOnlyList<string>? category) =>
        category?.Any(c => c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>Turns XInput slots into tiles. Pure, for tests.</summary>
    /// <param name="xusbOrigins">Present XUSB devices in Windows' order: null = real, else what emulates it.</param>
    internal static List<XboxSlotView> Describe(IReadOnlyList<XInputSlot> slots, IReadOnlyList<VirtualOrigin?> xusbOrigins,
        bool bluetoothXboxPresent, int? bluetoothBatteryPercent)
    {
        bool mixed = xusbOrigins.Any(o => o is null) && xusbOrigins.Any(o => o is not null);
        int next = 0;
        int wirelessCount = slots.Count(s => !s.IsWired);
        var result = new List<XboxSlotView>();
        foreach (var s in slots.OrderBy(s => s.Index))
        {
            // Cable and adapter controllers are XUSB devices; Bluetooth ones aren't (they come through HID).
            bool viaXusb = s.IsWired || !bluetoothXboxPresent;
            var origin = viaXusb && next < xusbOrigins.Count ? xusbOrigins[next++] : null;

            var connection = origin is not null ? ConnectionType.Virtual
                : s.IsWired ? ConnectionType.Wired
                : bluetoothXboxPresent ? ConnectionType.Bluetooth
                : ConnectionType.Dongle;

            int? percent = null;
            string? label = null;
            bool hasBattery = s.Type is XInputBatteryType.Alkaline or XInputBatteryType.NiMH or XInputBatteryType.Unknown;
            if (hasBattery && origin is null)
            {
                if (connection == ConnectionType.Bluetooth && bluetoothBatteryPercent is { } exact && wirelessCount == 1)
                    percent = exact;   // Windows' Bluetooth battery level: finer than XInput's four steps
                else
                {
                    percent = XInput.IconPercent(s.Level);
                    label = s.Level.ToString();
                }
            }
            result.Add(new XboxSlotView(s.Index, connection, percent, label, origin, mixed && viaXusb));
        }
        return result;
    }

    private static DeviceSnapshot ToSnapshot(XboxSlotView v, IReadOnlyList<XboxSlotView> all, DateTimeOffset now)
    {
        // Real controllers are numbered among real ones; emulated ones carry their creator in the badge instead.
        var real = all.Where(x => x.Origin is null).OrderBy(x => x.Slot).ToList();
        string name = v.Origin is null && real.Count > 1 ? $"Xbox Controller {real.IndexOf(v) + 1}" : "Xbox Controller";
        string? note = v.Origin is not null
            ? $"Virtual controller created by {(v.Origin.ShortCreator is { Length: > 0 } c ? c : "another app")}"
            : v.Connection == ConnectionType.Wired ? "On its cable: Xbox controllers don't report a battery level then"
            : v.BatteryLabel is not null ? "Xbox controllers report battery in four steps (Empty, Low, Medium, Full)"
            : null;
        if (v.Guessed) note = (note is null ? "" : note + ". ") + "Real and virtual Xbox controllers are both connected; which is which is a best guess";

        return new DeviceSnapshot($"xbox:slot{v.Slot}", name, DeviceKind.Controller, v.Connection, LinkState.Online,
            v.BatteryPercent, ChargeState.Unknown, v.BatteryPercent is null ? null : now, now,
            Note: note, Model: Model, Source: v.Origin?.ShortCreator is { Length: > 0 } s ? s : null, BatteryLabel: v.BatteryLabel);
    }

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock) return _snapshots;
    }

    public void Dispose() { }
}
