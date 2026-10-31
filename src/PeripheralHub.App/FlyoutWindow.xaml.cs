using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PeripheralHub.App;

/// <summary>The panel that pops up above the tray icon, like Windows' quick settings. Hides when it loses focus.</summary>
public partial class FlyoutWindow : Window
{
    private readonly DevicesViewModel _devices;
    private DateTime _hiddenAt;
    private bool _anchorBottom;

    public FlyoutWindow(DevicesViewModel devices)
    {
        _devices = devices;
        InitializeComponent();
        DataContext = devices;
        devices.Changed += UpdateEmpty;
        UpdateEmpty();
        SourceInitialized += (_, _) =>
        {
            Theme.StyleWindow(this, toolWindow: true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeepBottomEdge);
        };
        SizeChanged += (_, _) => { if (IsVisible) PlaceNearTray(); };
    }

    /// <summary>
    /// Clicking the tray icon while the panel is open first deactivates (hides) it, then the click would reopen it.
    /// Treat a click right after hiding as "close".
    /// </summary>
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 300) return;
        Show();
        UpdateLayout();
        PlaceNearTray();
        Activate();
    }

    private void UpdateEmpty() => EmptyText.Visibility = _devices.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Bottom-right above the taskbar (or next to it when the taskbar is on another edge).</summary>
    private void PlaceNearTray()
    {
        var work = SystemParameters.WorkArea;
        const double gap = 12;
        double screenW = SystemParameters.PrimaryScreenWidth, screenH = SystemParameters.PrimaryScreenHeight;
        bool taskbarOnTop = work.Top > 0 && work.Bottom >= screenH;
        _anchorBottom = !taskbarOnTop;
        Left = work.Left > 0 ? work.Left + gap : work.Right - ActualWidth - gap;           // taskbar on the left
        Top = taskbarOnTop ? work.Top + gap : work.Bottom - ActualHeight - gap;
        if (work.Right < screenW && work.Left == 0) Left = work.Right - ActualWidth - gap;  // taskbar on the right
    }

    // ---- Smooth growing: a tile's drawer animates the panel's height every frame. The panel sizes to its content,
    // which Windows does by keeping the top edge and moving the bottom; repositioning afterwards made the bottom edge
    // jump on every frame. Instead, move the top in the same step, so the bottom edge stays put (owner, 2026-10-28). ----

    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos { public IntPtr Hwnd, HwndInsertAfter; public int X, Y, Cx, Cy; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    private IntPtr KeepBottomEdge(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging || !_anchorBottom || !IsVisible) return IntPtr.Zero;
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        // Only a pure resize (size changes, no move requested): keep the current bottom edge.
        if ((pos.Flags & SwpNoSize) != 0 || (pos.Flags & SwpNoMove) == 0) return IntPtr.Zero;
        if (!GetWindowRect(hwnd, out var r) || pos.Cy == r.Bottom - r.Top) return IntPtr.Zero;
        pos.X = r.Left;
        pos.Y = r.Bottom - pos.Cy;
        pos.Flags &= ~SwpNoMove;
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        MenuPopup.IsOpen = false;
        Hide();
        _hiddenAt = DateTime.Now;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    /// <summary>The menu shown from ⋯ (TrayIcon wires it to the same actions as the tray right-click menu).</summary>
    public AppMenuView Menu => MenuView;

    private DateTime _menuClosedAt;

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        // A click on ⋯ while the menu is open closes it (StaysOpen=False) before this runs: don't reopen it.
        if ((DateTime.Now - _menuClosedAt).TotalMilliseconds < 300) return;
        ((App)Application.Current).Tray.RefreshMenus();
        MenuPopup.IsOpen = true;
    }

    private void OnMenuPopupClosed(object? sender, EventArgs e) => _menuClosedAt = DateTime.Now;

    /// <summary>After an action (Exit, Open log folder…), close the menu.</summary>
    public void CloseMenu() => MenuPopup.IsOpen = false;
}
