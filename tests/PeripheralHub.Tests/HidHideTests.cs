using PeripheralHub.Integration;

namespace PeripheralHub.Tests;

/// <summary>HidHideCLI "--cloak-state --inv-state --app-list" output, as seen on the owner's PC (2026-10-28).</summary>
public class HidHideTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\PeripheralHub\current\PeripheralHub.exe";

    [Fact]
    public void Listed_app_is_allowed()
    {
        var s = HidHide.Parse($"--cloak-on\r\n--inv-off\r\n--app-reg \"C:\\Program Files\\DSX\\DSX.exe\"\r\n--app-reg \"{Exe}\"\r\n", Exe);
        Assert.Equal((true, true, false, true, true), (s.Installed, s.CloakingOn, s.InverseMode, s.AppListed, s.Allowed));
    }

    [Fact]
    public void Path_match_ignores_case()
    {
        Assert.True(HidHide.Parse($"--cloak-on\n--inv-off\n--app-reg \"{Exe.ToUpperInvariant()}\"\n", Exe).Allowed);
    }

    [Fact]
    public void Unlisted_app_is_not_allowed()
    {
        var s = HidHide.Parse("--cloak-on\n--inv-off\n--app-reg \"C:\\Program Files\\DSX\\DSX.exe\"\n", Exe);
        Assert.False(s.Allowed);
        Assert.Null(s.Error);
    }

    [Fact]
    public void Inverse_mode_flips_the_meaning_of_the_list()
    {
        Assert.True(HidHide.Parse("--cloak-on\n--inv-on\n", Exe).Allowed);
        Assert.False(HidHide.Parse($"--cloak-on\n--inv-on\n--app-reg \"{Exe}\"\n", Exe).Allowed);
    }

    [Fact]
    public void Unexpected_output_is_an_error_not_a_verdict()
    {
        var s = HidHide.Parse("something else", Exe);
        Assert.NotNull(s.Error);
        Assert.False(s.Allowed);
    }
}
