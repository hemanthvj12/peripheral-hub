using PeripheralHub.Plugins.AulaF75;

namespace PeripheralHub.Tests;

/// <summary>Frames captured from the AULA F75 receiver on 2026-10-26, plus the community-published cable frame.</summary>
public class AulaProtocolTests
{
    [Fact]
    public void Requests_match_the_captured_frames()
    {
        Assert.Equal(Frames.Hex("13 07 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 1B"), AulaProtocol.OnlineRequest());
        Assert.Equal(Frames.Hex("13 4A 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5D"), AulaProtocol.BatteryRequest());
    }

    [Fact]
    public void Online_reply_when_keyboard_is_linked()
    {
        Assert.True(AulaProtocol.ParseOnline(Frames.Hex("13 07 01 00 01 01 00 00 00 00 00 00 00 00 00 00 00 00 00 1D")));
    }

    [Fact]
    public void Online_reply_when_keyboard_is_not_linked()
    {
        Assert.False(AulaProtocol.ParseOnline(Frames.Hex("13 07 01 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 1C")));
    }

    [Fact]
    public void Battery_on_battery_power()
    {
        var b = AulaProtocol.ParseBattery(Frames.Hex("13 4A 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 C5"));
        Assert.Equal(new AulaBattery(100, CableIn: false, RawState: 0x01), b);
    }

    [Fact]
    public void Battery_97_percent_from_the_community_capture()
    {
        var b = AulaProtocol.ParseBattery(Frames.Hex("13 4A 01 00 02 61 01 00 00 00 00 00 00 00 00 00 00 00 00 C2"));
        Assert.Equal(97, b?.Percent);
    }

    [Fact]
    public void Cable_in_pins_level_so_percent_is_withheld()
    {
        var b = AulaProtocol.ParseBattery(Frames.Hex("13 4A 01 00 02 64 10 00 00 00 00 00 00 00 00 00 00 00 00 D4"));
        Assert.NotNull(b);
        Assert.True(b!.Value.CableIn);
        Assert.Null(b.Value.Percent);
    }

    [Fact]
    public void Plug_in_transition_state_0x11_counts_as_cable_in()
    {
        // Captured 2026-10-27 at the moment of plugging in: real 97% with both flags set, then 100 (pinned) with 0x10.
        var b = AulaProtocol.ParseBattery(Frames.Hex("13 4A 01 00 02 61 11 00 00 00 00 00 00 00 00 00 00 00 00 D2"));
        Assert.NotNull(b);
        Assert.True(b!.Value.CableIn);
        Assert.Null(b.Value.Percent);
    }

    [Theory]
    [InlineData("13 0A 01 00 04 02 00 00 00 00 00 00 00 00 00 00 00 00 00 24", false)] // went to sleep / switched off
    [InlineData("13 0A 01 00 04 02 01 00 00 00 00 00 00 00 00 00 00 00 00 25", true)]  // woke up
    public void Link_push_reports_linked_state(string frame, bool linked)
    {
        var (l, battery) = AulaProtocol.ParseStatusPush(Frames.Hex(frame));
        Assert.Equal(linked, l);
        Assert.Null(battery);
    }

    [Fact]
    public void Battery_push_after_switching_on()
    {
        var (linked, battery) = AulaProtocol.ParseStatusPush(Frames.Hex("13 0A 01 00 04 05 64 01 00 00 00 00 00 00 00 00 00 00 00 8C"));
        Assert.Null(linked);
        Assert.Equal(new AulaBattery(100, CableIn: false, RawState: 0x01), battery);
    }

    [Fact]
    public void Unknown_push_kind_is_ignored()
    {
        // "07 00" was also pushed at power-on; its meaning is unknown.
        Assert.Equal((null, null), AulaProtocol.ParseStatusPush(Frames.Hex("13 0A 01 00 04 07 00 00 00 00 00 00 00 00 00 00 00 00 00 29")));
    }

    [Fact]
    public void Reply_flagged_with_crc_error_is_not_a_reading()
    {
        var frame = Frames.Hex("13 CA 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 45");
        Assert.True(AulaProtocol.IsReplyTo(frame, AulaProtocol.CmdBattery));
        Assert.Null(AulaProtocol.ParseBattery(frame));
    }

    [Fact]
    public void Bad_checksum_is_rejected()
    {
        Assert.Null(AulaProtocol.ParseBattery(Frames.Hex("13 4A 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 C6")));
    }
}
