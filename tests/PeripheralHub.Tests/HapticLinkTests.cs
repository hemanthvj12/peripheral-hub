using PeripheralHub.Integration;

namespace PeripheralHub.Tests;

/// <summary>HapticLink's STATUS reply (its control pipe; README of github.com/hemanthvj12/haptic-link).</summary>
public class HapticLinkTests
{
    [Fact]
    public void Status_reply_is_parsed()
    {
        var s = HapticLinkStatus.Parse("on|1|1|a1b2c3d4e5f6|On: games see a wired DualSense (haptics, adaptive triggers, rumble, lights).");
        Assert.NotNull(s);
        Assert.Equal("on", s.State);
        Assert.True(s.Enabled);
        Assert.True(s.Haptics);
        Assert.Equal("a1b2c3d4e5f6", s.ControllerMac);
        Assert.StartsWith("On: games see", s.Message);
    }

    [Fact]
    public void Message_may_contain_the_separator()
    {
        var s = HapticLinkStatus.Parse("error|1|0||Couldn't start: a|b");
        Assert.Equal("Couldn't start: a|b", s!.Message);
        Assert.Null(s.ControllerMac);
    }

    [Theory]
    [InlineData("")]
    [InlineData("on|1|1")]
    [InlineData("|1|1|a1b2c3d4e5f6|x")]
    public void Malformed_replies_give_null(string reply) => Assert.Null(HapticLinkStatus.Parse(reply));

    [Fact]
    public void Only_the_bridged_controller_gets_the_waves_and_only_while_on()
    {
        var on = HapticLinkStatus.Parse("on|1|1|A1:B2:C3:D4:E5:F6|On")!;
        Assert.True(on.IsBridging("a1b2c3d4e5f6"));
        Assert.False(on.IsBridging("0a0b0c0d0e0f"));
        Assert.False(on.IsBridging(null));
        Assert.False(HapticLinkStatus.Parse("waiting|1|0||Waiting")!.IsBridging("a1b2c3d4e5f6"));
        Assert.False(HapticLinkStatus.Parse("off|0|0|a1b2c3d4e5f6|Off")!.IsBridging("a1b2c3d4e5f6"));
    }
}
