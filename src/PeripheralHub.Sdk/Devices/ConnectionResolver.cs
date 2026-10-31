using PeripheralHub.Transport;

namespace PeripheralHub.Devices;

/// <summary>What the cable path currently says about a device.</summary>
/// <param name="BatteryPercent">Null when the device has no trustworthy level on the cable (e.g. the F75, which pins it to 100).</param>
public sealed record WiredObservation(int? BatteryPercent, ChargeState Charging, int? VoltageMillivolts, DateTimeOffset? UpdatedAt, string? Note);

/// <summary>What a 2.4G receiver currently says about its device.</summary>
/// <param name="Online">Null until the receiver first answers.</param>
/// <param name="LastSeenAt">When the device last answered through this receiver; null if not since the session opened.</param>
/// <param name="WatchingSince">When we started talking to this receiver (used when the device was never seen).</param>
public sealed record DongleObservation(bool? Online, int? BatteryPercent, ChargeState Charging, int? VoltageMillivolts,
    DateTimeOffset? BatteryUpdatedAt, DateTimeOffset? LastSeenAt, DateTimeOffset WatchingSince, string? Note = null);

/// <summary>
/// Picks the one connection to show for a device that can be linked several ways at once.
/// Priority: cable &gt; receiver (online) &gt; Bluetooth (connected) &gt; receiver (asleep/offline) &gt; Bluetooth (paired only).
/// </summary>
public static class ConnectionResolver
{
    public static DeviceSnapshot? Resolve(string id, string name, DeviceKind kind,
        WiredObservation? wired, DongleObservation? dongle, BleDeviceInfo? ble,
        DateTimeOffset now, TimeSpan offlineAfter)
    {
        if (wired is not null)
            return new DeviceSnapshot(id, name, kind, ConnectionType.Wired, LinkState.Online,
                wired.BatteryPercent, wired.Charging, wired.UpdatedAt, now, wired.VoltageMillivolts, wired.Note);

        if (dongle is { Online: true })
            return new DeviceSnapshot(id, name, kind, ConnectionType.Dongle, LinkState.Online,
                dongle.BatteryPercent, dongle.Charging, dongle.BatteryUpdatedAt, dongle.LastSeenAt ?? now,
                dongle.VoltageMillivolts, dongle.Note);

        if (ble is { Connected: true })
            return new DeviceSnapshot(id, name, kind, ConnectionType.Bluetooth, LinkState.Online,
                ble.BatteryPercent, ChargeState.Unknown, ble.BatteryUpdatedAt, now);

        if (dongle is not null)
        {
            // Receiver plugged in, device not answering. The receiver can't tell asleep from switched off
            // (both just drop the link), so call it Asleep (the usual reason), and Offline once it has been quiet for a while.
            var silentSince = dongle.LastSeenAt ?? dongle.WatchingSince;
            bool offline = now - silentSince >= offlineAfter;
            return new DeviceSnapshot(id, name, kind, ConnectionType.Dongle,
                offline ? LinkState.Offline : LinkState.Asleep,
                dongle.BatteryPercent, ChargeState.Unknown, dongle.BatteryUpdatedAt, dongle.LastSeenAt,
                dongle.VoltageMillivolts);
        }

        if (ble is not null)
            // Paired but off: Windows' cached battery is stale, so don't show it.
            return new DeviceSnapshot(id, name, kind, ConnectionType.None, LinkState.Disconnected,
                null, ChargeState.Unknown, null);

        return null;
    }
}
