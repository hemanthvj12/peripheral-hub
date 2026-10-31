using System.Diagnostics;
using Microsoft.Win32;

namespace PeripheralHub;

/// <summary>
/// A device maker's own configuration app, declared in a plugin's manifest ("vendorApps"). The hub pauses its requests to a device while that device's app is
/// running (both would share the same command channel), and UIs can offer to open the app.
/// </summary>
/// <param name="Model">The <see cref="DeviceSnapshot.Model"/> this app configures.</param>
/// <param name="DisplayName">Short name for UI text, e.g. "Kreo app".</param>
/// <param name="InstalledName">Start of the app's name in Windows' installed-programs list.</param>
/// <param name="ExeRelativePaths">Executable locations relative to the install folder, in order of preference.</param>
/// <param name="ProcessName">Process name while running (file name without ".exe").</param>
/// <param name="DefaultInstallFolder">Where the installer puts it by default, used if the installed-programs entry is missing.</param>
public sealed record VendorApp(string Model, string DisplayName, string InstalledName, string[] ExeRelativePaths,
    string ProcessName, string DefaultInstallFolder)
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Full path of the app's executable, or null if it isn't installed.</summary>
    public string? FindExecutable()
    {
        foreach (var folder in InstallFolders())
            foreach (var rel in ExeRelativePaths)
            {
                var path = Path.Combine(folder, rel);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    private IEnumerable<string> InstallFolders()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, key) in new[]
                 {
                     (Registry.LocalMachine, uninstall),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, uninstall),
                 })
        {
            using var root = hive.OpenSubKey(key);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display &&
                    display.StartsWith(InstalledName, StringComparison.OrdinalIgnoreCase) &&
                    entry.GetValue("InstallLocation") is string location && location.Length > 0)
                    yield return location;
            }
        }
        yield return DefaultInstallFolder;
    }
}
