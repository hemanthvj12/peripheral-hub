using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public bool IsRunning() => RunningProcesses.Contains(ProcessName);

    // Several drivers ask every scan (2 s); one shared list of process names, at most every 2 s, is much lighter than a
    // Process.GetProcessesByName per driver, which reads every process's details each time.
    private static readonly object RunningLock = new();
    private static HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private static long _runningAt = long.MinValue;

    private static HashSet<string> RunningProcesses
    {
        get
        {
            lock (RunningLock)
            {
                long now = Environment.TickCount64;
                if (now - _runningAt < 2000) return _running;
                _runningAt = now;
                return _running = ProcessNames();
            }
        }
    }

    /// <summary>Names of the running processes (file name without ".exe", like <see cref="Process.ProcessName"/>).</summary>
    private static HashSet<string> ProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == new IntPtr(-1)) return names;
        try
        {
            var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                names.Add(Path.GetFileNameWithoutExtension(entry.ExeFile));
        }
        finally { CloseHandle(snapshot); }
        return names;
    }

    private const uint SnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public int ModuleId, Threads, ParentProcessId, PriorityClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

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
