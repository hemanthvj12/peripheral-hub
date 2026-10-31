namespace PeripheralHub.Tests;

/// <summary>Pinned ("kept") devices: shown while disconnected and after restarts, dropped when unpaired in Windows.</summary>
public class KeptDevicesTests
{
    private const string EarId = "bt:a1b2c3d4e5f6", EarMac = "a1b2c3d4e5f6";
    private static readonly Dictionary<string, PairedDevice> EarPaired = new() { [EarMac] = new("Ear (2)", DeviceKind.Audio) };
    private static readonly Dictionary<string, PairedDevice> NothingPaired = [];

    private static DeviceSnapshot Ear(LinkState state = LinkState.Online, int? left = 80, int? right = 75, int? @case = 40) =>
        new(EarId, "Ear (2)", DeviceKind.Audio, state == LinkState.Online ? ConnectionType.Bluetooth : ConnectionType.None, state,
            Math.Min(left ?? 100, right ?? 100), ChargeState.Discharging, null, Model: "nothing-ear-2", BluetoothMac: EarMac,
            Parts: [new("L", left, false, true), new("R", right, false, true), new("Case", @case, false, @case is not null)]);

    private static DeviceSnapshot Mouse() =>
        new("kreo-ikarus", "Kreo Ikarus", DeviceKind.Mouse, ConnectionType.Dongle, LinkState.Online, 82, ChargeState.Discharging, null);

    [Fact]
    public void Kept_device_shows_as_disconnected_with_last_levels_after_a_restart()
    {
        var store = new Dictionary<string, KeptDevice>();
        var kept = new KeptDevices(store);
        kept.Keep(Ear());

        // Restart: a new instance from the stored settings, and the hub doesn't report the earbuds (not connected).
        var list = new KeptDevices(store).Merge([Mouse()], EarPaired, out _);

        var ear = Assert.Single(list, s => s.Id == EarId);
        Assert.Equal((LinkState.Disconnected, ConnectionType.None, "Ear (2)", DeviceKind.Audio, "nothing-ear-2", 75),
            (ear.State, ear.Connection, ear.Name, ear.Kind, ear.Model, ear.BatteryPercent));
        Assert.Equal(["L 80", "R 75", "Case 40"], ear.Parts!.Select(p => $"{p.Label} {p.Percent}"));
        Assert.All(ear.Parts!, p => Assert.False(p.Live));
    }

    [Fact]
    public void A_part_missing_from_a_reading_keeps_its_last_level()
    {
        var kept = new KeptDevices([]);
        kept.Keep(Ear(@case: 40));
        kept.Merge([Ear(left: 70, right: 65, @case: null)], EarPaired, out bool changed); // case closed

        Assert.True(changed);
        var parts = kept.Merge([], EarPaired, out _).Single().Parts!;
        Assert.Equal(["L 70", "R 65", "Case 40"], parts.Select(p => $"{p.Label} {p.Percent}"));
    }

    [Fact]
    public void Unpaired_device_is_removed_after_two_scans_and_unpinned()
    {
        var kept = new KeptDevices([]);
        kept.Keep(Ear());
        kept.Merge([Ear()], EarPaired, out _);

        var once = kept.Merge([Ear(LinkState.Disconnected)], NothingPaired, out bool changed1);
        Assert.Contains(once, s => s.Id == EarId); // one odd scan isn't enough
        Assert.False(changed1);

        var twice = kept.Merge([Ear(LinkState.Disconnected)], NothingPaired, out bool changed2);
        Assert.DoesNotContain(twice, s => s.Id == EarId); // gone, even though the driver still reports it this run
        Assert.True(changed2);
        Assert.False(kept.IsKept(EarId));
    }

    [Fact]
    public void Device_that_was_never_paired_is_not_forgotten()
    {
        // A controller only used on its cable reports a MAC that Windows never paired.
        var pad = new DeviceSnapshot("sony:f6e5d4c3b2a1", "DualSense", DeviceKind.Controller, ConnectionType.Wired, LinkState.Online,
            90, ChargeState.Charging, null, BluetoothMac: "f6e5d4c3b2a1");
        var kept = new KeptDevices([]);
        kept.Keep(pad);
        for (int i = 0; i < 3; i++) kept.Merge([pad], NothingPaired, out _);
        Assert.True(kept.IsKept(pad.Id));
    }

