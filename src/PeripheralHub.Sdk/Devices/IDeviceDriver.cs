using PeripheralHub.Transport;

namespace PeripheralHub.Devices;

/// <summary>Everything one scan observed, handed to every driver.</summary>
public sealed record ScanContext(
    IReadOnlyList<HidDeviceInfo> Hid,
    IReadOnlyList<BleDeviceInfo> BluetoothLe,
    IReadOnlyList<ClassicBtDeviceInfo> BluetoothClassic,
    DateTimeOffset Now)
{
    /// <summary>Classic Bluetooth devices by MAC: connected or not.</summary>
    public IReadOnlyDictionary<string, bool> BluetoothClassicConnected { get; } =
        BluetoothClassic.GroupBy(d => d.Mac, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(d => d.Connected), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Supports one product family. To add a new peripheral, implement this in a plugin and return it from
/// <see cref="Plugins.IDevicePlugin.CreateDrivers"/>.
/// </summary>
public interface IDeviceDriver : IDisposable
{
    /// <summary>Called after every scan: open sessions for new devices, drop removed ones.</summary>
    void Update(ScanContext context);

    /// <summary>Current view of this driver's devices (thread-safe).</summary>
    IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now);

    /// <summary>Raised from any thread when something a snapshot depends on changed.</summary>
    event Action? Changed;
}

/// <summary>
/// A driver that keeps a closer eye on devices the user is looking at (e.g. a tile with its controls open), for example
/// by holding a connection open to hear changes the device announces (a mode changed from the phone).
/// </summary>
public interface IWatchableDriver
{
    /// <summary>The device ids being watched now (all of them, replacing the previous set; may include other drivers' ids).</summary>
    void SetWatched(IReadOnlySet<string> deviceIds);
}

/// <summary>A driver whose devices can switch noise control (see <see cref="DeviceSnapshot.NoiseControl"/>).</summary>
public interface INoiseControlDriver
{
    /// <summary>
    /// Changes the noise control of device <paramref name="deviceId"/> (one of this driver's snapshots).
    /// <paramref name="strength"/> only applies to <see cref="NoiseMode.Cancellation"/>; null keeps the last one.
    /// </summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    Task<string?> SetNoiseControlAsync(string deviceId, NoiseMode mode, NoiseStrength? strength, CancellationToken ct = default);
}

/// <summary>A driver whose monitors can change settings over DDC/CI (see <see cref="DeviceSnapshot.Monitor"/>).</summary>
public interface IMonitorControlDriver
{
    /// <summary>Sets <paramref name="setting"/> of monitor <paramref name="deviceId"/> to <paramref name="percent"/> (0-100).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    Task<string?> SetMonitorLevelAsync(string deviceId, MonitorSetting setting, int percent, CancellationToken ct = default);

    /// <summary>Mutes or unmutes monitor <paramref name="deviceId"/>'s speakers (the volume level is kept).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    Task<string?> SetMonitorMuteAsync(string deviceId, bool muted, CancellationToken ct = default);
}

/// <summary>A driver whose devices are Windows sound devices (see <see cref="DeviceSnapshot.Sound"/>).</summary>
public interface ISoundDeviceDriver
{
    /// <summary>Makes device <paramref name="deviceId"/> Windows' default output and mic (for every use: apps, calls).</summary>
    /// <returns>Null on success, else a short message for the user.</returns>
    Task<string?> MakeDefaultSoundDeviceAsync(string deviceId, CancellationToken ct = default);
}
