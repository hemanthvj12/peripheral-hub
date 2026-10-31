namespace PeripheralHub;

/// <summary>
/// What a device is. The values are stored in settings, so new kinds go at the end; the order devices are listed in is
/// <see cref="DeviceOrder"/>.
/// </summary>
public enum DeviceKind { Mouse, Keyboard, Controller, Audio, Monitor }

/// <summary>
/// The order device types are listed in (owner, 2026-10-29): monitors, keyboards, mice, audio (earphones, headsets,
/// speakers), controllers.
/// </summary>
public static class DeviceOrder
{
    public static int Of(DeviceKind kind) => kind switch
    {
        DeviceKind.Monitor => 0,
        DeviceKind.Keyboard => 1,
        DeviceKind.Mouse => 2,
        DeviceKind.Audio => 3,
        DeviceKind.Controller => 4,
        _ => 5,
    };
}

/// <summary>
/// How the device is currently linked to the PC. Virtual = a device created by software (game streaming, remapping)
/// rather than real hardware. DisplayPort to Wireless are monitors' video links (Wireless = Miracast, BuiltIn = a
/// laptop's own screen).
/// </summary>
public enum ConnectionType { None, Wired, Dongle, Bluetooth, Virtual, DisplayPort, Hdmi, UsbC, Dvi, Vga, BuiltIn, Wireless }

public enum LinkState
{
    /// <summary>Reachable right now.</summary>
    Online,
    /// <summary>
    /// Its receiver is plugged in but the device is not answering, usually because it is asleep (it wakes when touched).
    /// It may also have been switched off or moved to another mode: the receiver only knows the link dropped, not why.
    /// </summary>
    Asleep,
    /// <summary>Asleep for longer than <see cref="PeripheralHubOptions.OfflineAfter"/>; most likely switched off.</summary>
    Offline,
    /// <summary>Known (paired or seen earlier) but its receiver/cable/Bluetooth link is gone.</summary>
    Disconnected,
}

public enum ChargeState { Unknown, Discharging, Charging, Full, Error }

/// <summary>
/// One peripheral as the UI should show it. Immutable; a new snapshot is published whenever anything changes.
/// When <see cref="State"/> is not <see cref="LinkState.Online"/>, battery fields hold the last known reading (see <see cref="BatteryUpdatedAt"/>).
/// </summary>
/// <param name="Id">Stable identifier, e.g. "kreo-ikarus" or "sony:a1b2c3d4e5f6".</param>
/// <param name="BatteryPercent">
/// Null when there is no trustworthy level
/// (never read, Bluetooth-only device that is disconnected, or charging on a cable where the firmware pins the level to 100).
/// </param>
/// <param name="BatteryUpdatedAt">When the battery value was last read from the device.</param>
/// <param name="LastSeenAt">When the device last answered or sent data; null if it hasn't since the hub started.</param>
/// <param name="VoltageMillivolts">Raw battery voltage, for devices that report it (Kreo).</param>
/// <param name="Note">Short human-readable caveat, e.g. why the battery level is missing.</param>
/// <param name="Model">
/// Product model, e.g. "kreo-ikarus" or "dualsense". UIs look it up in the plugin manifests (artwork, the maker's app);
/// null (e.g. generic Bluetooth devices) means the UI shows an icon for the device kind.
/// </param>
/// <param name="Paused">
/// True while the device maker's app (<see cref="VendorApp"/>) is open: the hub stops sending requests so the two apps
/// don't collide, and only updates from replies it overhears.
/// </param>
/// <param name="Parts">
/// For devices with several batteries (earbuds: left, right, case), each one; null for single-battery devices.
/// <see cref="BatteryPercent"/> then holds a summary (e.g. the lower bud) for places that show one number.
/// </param>
/// <param name="BluetoothMac">
/// The device's Bluetooth address (12 lower-case hex digits) when it has one, so UIs can tell when it's unpaired
/// ("forgotten") in Windows (the hub publishes the paired list with each device update).
/// </param>
/// <param name="Source">
/// For a <see cref="ConnectionType.Virtual"/> device: the app that created it, e.g. "Punktfunk" (shown next to the badge).
/// </param>
/// <param name="NoiseControl">
/// For earbuds/headphones with switchable noise control: the current mode. The UI shows mode buttons and changes it
/// through <see cref="Devices.INoiseControlDriver"/> on the driver that produced this snapshot. Null when not supported
/// or not known yet.
/// </param>
/// <param name="BatteryLabel">
/// For devices that only report coarse levels (Xbox controllers via XInput: Empty/Low/Medium/Full): shown instead of
/// "N%". <see cref="BatteryPercent"/> then only sets how full the battery icon looks.
/// </param>
/// <param name="BatteryUnavailable">
/// The device is online but its level can't be read right now (a Bluetooth PlayStation controller left in basic
/// reports): the UI shows the last level it knows, greyed, as "was N%".
/// </param>
/// <param name="Monitor">
/// For monitors: resolution, refresh rate and the DDC/CI settings the UI can change through
/// <see cref="Devices.IMonitorControlDriver"/>. Monitors have no battery, so the battery fields stay empty.
/// </param>
/// <param name="Sound">
/// For a device that is one of Windows' sound devices (USB headsets): whether it's the default. The UI can make it
/// the default through <see cref="Devices.ISoundDeviceDriver"/>.
/// </param>
public sealed record DeviceSnapshot(
    string Id,
    string Name,
    DeviceKind Kind,
    ConnectionType Connection,
    LinkState State,
    int? BatteryPercent,
    ChargeState Charging,
    DateTimeOffset? BatteryUpdatedAt,
    DateTimeOffset? LastSeenAt = null,
    int? VoltageMillivolts = null,
    string? Note = null,
    string? Model = null,
    bool Paused = false,
    IReadOnlyList<BatteryPart>? Parts = null,
    string? BluetoothMac = null,
    string? Source = null,
    string? BatteryLabel = null,
    NoiseControl? NoiseControl = null,
    bool BatteryUnavailable = false,
    MonitorInfo? Monitor = null,
    SoundDevice? Sound = null)
{
    /// <summary>True when the visible content differs (timestamps ignored: they tick on every poll).</summary>
    public bool DiffersFrom(DeviceSnapshot? other) =>
        other is null || Normalize(this) != Normalize(other) || !SameParts(Parts, other.Parts);

    private static DeviceSnapshot Normalize(DeviceSnapshot s) => s with { BatteryUpdatedAt = null, LastSeenAt = null, Parts = null };

    private static bool SameParts(IReadOnlyList<BatteryPart>? a, IReadOnlyList<BatteryPart>? b) =>
        a is null || b is null ? a is null && b is null : a.SequenceEqual(b);
}

/// <summary>One battery of a multi-battery device, e.g. ("L", 80, Charging: false, Live: true).</summary>
/// <param name="Label">Short label for the tile: "L", "R", "Case".</param>
/// <param name="Percent">Null if never read.</param>
/// <param name="Live">False when this part wasn't in the latest reading (e.g. the case is closed): the value is the last known one.</param>
public sealed record BatteryPart(string Label, int? Percent, bool Charging, bool Live);
