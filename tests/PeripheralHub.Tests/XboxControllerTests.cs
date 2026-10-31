using PeripheralHub.Plugins.XboxControllers;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>
/// Xbox controllers through XInput. Only the emulated Xbox 360 controller (DSX) has been seen live (2026-10-28):
/// slot 0, battery type Wired, level Full, XUSB device under "Nefarius Virtual Gamepad Emulation Bus G2".
/// The rest follows Microsoft's XInput documentation.
/// </summary>
public class XboxControllerTests
{
    private static readonly VirtualOrigin Nefarius = new(@"ROOT\USB\0000", "Nefarius Virtual Gamepad Emulation Bus G2");

    private static XInputSlot Slot(int i, XInputBatteryType type, XInputBatteryLevel level = XInputBatteryLevel.Full) => new(i, type, level);

    [Fact]
    public void Dsx_emulated_xbox_360_is_virtual_with_no_battery()
    {
        var v = Assert.Single(XboxControllerDriver.Describe([Slot(0, XInputBatteryType.Wired)], [Nefarius], false, null));

        Assert.Equal((ConnectionType.Virtual, (int?)null, (string?)null, false), (v.Connection, v.BatteryPercent, v.BatteryLabel, v.Guessed));
        Assert.Equal("Nefarius", v.Origin!.ShortCreator);
    }

    [Fact]
    public void Real_controller_on_its_cable_shows_wired_without_battery()
    {
        var v = Assert.Single(XboxControllerDriver.Describe([Slot(0, XInputBatteryType.Wired)], [null], false, null));
        Assert.Equal((ConnectionType.Wired, (int?)null, (string?)null), (v.Connection, v.BatteryPercent, v.BatteryLabel));
    }

    [Fact]
    public void Wireless_adapter_controller_shows_the_level_by_name()
    {
        var v = Assert.Single(XboxControllerDriver.Describe(
            [Slot(0, XInputBatteryType.NiMH, XInputBatteryLevel.Medium)], [null], false, null));

        Assert.Equal((ConnectionType.Dongle, "Medium"), (v.Connection, v.BatteryLabel));
        Assert.NotNull(v.BatteryPercent); // only fills the battery icon
    }

    [Fact]
    public void Bluetooth_controller_uses_windows_battery_percentage_when_there_is_one()
    {
        var v = Assert.Single(XboxControllerDriver.Describe(
            [Slot(0, XInputBatteryType.Alkaline, XInputBatteryLevel.Low)], [], bluetoothXboxPresent: true, bluetoothBatteryPercent: 37));

        Assert.Equal((ConnectionType.Bluetooth, (int?)37, (string?)null), (v.Connection, v.BatteryPercent, v.BatteryLabel));
    }

    [Fact]
    public void Two_bluetooth_controllers_fall_back_to_levels()
    {
        var views = XboxControllerDriver.Describe(
            [Slot(0, XInputBatteryType.Alkaline, XInputBatteryLevel.Low), Slot(1, XInputBatteryType.NiMH, XInputBatteryLevel.Full)],
            [], true, 37);

        Assert.Equal(["Low", "Full"], views.Select(v => v.BatteryLabel));
    }

    [Fact]
    public void Real_and_emulated_together_are_matched_by_order_and_marked_as_a_guess()
    {
        var views = XboxControllerDriver.Describe(
            [Slot(0, XInputBatteryType.Wired), Slot(1, XInputBatteryType.Wired)], [null, Nefarius], false, null);

        Assert.Equal([ConnectionType.Wired, ConnectionType.Virtual], views.Select(v => v.Connection));
        Assert.All(views, v => Assert.True(v.Guessed));
    }

    [Fact]
    public void Nothing_connected_shows_nothing()
    {
        Assert.Empty(XboxControllerDriver.Describe([], [], false, null));
    }
}
