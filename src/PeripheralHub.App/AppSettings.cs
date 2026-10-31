using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PeripheralHub.App;

/// <summary>User preferences, stored in %APPDATA%\PeripheralHub\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Pinned ("kept") devices by id: shown even while disconnected and after a restart (see <see cref="KeptDevices"/>).</summary>
    public Dictionary<string, KeptDevice> Kept { get; set; } = [];

    /// <summary>
    /// Before 2026-10-28 pins chose which devices the widget showed. Old pins become kept devices on load.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PinnedIds { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public bool WidgetHidden { get; set; }
    /// <summary>False (default): the widget lives on the desktop below all windows. True: always on top.</summary>
    public bool WidgetOnTop { get; set; }
    /// <summary>
    /// Show controllers that apps create (DSX's emulation, game streaming). Off by default (owner, 2026-10-28): they
    /// have no real battery and the real controller is shown anyway.
    /// </summary>
    public bool ShowVirtualControllers { get; set; }
    /// <summary>
    /// Switch Bluetooth PlayStation controllers to full reports to read their battery (on by default). Off: battery only
    /// when Steam/DSX/a game already switched it, so browsers and plain-gamepad games keep getting input (owner, 2026-10-29).
    /// </summary>
    public bool ReadBluetoothControllerBattery { get; set; } = true;
    /// <summary>Devices whose drawer was left open (folded away by default). Named for its first use, noise control.</summary>
    public HashSet<string> NoiseExpanded { get; set; } = [];

    /// <summary>"Start with Windows" was turned on at a first install; don't do it again on later (re)installs.</summary>
    public bool StartWithWindowsSetUp { get; set; }
    /// <summary>Last battery level seen per device id, shown (greyed) after a restart until a fresh reading arrives.</summary>
    public Dictionary<string, int> LastBattery { get; set; } = [];

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeripheralHub");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) is { } s)
            {
                foreach (var id in s.PinnedIds ?? [])
                    s.Kept.TryAdd(id, new KeptDevice { Name = id }); // details fill in when the device is next seen
                s.PinnedIds = null;
                return s;
            }
        }
        catch (Exception) { /* corrupt or unreadable: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* settings are a convenience; never crash over them */ }
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PeripheralHub";

    /// <summary>
    /// True only if the entry starts this exe. An entry left by another copy (e.g. an older install in a different
    /// folder) counts as off, so ticking "Start with Windows" points it here in one click.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && IsThisExe(command);
        }
    }

    private static bool IsThisExe(string command) =>
        string.Equals(command.Trim().Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Removes the entry if it starts this exe (used when uninstalling).</summary>
    public static void RemoveIfThisExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string command && IsThisExe(command))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
