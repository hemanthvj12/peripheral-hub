using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PeripheralHub.App;

/// <summary>Follows Windows' light/dark app mode and accent colour; exposes them as dynamic brushes.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                Application.Current.Dispatcher.Invoke(Apply);
        };
    }

    private static void Apply()
    {
        IsDark = ReadAppsUseDarkTheme();
        var accent = ReadAccent() ?? Color.FromRgb(0x00, 0x67, 0xC0);
        var r = Application.Current.Resources;

        Set(r, "WindowBg", IsDark ? "#202020" : "#F3F3F3");
        Set(r, "CardBg", IsDark ? "#2B2B2B" : "#FFFFFF");
        Set(r, "CardHoverBg", IsDark ? "#323232" : "#F9F9F9");
        Set(r, "CardBorder", IsDark ? "#3A3A3A" : "#E5E5E5");
        Set(r, "TextPrimary", IsDark ? "#FFFFFF" : "#1B1B1B");
        Set(r, "TextSecondary", IsDark ? "#A0A0A0" : "#616161");
        Set(r, "Track", IsDark ? "#3D3D3D" : "#E0E0E0");
        // A tile's drawer (extra controls) is a step lighter than the tile so it stands out; its pills one step more.
        Set(r, "DrawerBg", IsDark ? "#383838" : "#F2F2F2");
        Set(r, "DrawerTrack", IsDark ? "#484848" : "#E2E2E2");
        Set(r, "Good", IsDark ? "#6CCB5F" : "#0F7B0F");
        Set(r, "Bad", IsDark ? "#FF99A4" : "#C42B1C");
        Set(r, "Warn", IsDark ? "#FFC83D" : "#9D5D00");
        Set(r, "ButtonHoverBg", IsDark ? "#3A3A3A" : "#EAEAEA");
        r["Accent"] = Freeze(new SolidColorBrush(IsDark ? Lighten(accent, 0.35) : accent));

        // Connection badges: text colour + a faint tint of the same colour behind it.
        SetBadge(r, "ConnBluetooth", IsDark ? "#62B0FF" : "#0063B1");
        SetBadge(r, "ConnDongle", IsDark ? "#B794FF" : "#6B2FD6");
        SetBadge(r, "ConnWired", IsDark ? "#F5B041" : "#A35200");
        SetBadge(r, "ConnDisplay", IsDark ? "#4FD1C5" : "#00767A"); // monitors' video links (DisplayPort, HDMI…)
        // Marker (the vibration waves on a DualSense that HapticLink is giving games as a wired one): indigo,
        // between the Bluetooth blue and the brand purple.
        SetBadge(r, "Marker", IsDark ? "#8E9CFF" : "#3D4FC4");
    }

    private static void SetBadge(ResourceDictionary r, string key, string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        r[key] = Freeze(new SolidColorBrush(c));
        r[key + "Bg"] = Freeze(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x2E : (byte)0x1F, c.R, c.G, c.B)));
    }

    private static void Set(ResourceDictionary r, string key, string hex) =>
        r[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private static bool ReadAppsUseDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static Color? ReadAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is not int abgr) return null;
        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
    }

    // ---- Windows 11 window chrome ----
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Rounded corners + native border, dark title colours, and (optionally) hidden from Alt+Tab.</summary>
    public static void StyleWindow(Window w, bool toolWindow)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (toolWindow)
        {
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
            SetWindowLong(hwnd, GWL_EXSTYLE, (GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        }
    }
}
