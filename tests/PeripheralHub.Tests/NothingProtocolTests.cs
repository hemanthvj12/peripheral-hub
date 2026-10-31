using PeripheralHub.Plugins.NothingEar;

namespace PeripheralHub.Tests;

/// <summary>Frames captured from the owner's Nothing Ear (2) on 2026-10-27 over its "NT LINK" serial service.</summary>
public class NothingProtocolTests
{
    [Fact]
    public void Battery_request_matches_the_frame_the_earbuds_answered()
    {
        Assert.Equal(Frames.Hex("55 60 01 07 C0 00 00 01 AC DF"), NothingProtocol.BuildRequest(NothingProtocol.CmdBatteryRequest, 1));
    }

    [Fact]
    public void Reply_with_both_buds_out_of_the_case()
    {
        var buffer = Frames.Hex("55 60 01 07 40 05 00 01 02 02 64 03 64 32 89").ToList();

        var frame = NothingProtocol.TakeFrame(buffer);

        Assert.NotNull(frame);
        Assert.Equal(NothingProtocol.CmdBatteryReply, frame.Value.Command);
        Assert.Empty(buffer);
        Assert.Equal(new NothingBattery(new PartLevel(100, false), new PartLevel(100, false), null),
            NothingProtocol.ParseBattery(frame.Value.Payload));
    }

    [Fact]
    public void Charging_bit_and_case()
    {
        // Left 55% charging, right 60% charging, case 80%: bit 7 of a level is "charging".
        Assert.Equal(new NothingBattery(new PartLevel(55, true), new PartLevel(60, true), new PartLevel(80, false)),
            NothingProtocol.ParseBattery(Frames.Hex("03 02 B7 03 BC 04 50")));
    }

    [Fact]
    public void Frames_split_across_reads_and_noise_are_handled()
    {
        var buffer = Frames.Hex("00 FF 55 60 01 07 40 05").ToList(); // junk, then half a frame
        Assert.Null(NothingProtocol.TakeFrame(buffer));

        buffer.AddRange(Frames.Hex("00 01 02 02 64 03 64 32 89"));
        Assert.Equal(NothingProtocol.CmdBatteryReply, NothingProtocol.TakeFrame(buffer)?.Command);
    }

    [Fact]
    public void Frame_with_a_bad_crc_is_dropped()
    {
        var buffer = Frames.Hex("55 60 01 07 40 05 00 01 02 02 64 03 64 32 88").ToList();
        Assert.Null(NothingProtocol.TakeFrame(buffer));
    }

    [Theory]
    [InlineData("Ear (2)", "nothing-ear-2")]       // default Windows name: gets the Ear (2) picture
    [InlineData("My Ear (2)", "nothing-ear-2")]
    [InlineData("Ear (stick)", "nothing-ear")]     // other models: generic icon
    [InlineData("My buds", "nothing-ear")]         // renamed pair: generic icon
    public void Windows_name_picks_the_artwork(string name, string model)
    {
        Assert.Equal(model, NothingEarDriver.ModelFor(name));
    }

    [Fact]
    public void Truncated_battery_payload_is_rejected()
    {
        Assert.Null(NothingProtocol.ParseBattery(Frames.Hex("03 02 64")));
        Assert.Null(NothingProtocol.ParseBattery([]));
    }

    // ---- Noise control (codes from abyss-x-desktop; not yet captured from the buds) ----

    [Theory]
    [InlineData(NoiseMode.Off, null, 0x05)]
    [InlineData(NoiseMode.Transparency, null, 0x07)]
    [InlineData(NoiseMode.Cancellation, NoiseStrength.High, 0x01)]
    [InlineData(NoiseMode.Cancellation, NoiseStrength.Mid, 0x02)]
    [InlineData(NoiseMode.Cancellation, NoiseStrength.Low, 0x03)]
    [InlineData(NoiseMode.Cancellation, NoiseStrength.Adaptive, 0x04)]
    [InlineData(NoiseMode.Cancellation, null, 0x04)] // strength never seen: adaptive
    public void Anc_set_frame_carries_the_mode_code(NoiseMode mode, NoiseStrength? strength, byte code)
    {
        var frame = NothingProtocol.BuildAncSet(mode, strength, 7);

        // 55 60 01 0F F0 03 00 07 01 <code> 00 <crc>
        Assert.Equal(Frames.Hex($"55 60 01 0F F0 03 00 07 01 {code:X2} 00"), frame[..^2]);
        ushort crc = NothingProtocol.Crc16(frame.AsSpan(0, frame.Length - 2));
        Assert.Equal(new[] { (byte)crc, (byte)(crc >> 8) }, frame[^2..]);
    }

