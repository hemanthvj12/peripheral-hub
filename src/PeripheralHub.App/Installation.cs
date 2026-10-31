using System.IO;

namespace PeripheralHub.App;

/// <summary>
/// Install-time steps that Velopack runs: the first start after Setup, and clean-up before Windows' "Uninstall"
/// (Settings → Apps) deletes the app's files.
/// The uninstall step gets 30 seconds, then Velopack exits the process.
/// Settings (%APPDATA%\PeripheralHub) are kept, as most apps do, so a reinstall picks up kept (pinned) devices and the widget position.
/// </summary>
public static class Installation
{
    /// <summary>
    /// The first start after Setup: turn on "Start with Windows", as a tray app is expected to. Running Setup again
    /// (a reinstall, which is how new versions are installed while updates are manual) also counts as a first run, so
    /// a flag in the settings (which survive reinstalls) makes this happen only once: if the user turns it off later,
    /// it stays off.
    /// </summary>
    public static void FirstRun()
    {
        var settings = AppSettings.Load();
        if (settings.StartWithWindowsSetUp) return;
        try
        {
            StartupRegistration.Set(true);
            settings.StartWithWindowsSetUp = true;
            settings.Save();
            AppLog.Write("First run after install: turned on Start with Windows");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Write($"First run: couldn't turn on Start with Windows ({ex.Message})");
        }
    }

    public static void BeforeUninstall()
    {
        // "Start with Windows" would otherwise point at a deleted exe. Only remove it if it's ours (a development copy
        // installed with install.ps1 lives elsewhere and keeps its own entry).
        try { StartupRegistration.RemoveIfThisExe(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }

        // Our entry on HidHide's allowed apps would point at a deleted exe. Changing it needs admin rights, so Windows
        // shows one prompt; if the user declines (or doesn't answer in time) the entry just stays, which is harmless.
        if (Integration.HidHide.Check(Environment.ProcessPath!).AppListed)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, HidHideSetup.RemoveArg)
                    { UseShellExecute = true, Verb = "runas" })!;
                p.WaitForExit(25_000);
            }
            catch (System.ComponentModel.Win32Exception) { /* prompt declined */ }
        }
    }
}
