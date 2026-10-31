using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PeripheralHub.App;

/// <summary>Opens a device maker's app, or brings it to the front if it's already running.</summary>
public static class VendorLauncher
{
    /// <returns>An error message for the user, or null on success.</returns>
    public static string? Open(VendorApp app)
    {
        foreach (var p in Process.GetProcessesByName(app.ProcessName))
        {
            using (p)
            {
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return null;
            }
        }

        // Not running, or running with no visible window (e.g. minimised to its own tray icon):
        // start it. The app's own single-instance handling takes over in the second case.
        var exe = app.FindExecutable();
        if (exe is null) return $"The {app.DisplayName} isn't installed.";
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return null;
        }
        catch (Exception ex) // e.g. the user cancelled a UAC prompt, or the file was removed
        {
            return $"Couldn't open the {app.DisplayName}: {ex.Message}";
        }
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
