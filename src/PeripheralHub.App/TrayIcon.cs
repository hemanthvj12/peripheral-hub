using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PeripheralHub.App;

/// <summary>Notification-area icon (<see cref="IconFactory"/> badge). Hovering lists every device's battery.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;
    private readonly DevicesViewModel _devices;
    private readonly AppSettings _settings;
    private readonly AppMenuWindow _menu = new();
    private Integration.HidHideState _hidHide = Integration.HidHideState.NotInstalled;
    private bool _hidHideBusy;
    private readonly AppUpdater _updater;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? WidgetToggled;
    public event Action? WidgetOnTopToggled;
    public event Action<bool>? SonyBatteryChanged;

    public TrayIcon(DevicesViewModel devices, AppSettings settings, AppUpdater updater)
    {
        _devices = devices;
        _settings = settings;
        _updater = updater;

        // The app's own menu (tray right-click; the panel's ⋯ shows the same view in a pop-up, see AttachMenu),
        // replacing the Windows Forms menu (owner, 2026-10-28).
        AttachMenu(_menu.View);
        _ = RefreshHidHideAsync();
        // No notification when an update is ready (owner: no notifications of any kind); the menu shows it.
        updater.PropertyChanged += (_, _) => RefreshMenu();

        _image = IconFactory.CreateIcon(Forms.SystemInformation.SmallIconSize.Width);
        _icon = new Forms.NotifyIcon { Icon = _image, Visible = true, Text = "Peripherals" };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) ShowMenuAtCursor(); };
        devices.Changed += UpdateTooltip;
        // The Sony battery row appears or goes as a Sony controller is paired or unpaired.
        devices.Changed += () =>
        {
            if (_sonyRowShown != devices.SonyControllerPaired) RefreshMenu();
        };
        UpdateTooltip();
    }

    private readonly List<AppMenuView> _views = [];
    private bool _sonyRowShown;

    /// <summary>Wires a copy of the menu (the tray window's, the panel's pop-up) to the actions; all copies show the same state.</summary>
    public void AttachMenu(AppMenuView view)
    {
        _views.Add(view);
        view.WidgetToggled += () => { WidgetToggled?.Invoke(); RefreshMenu(); };
        view.OnTopToggled += () => { WidgetOnTopToggled?.Invoke(); RefreshMenu(); };
        view.VirtualToggled += () => { _devices.ShowVirtualControllers = !_devices.ShowVirtualControllers; RefreshMenu(); };
        view.SonyBatteryToggled += () => { SonyBatteryChanged?.Invoke(!_settings.ReadBluetoothControllerBattery); RefreshMenu(); };
        view.HidHideToggled += async () => await ToggleHidHideAsync();
        view.StartupToggled += () => { ToggleStartup(); RefreshMenu(); };
        view.UpdateClicked += async () =>
        {
            if (!AppUpdater.AutomaticUpdates) OpenInShell(AppUpdater.ReleasesPage);
            else if (_updater.ReadyVersion is not null) _updater.ApplyAndRestart();
            else await _updater.CheckAsync(manual: true);
        };
        view.LogClicked += () =>
        {
            System.IO.Directory.CreateDirectory(AppLog.Folder);
            OpenInShell(AppLog.Folder);
        };
        view.ExitClicked += () => ExitRequested?.Invoke();
        RefreshMenu();
    }

    /// <summary>Brings every copy of the menu up to date (call before showing one).</summary>
    public void RefreshMenus()
    {
        RefreshMenu();
        _ = RefreshHidHideAsync();
    }

    public void ShowMenuAtCursor()
    {
        RefreshMenus();
        _menu.ShowAtCursor();
    }

    private void RefreshMenu()
    {
        // HidHide row only when HidHide is installed. On = PeripheralHub can see the controllers HidHide hides (DSX turns
        // hiding on whenever it runs). Clicking adds or removes it, with one admin prompt.
        string hidHideTip =
            _hidHide.Error is not null ? $"Couldn't read HidHide's settings: {_hidHide.Error}"
            : _hidHide.InverseMode ? "HidHide is set to block only the apps on its list; change that in HidHide Configuration Client."
            : _hidHide.Allowed ? "PeripheralHub can see controllers that HidHide hides (e.g. while DSX runs). Click to remove it from HidHide's allowed apps (admin prompt)."
            : "Controllers that HidHide hides (e.g. while DSX runs) are invisible to PeripheralHub. Click to add it to HidHide's allowed apps (admin prompt).";

        // Updates only exist for a copy installed with Setup. With automatic updates off, it opens the releases page.
        string updateText =
            !AppUpdater.AutomaticUpdates ? "Get new versions…" :
            _updater.ReadyVersion is { } ready ? $"Restart to update to {ready}" :
            _updater.Checking ? "Checking for updates…" :
            _updater.LastResult is { } result ? $"Check for updates ({result})" :
            "Check for updates";
        string? updateTip = AppUpdater.AutomaticUpdates ? null
            : "Opens the releases page on GitHub. Download the newest PeripheralHub-win-Setup.exe and run it: it updates this copy and keeps your settings.";

        var state = new AppMenuView.State(
            Widget: !_settings.WidgetHidden, OnTop: _settings.WidgetOnTop, Virtual: _settings.ShowVirtualControllers,
            SonyBatteryVisible: _devices.SonyControllerPaired, SonyBattery: _settings.ReadBluetoothControllerBattery,
            Startup: StartupRegistration.IsEnabled,
            HidHideVisible: _hidHide.Installed, HidHideOn: _hidHide.Allowed,
            HidHideEnabled: !_hidHideBusy && _hidHide.Error is null && !_hidHide.InverseMode, HidHideTooltip: hidHideTip,
            UpdateVisible: _updater.IsInstalled, UpdateEnabled: !_updater.Checking, UpdateText: updateText, UpdateTooltip: updateTip);
        _sonyRowShown = state.SonyBatteryVisible;
        foreach (var v in _views) v.Refresh(state);
    }
    private void OpenInShell(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _icon.ShowBalloonTip(3000, "Peripherals", $"Couldn't open {target}: {ex.Message}", Forms.ToolTipIcon.Warning);
        }
    }

    private async Task RefreshHidHideAsync()
    {
        var exe = Environment.ProcessPath!;
        var state = await Task.Run(() => Integration.HidHide.Check(exe));
        if (state == _hidHide) return;
        _hidHide = state;
        RefreshMenu();
    }

    private async Task ToggleHidHideAsync()
    {
        bool allow = !_hidHide.Allowed;
        _hidHideBusy = true;
        RefreshMenu();
        var (ok, error) = await HidHideSetup.RequestAsync(allow);
        AppLog.Write($"HidHide: {(allow ? "allow" : "remove")} {(ok ? "done" : error is null ? "cancelled" : "failed: " + error)}");
        if (!ok && error is not null)
            _icon.ShowBalloonTip(3000, "Peripherals", $"Couldn't change HidHide's allowed apps: {error}", Forms.ToolTipIcon.Warning);
        _hidHideBusy = false;
        await RefreshHidHideAsync();
        RefreshMenu();
    }

    private void ToggleStartup()
    {
        try { StartupRegistration.Set(!StartupRegistration.IsEnabled); }
        catch (Exception ex) { _icon.ShowBalloonTip(3000, "Peripherals", $"Couldn't change startup setting: {ex.Message}", Forms.ToolTipIcon.Warning); }
    }

    /// <summary>One line per battery device (NotifyIcon text is limited to 127 characters; monitors have no battery to list).</summary>
    private void UpdateTooltip()
    {
        var lines = _devices.Devices.Where(d => d.InBatteryList).Select(d =>
            $"{d.Name}: {(d.IsOnline ? (d.HasBattery ? d.BatteryText : d.IsCharging ? "charging" : "online") : d.Snapshot.State.ToString().ToLowerInvariant())}");
        string text = string.Join("\n", lines);
        if (text.Length == 0) text = "Peripherals: no devices";
        _icon.Text = text.Length > 127 ? text[..124] + "..." : text;
    }

    public void Dispose()
    {
        _menu.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
