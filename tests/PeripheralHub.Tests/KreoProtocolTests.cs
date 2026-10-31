using PeripheralHub.Plugins.KreoIkarus;

namespace PeripheralHub.Tests;

/// <summary>Frames captured from the Kreo Ikarus on 2026-10-26.</summary>
public class KreoProtocolTests
{
    [Fact]
    public void Requests_match_what_the_vendor_app_sends()
    {
        Assert.Equal(Frames.Hex("08 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 4A"), KreoProtocol.BuildRequest(KreoProtocol.CmdOnline));
        Assert.Equal(Frames.Hex("08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49"), KreoProtocol.BuildRequest(KreoProtocol.CmdBattery));
    }

    [Fact]
    public void Online_reply_from_the_cable()
    {
        var reply = Frames.Hex("08 03 00 00 00 01 01 77 AD 66 00 00 00 00 00 00 BE");
        Assert.True(KreoProtocol.IsValid(reply));
        Assert.True(KreoProtocol.ParseOnline(reply));
    }

    [Fact]
    public void Online_reply_while_asleep_on_the_receiver()
    {
        var reply = Frames.Hex("08 03 00 00 00 01 00 77 AD 66 00 00 00 00 00 00 BF");
        Assert.False(KreoProtocol.ParseOnline(reply));
    }

    [Fact]
    public void Battery_reply_on_the_cable_is_full_and_charging()
    {
        var b = KreoProtocol.ParseBattery(Frames.Hex("08 04 00 00 00 02 64 01 10 48 00 00 00 00 00 00 8A"));
        Assert.Equal(new KreoBattery(100, Charging: true, Millivolts: 4168), b);
    }

    [Fact]
    public void Battery_reply_on_the_receiver_is_not_charging()
    {
        var b = KreoProtocol.ParseBattery(Frames.Hex("08 04 00 00 00 02 64 00 10 44 00 00 00 00 00 00 8F"));
        Assert.Equal(new KreoBattery(100, Charging: false, Millivolts: 4164), b);
    }

    [Fact]
    public void Display_percent_matches_the_Kreo_app_on_the_receiver()
    {
        // 2026-10-27: the mouse said 90% at 4.072 V; the Kreo app showed 92 (table interpolation gives 93, then it smooths).
        var b = KreoProtocol.ParseBattery(Frames.Hex("08 04 00 00 00 02 5A 00 0F E8 00 00 00 00 00 00 F6"))!.Value;
        Assert.Equal(90, b.Percent);
        Assert.Equal(4072, b.Millivolts);
        Assert.Equal(93, b.DisplayPercent);
    }

    [Fact]
    public void Display_percent_uses_the_mouse_value_while_charging()
    {
        // On the cable the voltage reads high (4.168 V would be "100%" by the table); the mouse's own 100% is used.
        var b = KreoProtocol.ParseBattery(Frames.Hex("08 04 00 00 00 02 5A 01 10 3F 00 00 00 00 00 00 9D"))!.Value;
        Assert.True(b.Charging);
        Assert.Equal(90, b.DisplayPercent);
    }

    [Theory]
    [InlineData(3000, 0)]
    [InlineData(3050, 0)]
    [InlineData(3600, 20)]
    [InlineData(4060, 90)]
    [InlineData(4070, 93)] // halfway between 90% and 95%, rounded
    [InlineData(4110, 100)]
    [InlineData(4200, 100)]
    public void Voltage_table_interpolation(int mv, int percent) => Assert.Equal(percent, KreoProtocol.PercentFromMillivolts(mv));

    [Theory]
    [InlineData(null, 93, false, 93)] // first reading
    [InlineData(92, 93, false, 92)]   // voltage wobble upwards while discharging: ignored
    [InlineData(92, 91, false, 91)]   // going down: shown
    [InlineData(80, 95, false, 95)]   // big rise: charged elsewhere, shown
    [InlineData(92, 93, true, 93)]    // charging: rises shown
    public void Shown_level_ignores_small_rises_while_discharging(int? previous, int reading, bool charging, int shown) =>
        Assert.Equal(shown, PeripheralHub.Plugins.KreoIkarus.KreoIkarusDriver.Session.Smooth(previous, reading, charging));

    [Fact]
    public void Corrupted_frame_is_rejected()
    {
        var reply = Frames.Hex("08 04 00 00 00 02 64 00 10 44 00 00 00 00 00 00 8E"); // checksum off by one
        Assert.False(KreoProtocol.IsValid(reply));
        Assert.Null(KreoProtocol.ParseBattery(reply));
    }

    [Fact]
    public void A_reply_to_a_different_command_is_not_mistaken_for_battery()
    {
        Assert.Null(KreoProtocol.ParseBattery(Frames.Hex("08 03 00 00 00 01 01 77 AD 66 00 00 00 00 00 00 BE")));
    }

    [Fact]
    public void Status_push_is_recognised()
    {
        var push = new byte[17];
        push[0] = 0x08; push[1] = KreoProtocol.CmdStatusChanged; push[6] = 0x40;
        push[16] = KreoProtocol.Checksum(push);
        Assert.True(KreoProtocol.IsStatusPush(push));
    }
}
