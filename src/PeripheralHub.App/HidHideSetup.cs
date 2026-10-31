using System.ComponentModel;
using System.Diagnostics;
using PeripheralHub.Integration;

namespace PeripheralHub.App;

/// <summary>
/// Adds this exe to (or removes it from) HidHide's allowed apps. Changing HidHide's lists needs admin rights, so the
/// app starts a copy of itself elevated with <see cref="AllowArg"/>/<see cref="RemoveArg"/> (one admin prompt), which
/// does only that and exits. Nothing else runs or is scheduled; after reinstalling HidHide (which clears its lists) the
/// tray item shows unticked again.
/// </summary>
public static class HidHideSetup
{
    public const string AllowArg = "--hidhide-allow", RemoveArg = "--hidhide-remove";

    private static string ExePath => Environment.ProcessPath!;

    /// <summary>Elevated entry point (<see cref="AllowArg"/>). Returns the process exit code.</summary>
    public static int RunAllow()
    {
        if (HidHide.FindCli() is not { } cli) return 2;
        return HidHide.RunCli(cli, $"--app-reg \"{ExePath}\"").ExitCode == 0 ? 0 : 3;
    }

    /// <summary>Elevated entry point (<see cref="RemoveArg"/>): from the tray, and when uninstalling.</summary>
    public static int RunRemove()
    {
        if (HidHide.FindCli() is not { } cli) return 0;
        return HidHide.RunCli(cli, $"--app-unreg \"{ExePath}\"").ExitCode == 0 ? 0 : 3;
    }

    /// <summary>Runs this exe elevated with <paramref name="allow"/> ? <see cref="AllowArg"/> : <see cref="RemoveArg"/>.</summary>
    /// <returns>Ok, or an error message for the user (null when they cancelled the admin prompt).</returns>
    public static async Task<(bool Ok, string? Error)> RequestAsync(bool allow)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ExePath, allow ? AllowArg : RemoveArg) { UseShellExecute = true, Verb = "runas" })!;
            await p.WaitForExitAsync();
            return p.ExitCode switch
            {
                0 => (true, null),
                2 => (false, "HidHide isn't installed."),
                _ => (false, "HidHide refused the change."),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, null); } // prompt cancelled
        catch (Win32Exception ex) { return (false, ex.Message); }
    }
}
