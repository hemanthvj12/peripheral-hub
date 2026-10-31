using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>Which connection wins when a mouse/keyboard is linked several ways, and what is shown for each.</summary>
public class ConnectionResolverTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 26, 2, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(10);

    private static DeviceSnapshot? Resolve(WiredObservation? wired = null, DongleObservation? dongle = null, BleDeviceInfo? ble = null,
        DateTimeOffset? now = null) =>
        ConnectionResolver.Resolve("dev", "Device", DeviceKind.Mouse, wired, dongle, ble, now ?? T, OfflineAfter);

    private static BleDeviceInfo Ble(bool connected, int? battery = 98) => new("c488bd9bc047", 0x3554, 0xF533, "Dev", connected, battery, T);

    /// <param name="lastSeenAgo">How long before <see cref="T"/> the device last answered; null = not since watching started.</param>
    private static DongleObservation Dongle(bool? online, int? battery = 80, TimeSpan? lastSeenAgo = null, TimeSpan? watchingFor = null) =>
        new(online, battery, ChargeState.Discharging, 4000, T,
            LastSeenAt: lastSeenAgo is { } a ? T - a : null,
            WatchingSince: T - (watchingFor ?? TimeSpan.FromHours(1)));

    [Fact]
    public void Nothing_present_means_no_device()
    {
        Assert.Null(Resolve());
    }

    [Fact]
    public void Cable_wins_over_everything_and_shows_its_level()
    {
        // Kreo on the cable: the receiver said 85%, the cable reports a real 90% while charging.
        var s = Resolve(new WiredObservation(90, ChargeState.Charging, 4159, T, null), Dongle(true, 85, lastSeenAgo: TimeSpan.Zero), Ble(true))!;
        Assert.Equal(ConnectionType.Wired, s.Connection);
        Assert.Equal(LinkState.Online, s.State);
        Assert.Equal(90, s.BatteryPercent);
        Assert.Equal(ChargeState.Charging, s.Charging);
        Assert.Equal(4159, s.VoltageMillivolts);
        Assert.Equal(T, s.LastSeenAt);
    }

    [Fact]
    public void Cable_without_a_trustworthy_level_shows_charging_only()
    {
        // F75 on the cable: no level over USB.
        var s = Resolve(new WiredObservation(null, ChargeState.Charging, null, null, "The F75 doesn't report its level over USB"))!;
        Assert.Equal(ConnectionType.Wired, s.Connection);
        Assert.Null(s.BatteryPercent);
        Assert.Equal(ChargeState.Charging, s.Charging);
    }

    [Fact]
    public void Online_receiver_wins_over_bluetooth()
    {
        var s = Resolve(dongle: Dongle(true, lastSeenAgo: TimeSpan.Zero), ble: Ble(true))!;
        Assert.Equal(ConnectionType.Dongle, s.Connection);
        Assert.Equal(80, s.BatteryPercent);
    }

    [Fact]
    public void Connected_bluetooth_wins_over_a_sleeping_receiver()
    {
        var s = Resolve(dongle: Dongle(false, lastSeenAgo: TimeSpan.FromMinutes(1)), ble: Ble(true))!;
        Assert.Equal(ConnectionType.Bluetooth, s.Connection);
        Assert.Equal(LinkState.Online, s.State);
        Assert.Equal(98, s.BatteryPercent);
    }

    [Fact]
    public void Recently_silent_device_is_asleep_with_last_known_battery()
    {
        var s = Resolve(dongle: Dongle(false, 64, lastSeenAgo: TimeSpan.FromMinutes(3)))!;
        Assert.Equal(LinkState.Asleep, s.State);
        Assert.Equal(64, s.BatteryPercent);
        Assert.Equal(ChargeState.Unknown, s.Charging);
        Assert.Equal(T - TimeSpan.FromMinutes(3), s.LastSeenAt);
        Assert.Null(s.Note);
    }

    [Fact]
    public void Device_silent_longer_than_the_threshold_is_offline()
    {
        var s = Resolve(dongle: Dongle(false, 64, lastSeenAgo: TimeSpan.FromMinutes(10)))!;
        Assert.Equal(LinkState.Offline, s.State);
        Assert.Equal(64, s.BatteryPercent);
        Assert.Null(s.Note);
    }

    [Fact]
    public void Device_never_seen_counts_from_when_watching_started()
    {
        Assert.Equal(LinkState.Asleep, Resolve(dongle: Dongle(null, null, watchingFor: TimeSpan.FromSeconds(5)))!.State);
        var later = Resolve(dongle: Dongle(false, null, watchingFor: TimeSpan.FromMinutes(11)))!;
        Assert.Equal(LinkState.Offline, later.State);
        Assert.Null(later.LastSeenAt);
    }

    [Fact]
    public void Asleep_becomes_offline_as_time_passes()
    {
        var obs = Dongle(false, lastSeenAgo: TimeSpan.FromMinutes(9));
        Assert.Equal(LinkState.Asleep, Resolve(dongle: obs)!.State);
        Assert.Equal(LinkState.Offline, Resolve(dongle: obs, now: T + TimeSpan.FromMinutes(1))!.State);
    }

    [Fact]
    public void Paired_but_disconnected_bluetooth_hides_stale_battery()
    {
        var s = Resolve(ble: Ble(false, 100))!;
        Assert.Equal(LinkState.Disconnected, s.State);
        Assert.Equal(ConnectionType.None, s.Connection);
        Assert.Null(s.BatteryPercent);
    }

    [Fact]
    public void Snapshot_change_detection_ignores_timestamps()
    {
        var a = Resolve(dongle: Dongle(true, lastSeenAgo: TimeSpan.Zero))!;
        var b = a with { BatteryUpdatedAt = T.AddMinutes(1), LastSeenAt = T.AddMinutes(1) };
        Assert.False(b.DiffersFrom(a));
        Assert.True((a with { BatteryPercent = 79 }).DiffersFrom(a));
        Assert.True((a with { State = LinkState.Offline }).DiffersFrom(a));
    }
}
