namespace PeripheralHub;

public sealed class PeripheralHubOptions
{
    /// <summary>How often to look for plugged/unplugged/connected devices.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often to ask a receiver whether its (online) device is still reachable.</summary>
    public TimeSpan OnlinePollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to ask while the device is asleep, so waking up shows quickly. The receiver answers this
    /// by itself (it doesn't wake the device), so polling fast costs nothing on the device's battery.
    /// The Kreo announces nothing when it wakes, so this is what bounds its wake-up delay.
    /// </summary>
    public TimeSpan AsleepPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often to re-read battery from mice/keyboards that are reachable.</summary>
    public TimeSpan BatteryPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A controller that sends no input for this long is treated as disconnected.</summary>
    public TimeSpan ControllerSilenceTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// A receiver device that hasn't answered for this long changes from <see cref="LinkState.Asleep"/> to <see cref="LinkState.Offline"/>.
    /// (Measured: the Kreo sleeps ~60 s after the last movement, the F75 after ~5 min idle.)
    /// </summary>
    public TimeSpan OfflineAfter { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Whether Bluetooth PlayStation controllers may be switched to full reports to read their battery. The switch
    /// lasts until the controller reconnects, and meanwhile apps that read it as a plain gamepad (browsers,
    /// DirectInput games) get no input. False: battery is read only when another app (Steam, DSX, a game) already
    /// switched it. Changeable while running (menu switch "Show Sony controller battery").
    /// </summary>
    public bool SwitchBluetoothControllers { get; set; } = true;

    /// <summary>Optional diagnostic log sink.</summary>
    public Action<string>? Log { get; init; }
}