    [Fact]
    public void Re_paired_device_comes_back()
    {
        var kept = new KeptDevices([]);
        kept.Merge([Ear()], EarPaired, out _);
        kept.Merge([Ear()], NothingPaired, out _);
        Assert.Empty(kept.Merge([Ear()], NothingPaired, out _)); // forgotten

        Assert.Single(kept.Merge([Ear()], EarPaired, out _));    // paired again
    }

    [Fact]
    public void Without_a_paired_list_nothing_is_forgotten()
    {
        var kept = new KeptDevices([]);
        kept.Keep(Ear());
        kept.Merge([Ear()], EarPaired, out _);
        for (int i = 0; i < 3; i++) kept.Merge([], null, out _);
        Assert.True(kept.IsKept(EarId));
    }

    [Fact]
    public void Disconnected_kept_devices_sit_with_their_kind()
    {
        var kept = new KeptDevices([]);
        kept.Keep(Ear());
        var pad = new DeviceSnapshot("sony:f6e5d4c3b2a1", "DualSense", DeviceKind.Controller, ConnectionType.None,
            LinkState.Disconnected, 60, ChargeState.Unknown, null);
        kept.Keep(pad);
        var monitor = new DeviceSnapshot("monitor:acr0750:344027044", "Acer VG240Y S", DeviceKind.Monitor, ConnectionType.None,
            LinkState.Disconnected, null, ChargeState.Unknown, null);
        kept.Keep(monitor);
        var keyboard = Mouse() with { Id = "aula-f75", Name = "AULA F75", Kind = DeviceKind.Keyboard };

        var list = kept.Merge([keyboard, Mouse()], EarPaired, out _);

        // The hub's order (owner, 2026-10-29): monitors, keyboards, mice, audio, controllers.
        Assert.Equal([monitor.Id, "aula-f75", "kreo-ikarus", EarId, pad.Id], list.Select(s => s.Id));
    }

    [Fact]
    public void Old_pin_without_details_borrows_windows_name_and_kind()
    {
        // A widget pin from before 2026-10-28: only the id is known until the device is seen again.
        var kept = new KeptDevices(new() { ["sony:f6e5d4c3b2a1"] = new KeptDevice { Name = "sony:f6e5d4c3b2a1" } });
        var paired = new Dictionary<string, PairedDevice> { ["f6e5d4c3b2a1"] = new("DualSense Wireless Controller", DeviceKind.Controller) };

        var pad = Assert.Single(kept.Merge([], paired, out bool changed));

        Assert.True(changed);
        Assert.Equal(("DualSense Wireless Controller", DeviceKind.Controller, "f6e5d4c3b2a1"), (pad.Name, pad.Kind, pad.BluetoothMac));
    }

    [Fact]
    public void Unpinned_device_leaves_the_list_as_soon_as_it_disconnects()
    {
        var kept = new KeptDevices([]);
        Assert.Single(kept.Merge([Ear()], EarPaired, out _));
        Assert.Empty(kept.Merge([Ear(LinkState.Disconnected)], EarPaired, out _));
    }

    [Fact]
    public void Unpinned_asleep_device_stays()
    {
        // Receiver plugged in, mouse asleep: still linked, wakes on touch.
        var asleep = Mouse() with { State = LinkState.Asleep };
        var offline = asleep with { Id = "aula-f75", State = LinkState.Offline };
        Assert.Equal(2, new KeptDevices([]).Merge([asleep, offline], null, out _).Count);
    }

    [Fact]
    public void Unpinned_devices_are_unaffected()
    {
        var list = new KeptDevices([]).Merge([Mouse()], EarPaired, out bool changed);
        Assert.Equal(["kreo-ikarus"], list.Select(s => s.Id));
        Assert.False(changed);
    }
}

public class DeviceOrderTests
{
    [Fact]
    public void Types_are_listed_monitor_keyboard_mouse_audio_controller()
    {
        var kinds = Enum.GetValues<DeviceKind>().OrderBy(DeviceOrder.Of);
        Assert.Equal([DeviceKind.Monitor, DeviceKind.Keyboard, DeviceKind.Mouse, DeviceKind.Audio, DeviceKind.Controller], kinds);
    }
}