    [Fact]
    public void Anc_read_request()
    {
        var frame = NothingProtocol.BuildRequest(NothingProtocol.CmdAncRequest, 2);
        Assert.Equal(Frames.Hex("55 60 01 1E C0 00 00 02"), frame[..^2]);
    }

    [Fact]
    public void Anc_reply_round_trips_through_frame_parsing()
    {
        var reply = NothingProtocol.BuildFrame(NothingProtocol.CmdAncReply, 2, Frames.Hex("01 03 00"));
        var frame = NothingProtocol.TakeFrame(reply.ToList());

        Assert.NotNull(frame);
        Assert.True(NothingProtocol.IsAnc(frame.Value.Command));
        Assert.Equal((NoiseMode.Cancellation, (NoiseStrength?)NoiseStrength.Low), NothingProtocol.ParseAnc(frame.Value.Payload));
    }

    [Fact]
    public void Anc_reply_captured_from_the_earbuds()
    {
        // Captured 2026-10-28 on the live link: reply 401E, op 02, payload 01 05 00 (off), with CRC.
        var frame = NothingProtocol.TakeFrame(Frames.Hex("55 60 01 1E 40 03 00 02 01 05 00 8A 58").ToList());
        Assert.Equal(NothingProtocol.CmdAncReply, frame?.Command);
        Assert.Equal((NoiseMode.Off, (NoiseStrength?)null), NothingProtocol.ParseAnc(frame!.Value.Payload));
    }

    [Fact]
    public void Anc_pushes_from_a_phone_change_have_no_crc()
    {
        // Captured 2026-10-28 while switching modes in the phone app: 55 00 01 03 E0 03 00 00 01 <code> 00, no CRC,
        // several in a row in one read; followed by a normal reply (with CRC).
        var buffer = Frames.Hex("55 00 01 03 E0 03 00 00 01 07 00 55 00 01 03 E0 03 00 00 01 04 00 " +
                                "55 60 01 07 40 05 00 05 02 02 32 03 28 D2 E8").ToList();

        var first = NothingProtocol.TakeFrame(buffer);
        var second = NothingProtocol.TakeFrame(buffer);
        var third = NothingProtocol.TakeFrame(buffer);

        Assert.Equal(NothingProtocol.CmdAncPush, first?.Command);
        Assert.Equal((NoiseMode.Transparency, (NoiseStrength?)null), NothingProtocol.ParseAnc(first!.Value.Payload));
        Assert.Equal((NoiseMode.Cancellation, (NoiseStrength?)NoiseStrength.Adaptive), NothingProtocol.ParseAnc(second!.Value.Payload));
        Assert.Equal(NothingProtocol.CmdBatteryReply, third?.Command);
        Assert.Empty(buffer);
    }

    [Fact]
    public void Half_a_push_waits_for_the_rest()
    {
        var buffer = Frames.Hex("55 00 01 03 E0 03 00 00 01").ToList();
        Assert.Null(NothingProtocol.TakeFrame(buffer));
        buffer.AddRange(Frames.Hex("01 00"));
        Assert.Equal((NoiseMode.Cancellation, (NoiseStrength?)NoiseStrength.High), NothingProtocol.ParseAnc(NothingProtocol.TakeFrame(buffer)!.Value.Payload));
    }

    [Fact]
    public void Anc_codes_parse_and_unknown_codes_are_ignored()
    {
        Assert.Equal((NoiseMode.Off, (NoiseStrength?)null), NothingProtocol.ParseAnc(Frames.Hex("01 05 00")));
        Assert.Equal((NoiseMode.Transparency, (NoiseStrength?)null), NothingProtocol.ParseAnc(Frames.Hex("01 07 00")));
        Assert.Null(NothingProtocol.ParseAnc(Frames.Hex("01 06 00")));
        Assert.Null(NothingProtocol.ParseAnc(Frames.Hex("01")));
    }
}
