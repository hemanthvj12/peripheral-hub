using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Tests;

/// <summary>Other Bluetooth peripherals, read from Windows' device properties.</summary>
public class GenericBluetoothTests
{
    [Fact]
    public void Hands_free_node_id_gives_mac_and_vendor()
    {
        // Captured 2026-10-27 from "Ear (2)": its battery sits on this hands-free (0x111E) node.
        var id = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&000102B0_PID&0000\A&1D0D9AA7&3&A1B2C3D4E5F6_C00000000";
        Assert.Equal(("a1b2c3d4e5f6", (ushort)0x02B0, (ushort)0x0000, (Guid?)new Guid("0000111E-0000-1000-8000-00805F9B34FB")), BluetoothDevices.ParseClassicChildId(id));
    }

    [Fact]
    public void Device_node_id_is_not_a_profile_node()
    {
        Assert.Null(BluetoothDevices.ParseClassicChildId(@"BTHENUM\DEV_A1B2C3D4E5F6\A&1D0D9AA7&3&BLUETOOTHDEVICE_A1B2C3D4E5F6"));
    }

    [Theory]
    [InlineData("Communication.Headset.Bluetooth", DeviceKind.Audio)]
    [InlineData("Multimedia.Headphone", DeviceKind.Audio)]
    [InlineData("Input.Mouse", DeviceKind.Mouse)]
    [InlineData("Input.Keyboard", DeviceKind.Keyboard)]
    [InlineData("Input.Gaming", DeviceKind.Controller)]
    public void Categories_map_to_kinds(string category, DeviceKind kind)
    {
        Assert.Equal(kind, GenericBluetoothDriver.KindFor([category]));
    }

    [Theory]
    [InlineData("Communication.Phone")]
    [InlineData("Computer.Laptop")]
    public void Other_categories_are_skipped(string category)
    {
        Assert.Null(GenericBluetoothDriver.KindFor([category]));
    }

    [Fact]
    public void Devices_a_plugin_claims_are_left_to_it()
    {
        var now = DateTimeOffset.Now;
        var kreo = new BleDeviceInfo("aa0000000001", 0x3554, 0xF533, "Kreo Ikarus", true, 80, now, ["Input.Mouse"]);
        var claiming = new GenericBluetoothDriver((vid, pid) => vid == 0x3554 && pid == 0xF533);
        claiming.Update(new ScanContext([], [kreo], [], now));
        Assert.Empty(claiming.GetSnapshots(now));

        // Without the plugin, the same mouse shows as a plain Bluetooth device with Windows' level.
        var plain = new GenericBluetoothDriver();
        plain.Update(new ScanContext([], [kreo], [], now));
        var s = Assert.Single(plain.GetSnapshots(now));
        Assert.Equal(("Kreo Ikarus", DeviceKind.Mouse, 80, (string?)null), (s.Name, s.Kind, s.BatteryPercent, s.Model));
    }

    [Fact]
    public void Headset_stays_as_disconnected_with_its_last_level_after_it_disconnects()
    {
        var driver = new GenericBluetoothDriver();
        var now = DateTimeOffset.Now;
        ClassicBtDeviceInfo Ear(bool connected) =>
            new("a1b2c3d4e5f6", 0x02B0, 0, "Ear (2)", connected, 90, now, ["Communication.Headset.Bluetooth"]);

        driver.Update(new ScanContext([], [], [Ear(true)], now));
        var s = Assert.Single(driver.GetSnapshots(now));
        Assert.Equal(("bt:a1b2c3d4e5f6", "Ear (2)", DeviceKind.Audio, ConnectionType.Bluetooth, LinkState.Online, 90, ChargeState.Unknown),
            (s.Id, s.Name, s.Kind, s.Connection, s.State, s.BatteryPercent, s.Charging));

        driver.Update(new ScanContext([], [], [Ear(false)], now));
        s = Assert.Single(driver.GetSnapshots(now));
        Assert.Equal((ConnectionType.None, LinkState.Disconnected, 90), (s.Connection, s.State, s.BatteryPercent));

        driver.Update(new ScanContext([], [], [Ear(true)], now));
        Assert.Equal(LinkState.Online, Assert.Single(driver.GetSnapshots(now)).State);
    }

    [Fact]
    public void Paired_device_never_connected_this_run_does_not_show()
    {
        var driver = new GenericBluetoothDriver();
        var now = DateTimeOffset.Now;
        driver.Update(new ScanContext([], [], [new("aabbccddeeff", 0x02B0, 0, "Old buds", false, 40, now, ["Communication.Headset.Bluetooth"])], now));
        Assert.Empty(driver.GetSnapshots(now));
    }
}
