using System.Diagnostics;
using Microsoft.Win32;

namespace PeripheralHub.Integration;

/// <param name="Installed">HidHide's command-line tool was found.</param>
/// <param name="CloakingOn">HidHide is hiding right now (DSX turns this on whenever it runs, even in native mode).</param>
/// <param name="InverseMode">HidHide's app list names apps that are blocked rather than allowed.</param>
/// <param name="AppListed">The app's exact path is on HidHide's application list.</param>
/// <param name="Error">Set when HidHide is installed but its state couldn't be read.</param>
public sealed record HidHideState(bool Installed, bool CloakingOn = false, bool InverseMode = false, bool AppListed = false,
    string? Error = null)
{
    public static HidHideState NotInstalled { get; } = new(false);

    /// <summary>The app can see the devices HidHide hides.</summary>
    public bool Allowed => Installed && Error is null && (InverseMode ? !AppListed : AppListed);
}

/// <summary>
/// HidHide (the driver DS4Windows and DSX use to hide real controllers from games) through its own command-line tool.
/// Reading its state needs no admin rights; adding or removing an allowed app does. The owner turns this on from the
/// tray ("Allow in HidHide", 2026-10-28) so the real controllers stay visible while DSX runs.
/// </summary>
public static class HidHide
{
    public const string RegistryKey = @"SOFTWARE\Nefarius Software Solutions e.U.\HidHide";

    /// <summary>Full path of HidHideCLI.exe, or null when HidHide isn't installed.</summary>
    public static string? FindCli()
    {
        string? root = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
            root = key?.GetValue("Path") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide");
        var cli = Path.Combine(root, Environment.Is64BitOperatingSystem ? "x64" : "x86", "HidHideCLI.exe");
        return File.Exists(cli) ? cli : null;
    }

    /// <summary>Reads HidHide's state for <paramref name="appPath"/> (no admin rights needed).</summary>
    public static HidHideState Check(string appPath)
    {
        if (FindCli() is not { } cli) return HidHideState.NotInstalled;
        var (exit, output) = RunCli(cli, "--cloak-state --inv-state --app-list");
        return exit == 0 ? Parse(output, appPath) : new HidHideState(true, Error: $"HidHideCLI exited with {exit}: {output.Trim()}");
    }

    internal static HidHideState Parse(string output, string appPath)
    {
        bool? cloak = null, inverse = null;
        bool listed = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "--cloak-on") cloak = true;
            else if (line == "--cloak-off") cloak = false;
            else if (line == "--inv-on") inverse = true;
            else if (line == "--inv-off") inverse = false;
            else if (line.StartsWith("--app-reg ", StringComparison.Ordinal) && SamePath(line[10..].Trim().Trim('"'), appPath)) listed = true;
        }
        return cloak is null || inverse is null
            ? new HidHideState(true, Error: $"Unexpected HidHideCLI output: {output.Trim()}")
            : new HidHideState(true, cloak.Value, inverse.Value, listed);
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Runs HidHideCLI with no window. Its stdin is closed right away: with no console attached it would otherwise wait
    /// for more commands on stdin.
    /// </summary>
    public static (int ExitCode, string Output) RunCli(string cli, string arguments, int timeoutMs = 10_000)
    {
        var psi = new ProcessStartInfo(cli, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return (-1, "timed out");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
    }
}
