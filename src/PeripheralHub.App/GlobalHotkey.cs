using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PeripheralHub.App;

/// <summary>
/// A system-wide keyboard shortcut (owner, 2026-11-01: Ctrl+Alt+P opens and closes the panel). Registered with Windows
/// on a hidden message window; if another app already owns the combination, registration fails and it's only logged.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    [Flags]
    public enum Modifiers : uint { Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8 }

    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000; // holding the keys down fires once
    private const int Id = 1;

    private readonly HwndSource _window;
    private readonly Action _pressed;

    public bool IsRegistered { get; }

    public GlobalHotkey(Modifiers modifiers, uint virtualKey, Action pressed)
    {
        _pressed = pressed;
        // A message-only window (parent HWND_MESSAGE): receives WM_HOTKEY, never shown.
        _window = new HwndSource(new HwndSourceParameters("PeripheralHub hotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(OnMessage);
        IsRegistered = RegisterHotKey(_window.Handle, Id, (uint)modifiers | ModNoRepeat, virtualKey);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id) { handled = true; _pressed(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(_window.Handle, Id);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
