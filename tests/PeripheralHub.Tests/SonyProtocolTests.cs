using PeripheralHub.Plugins.SonyControllers;

namespace PeripheralHub.Tests;

/// <summary>Status bytes observed from the DualSense and DualShock 4 on 2026-10-26.</summary>
public class SonyProtocolTests
{
    [Fact]
    public void DualSense_usb_charging()
    {
        var b = SonyProtocol.ParseDualSense(Frames.Sony(0x01, 64, 53, 0x17));
        Assert.Equal(new SonyBattery(75, ChargeState.Charging, true), b);
    }

    [Fact]
    public void DualSense_bluetooth_on_battery()
    {
        var b = SonyProtocol.ParseDualSense(Frames.Sony(0x31, 78, 54, 0x07));
        Assert.Equal(new SonyBattery(75, ChargeState.Discharging, false), b);
    }

    [Fact]
    public void DualSense_bluetooth_on_a_phone_charger()
    {
        var b = SonyProtocol.ParseDualSense(Frames.Sony(0x31, 78, 54, 0x17));
        Assert.Equal(ChargeState.Charging, b?.Charging);
        Assert.Equal(75, b?.Percent);
    }

    [Theory]
    [InlineData(0x20, 100, ChargeState.Full)]
    [InlineData(0x28, 85, ChargeState.Full)] // seen on a phone charger over Bluetooth, 85% unplugged
    [InlineData(0x2A, 100, ChargeState.Full)]
    [InlineData(0xA5, null, ChargeState.Error)]
    [InlineData(0xB5, null, ChargeState.Error)]
    public void DualSense_full_and_error_states(byte status, int? percent, ChargeState state)
    {
        var b = SonyProtocol.ParseDualSense(Frames.Sony(0x01, 64, 53, status));
        Assert.Equal(percent, b?.Percent);
        Assert.Equal(state, b?.Charging);
    }

    [Fact]
    public void DualSense_short_bluetooth_report_has_no_battery()
    {
        Assert.Null(SonyProtocol.ParseDualSense(new byte[] { 0x01, 0x80, 0x80, 0x80, 0x80, 0x08, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Basic_bluetooth_report_padded_by_windows_has_no_battery()
    {
        // Windows pads the basic 0x01 report to the 78-byte input length; the padding isn't a USB status byte.
        var dualSense = Frames.Sony(0x01, 78, 53, 0x00);
        Assert.Null(SonyProtocol.ParseBattery(SonyModel.DualSense, dualSense, bluetooth: true));
        Assert.Null(SonyProtocol.ParseBattery(SonyModel.DualShock4, Frames.Sony(0x01, 78, 30, 0x00), bluetooth: true));
        // The full Bluetooth report still reads, and a USB report isn't taken for a Bluetooth one.
        Assert.Equal(75, SonyProtocol.ParseBattery(SonyModel.DualSense, Frames.Sony(0x31, 78, 54, 0x07), bluetooth: true)?.Percent);
        Assert.Null(SonyProtocol.ParseBattery(SonyModel.DualSense, Frames.Sony(0x31, 78, 54, 0x07), bluetooth: false));
    }

    [Fact]
    public void DualShock4_usb_charging()
    {
        var b = SonyProtocol.ParseDualShock4(Frames.Sony(0x01, 64, 30, 0x15));
        Assert.Equal(new SonyBattery(55, ChargeState.Charging, true), b);
    }

    [Fact]
    public void DualShock4_bluetooth_on_battery()
    {
        var b = SonyProtocol.ParseDualShock4(Frames.Sony(0x11, 78, 32, 0x03));
        Assert.Equal(new SonyBattery(35, ChargeState.Discharging, false), b);
    }

    [Fact]
    public void DualShock4_bluetooth_on_a_phone_charger()
    {
        var b = SonyProtocol.ParseDualShock4(Frames.Sony(0x11, 78, 32, 0x16));
        Assert.Equal(new SonyBattery(65, ChargeState.Charging, true), b);
    }

    [Fact]
    public void DualShock4_full_on_charger()
    {
        Assert.Equal(new SonyBattery(100, ChargeState.Full, true), SonyProtocol.ParseDualShock4(Frames.Sony(0x01, 64, 30, 0x1B)));
    }

    [Fact]
    public void DualShock4_bluetooth_report_padded_to_driver_buffer_size_still_parses()
    {
        // Windows reports a 547-byte input length for the DS4 over Bluetooth.
        Assert.Equal(35, SonyProtocol.ParseDualShock4(Frames.Sony(0x11, 547, 32, 0x03))?.Percent);
    }

    [Theory]
    [InlineData(0x0CE6, SonyModel.DualSense)]
    [InlineData(0x0DF2, SonyModel.DualSenseEdge)]
    [InlineData(0x05C4, SonyModel.DualShock4)]
    [InlineData(0x09CC, SonyModel.DualShock4)]
    [InlineData(0x0BA0, SonyModel.DualShock4Dongle)]
    public void Product_ids_map_to_models(ushort pid, SonyModel model) => Assert.Equal(model, SonyProtocol.ModelFor(pid));

    [Fact]
    public void Usb_mac_feature_is_little_endian()
    {
        var feature = Frames.Hex("09 A1 B2 C3 D4 E5 F6 00 00 00 00 00 00 00 00 00 00 00 00 00");
        Assert.Equal("f6e5d4c3b2a1", SonyProtocol.ParseUsbMac(feature));
    }

    // Feature 0x0B in the layout read from the owner's DualSense on 2026-11-03 (addresses replaced by placeholders):
    // own MAC, 08 25 00, four (value, MAC) entries △ ○ ✕ □, CRC-32 of A3 + report.
    private static byte[] PairingReport(string entries, bool goodCrc = true)
    {
        var r = Frames.Hex("0B F6 E5 D4 C3 B2 A1 08 25 00 " + entries + " 00 00 00 00");
        uint crc = SonyProtocol.Crc32(0xA3, r.AsSpan(0, 38)) ^ (goodCrc ? 0u : 1u);
        BitConverter.GetBytes(crc).CopyTo(r, 38);
        return r;
    }

    [Fact]
    public void DualSense_paired_hosts()
    {
        var r = PairingReport("01 11 11 11 11 11 11 00 22 22 22 22 22 22 02 11 11 11 11 11 11 03 66 55 44 33 22 11");
        var hosts = SonyProtocol.ParsePairedHosts(r, "a1b2c3d4e5f6", bluetooth: true)!;
        Assert.Equal(["△", "○", "✕", "□"], hosts.Select(h => h.Button));
        Assert.Equal(["111111111111", "222222222222", "111111111111", "112233445566"], hosts.Select(h => h.Mac));
        Assert.Equal([false, true, false, false], hosts.Select(h => h.InUse));
    }

    [Fact]
    public void DualSense_paired_hosts_rejects_bad_crc_other_controller_and_empty_slot()
    {
        const string entries = "00 11 11 11 11 11 11 01 00 00 00 00 00 00 02 11 11 11 11 11 11 03 66 55 44 33 22 11";
        Assert.Null(SonyProtocol.ParsePairedHosts(PairingReport(entries, goodCrc: false), "a1b2c3d4e5f6", bluetooth: true));
        Assert.Null(SonyProtocol.ParsePairedHosts(PairingReport(entries), "0a0b0c0d0e0f", bluetooth: true));
        Assert.Null(SonyProtocol.ParsePairedHosts(PairingReport(entries), "a1b2c3d4e5f6", bluetooth: true)![1].Mac);
    }
}
