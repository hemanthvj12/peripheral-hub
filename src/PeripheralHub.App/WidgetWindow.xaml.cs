using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PeripheralHub.App;

/// <summary>
/// Small window with the same devices as the panel. Two modes:
/// <list type="bullet">
/// <item>Desktop (default): owned by the desktop window (Progman) and kept at the bottom of the z-order, so it sits on
/// the desktop below every app window, and comes along when the desktop is shown (Win+D).</item>
/// <item>Always on top: a normal topmost window.</item>
/// </list>
/// As a tool window it stays out of the taskbar and Alt+Tab either way.
/// </summary>
public partial class WidgetWindow : Window
{
    private readonly DevicesViewModel _devices;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;

    public WidgetWindow(DevicesViewModel devices, AppSettings settings)
    {
        _devices = devices;
        _settings = settings;
        InitializeComponent();
        DataContext = devices;
        SourceInitialized += (_, _) =>
        {
            Theme.StyleWindow(this, toolWindow: true);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
            ApplyLayer();
        };
        Loaded += (_, _) => RestorePosition();
        devices.Changed += UpdateVisibility;

        // After a system restart the widget once came up as an empty frame: Windows lost its first drawn frame and
        // WPF only redraws what changes. Repaint everything after events that can drop it, and every 30 s to self-heal.
        var heal = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        heal.Tick += (_, _) => Repaint();
        heal.Start();
        SystemEvents.SessionSwitch += (_, _) => Dispatcher.BeginInvoke(Repaint);
        SystemEvents.PowerModeChanged += (_, _) => Dispatcher.BeginInvoke(Repaint);
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Repaint);
    }

    /// <summary>Makes WPF redraw the whole window (its WM_PAINT handler invalidates the full render target).</summary>
    private void Repaint()
    {
        if (_hwnd != IntPtr.Zero && IsVisible) InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>Visible when there's a device (the same list as the panel), unless the user hid it.</summary>
    public void UpdateVisibility()
    {
        bool show = _devices.Devices.Count > 0 && !_settings.WidgetHidden;
        if (show && !IsVisible) { Show(); ApplyLayer(); }
        else if (!show && IsVisible) Hide();
        OnTopMenuItem.IsChecked = _settings.WidgetOnTop;
    }

    /// <summary>Switches between desktop mode and always-on-top according to settings.</summary>
    public void ApplyLayer()
    {
        OnTopMenuItem.IsChecked = _settings.WidgetOnTop;
        if (_hwnd == IntPtr.Zero) return;
        if (_settings.WidgetOnTop)
        {
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
            Topmost = true;
        }
        else
        {
            Topmost = false;
            // Owned windows always stay above their owner; owning it by the desktop keeps it visible on Win+D.
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, FindWindow("Progman", null));
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>In desktop mode, refuse every attempt to raise the window (clicks, drags) by forcing it to the bottom.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046, WM_DISPLAYCHANGE = 0x007E, WM_THEMECHANGED = 0x031A, WM_DWMCOMPOSITIONCHANGED = 0x031E;
        if (msg is WM_DISPLAYCHANGE or WM_THEMECHANGED or WM_DWMCOMPOSITIONCHANGED) Dispatcher.BeginInvoke(Repaint);
        if (msg == WM_WINDOWPOSCHANGING && !_settings.WidgetOnTop)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private void RestorePosition()
    {
        var work = SystemParameters.WorkArea;
        double left = _settings.WidgetLeft ?? work.Right - ActualWidth - 24;
        double top = _settings.WidgetTop ?? work.Top + 120; // below typical title bars/window buttons
        // Keep it on screen if the display layout changed since last time.
        Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40);
        Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        double left = Left, top = Top;
        DragMove();
        // A click that didn't move the widget opens/closes the clicked tile's drawer.
        if (Math.Abs(Left - left) < 1 && Math.Abs(Top - top) < 1)
        {
            for (var d = e.OriginalSource as DependencyObject; d is not null;
                 d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is DeviceTile tile) { tile.ToggleDrawer(); break; }
            return;
        }
        _settings.WidgetLeft = Left;
        _settings.WidgetTop = Top;
        _settings.Save();
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        _settings.WidgetHidden = true;
        _settings.Save();
        UpdateVisibility();
    }

    private void OnToggleOnTop(object sender, RoutedEventArgs e) => ((App)Application.Current).ToggleWidgetOnTop();


    // ---- Win32 ----
    private const int GWLP_HWNDPARENT = -8;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
