using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PeripheralHub.App;

/// <summary>The menu (<see cref="AppMenuView"/>) as its own window, for the tray icon's right-click.</summary>
public partial class AppMenuWindow : Window
{
    public AppMenuWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.StyleWindow(this, toolWindow: true);
        View.ActionInvoked += Hide;
    }

    /// <summary>Opens next to the mouse pointer, like a context menu: up and to the left near the taskbar, kept on screen.</summary>
    public void ShowAtCursor()
    {
        // Right-clicking the tray icon while the menu is open first closes it (focus moves), then the click would
        // reopen it at once: treat a click right after closing as "close".
        if (!IsVisible && (DateTime.Now - _hiddenAt).TotalMilliseconds < 300) return;
        if (!IsVisible) { Opacity = 0; Show(); }
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        double x = cursor.X / dpi.DpiScaleX, y = cursor.Y / dpi.DpiScaleY;
        var work = SystemParameters.WorkArea;
        double w = ActualWidth, h = ActualHeight;
        Left = Math.Clamp(x - w >= work.Left ? x - w : x, work.Left, work.Right - w);
        Top = Math.Clamp(y - h >= work.Top ? y - h : y, work.Top, work.Bottom - h);
        Opacity = 1;
        Activate();
    }

    private DateTime _hiddenAt;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Hide();
        _hiddenAt = DateTime.Now;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }
}
