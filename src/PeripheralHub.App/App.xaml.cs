using System.Windows;

namespace PeripheralHub.App;

/// <summary>
/// Tray app: no main window. The tray icon opens the panel; the widget shows the same devices on the desktop.
/// The data layer (<see cref="DeviceHub"/>) runs in-process.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private DeviceHub? _hub;
    private PeripheralHubOptions? _hubOptions;
    private AppSettings _settings = new();

    public DevicesViewModel Devices { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;
    public AppUpdater Updater { get; private set; } = null!;
    private FlyoutWindow _flyout = null!;
    private WidgetWindow _widget = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Developer helper: regenerate Assets\app.ico (plus a preview sheet) from IconFactory.
        if (e.Args.Length == 2 && e.Args[0] == "--write-icon") { IconFactory.WriteFiles(e.Args[1]); Shutdown(); return; }
        // Developer helper: every device picture on dark and light tiles, drawn by DeviceArt.
        if (e.Args.Length == 2 && e.Args[0] == "--write-art") { DeviceArt.WritePreview(e.Args[1]); Shutdown(); return; }

        // Elevated helper runs (started from the tray's "Show hidden controllers" with an admin prompt): do the one job and exit.
        if (e.Args is [HidHideSetup.AllowArg]) { Shutdown(HidHideSetup.RunAllow()); return; }
        if (e.Args is [HidHideSetup.RemoveArg]) { Shutdown(HidHideSetup.RunRemove()); return; }

        // Second launch: ask the running instance to open its panel, then quit.
        var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PeripheralHub.App.Open");
        _singleInstance = new Mutex(true, @"Local\PeripheralHub.App", out bool first);
        if (!first) { openSignal.Set(); Shutdown(); return; }
        new Thread(() => { while (openSignal.WaitOne()) Dispatcher.BeginInvoke(() => _flyout.Toggle()); })
            { IsBackground = true, Name = "OpenSignal" }.Start();

        AppLog.CatchUnhandled(this);
        AppLog.Write($"PeripheralHub {AppLog.Version} starting from {Environment.ProcessPath} " +
                     $"(Windows {Environment.OSVersion.Version}, .NET {Environment.Version})");

        Theme.Initialize();
        _settings = AppSettings.Load();
        Devices = new DevicesViewModel(_settings);
        _hubOptions = new PeripheralHubOptions { Log = AppLog.Write, SwitchBluetoothControllers = _settings.ReadBluetoothControllerBattery };
        _hub = new DeviceHub(_hubOptions);
        _hub.PluginActivated += p => AppLog.Write($"Device detected: turned on plugin {p.Manifest.Id}");
        AppLog.Write($"Plugins available: {string.Join(", ", _hub.Plugins.Available.Select(p => $"{p.Manifest.Id} {p.Manifest.Version}"))}; " +
                     $"active: {string.Join(", ", _hub.Plugins.Active.Select(p => p.Manifest.Id))}");
        PluginAssets.Plugins = _hub.Plugins;
        DeviceViewModel.SetMonitorLevel = (id, setting, percent) => _hub.SetMonitorLevelAsync(id, setting, percent);
        DeviceViewModel.SetMonitorMute = (id, muted) => _hub.SetMonitorMuteAsync(id, muted);

        _flyout = new FlyoutWindow(Devices);
        _widget = new WidgetWindow(Devices, _settings);

        Updater = new AppUpdater(Dispatcher);
        Tray = new TrayIcon(Devices, _settings, Updater);
        Tray.AttachMenu(_flyout.Menu);
        _flyout.Menu.ActionInvoked += _flyout.CloseMenu;
        Tray.OpenRequested += () => _flyout.Toggle();
        Tray.ExitRequested += Shutdown;
        Tray.WidgetToggled += () =>
        {
            _settings.WidgetHidden = !_settings.WidgetHidden;
            _settings.Save();
            _widget.UpdateVisibility();
        };
        Tray.WidgetOnTopToggled += ToggleWidgetOnTop;
        Tray.SonyBatteryChanged += full =>
        {
            if (_settings.ReadBluetoothControllerBattery == full) return;
            _settings.ReadBluetoothControllerBattery = full;
            _settings.Save();
            _hubOptions!.SwitchBluetoothControllers = _settings.ReadBluetoothControllerBattery;
            AppLog.Write($"Show Sony controller battery over Bluetooth: {(full ? "on" : "off")}");
        };

        _hub.DevicesChanged += (_, args) => Dispatcher.BeginInvoke(() => Devices.Apply(args.Devices, args.PairedBluetooth));
        // Tiles with their controls open are followed live (the Nothing plugin keeps the earbuds' channel open, monitors
        // are read every 3 s), but only while someone can see them: the panel or widget is showing and the PC isn't locked.
        _flyout.IsVisibleChanged += (_, _) => UpdateWatched();
        _widget.IsVisibleChanged += (_, _) => UpdateWatched();
        Devices.WatchedChanged += _ => UpdateWatched();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        UpdateWatched();
        _hub.Start();
        Updater.Start();

        if (e.Args.Contains("--open"))
            Dispatcher.BeginInvoke(async () => { await Task.Delay(1500); _flyout.Toggle(); });
    }

    private bool _locked;

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        bool locked = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect => true,
            Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect => false,
            _ => _locked,
        };
        Dispatcher.BeginInvoke(() => { _locked = locked; UpdateWatched(); });
    }

    private bool _watching;

    /// <summary>Hands the hub the open tiles while one of our windows is on screen, else nothing (drivers fall back to their slow poll).</summary>
    private void UpdateWatched()
    {
        bool watching = !_locked && (_flyout.IsVisible || _widget.IsVisible);
        _hub?.SetWatched(watching ? Devices.WatchedIds : []);
        if (watching != _watching) AppLog.Write(watching ? "Following open tiles live" : "Stopped following open tiles (nothing on screen)");
        _watching = watching;
    }

    /// <summary>From a tile's noise-control row. Returns null on success, else a message for the user.</summary>
    public Task<string?> SetNoiseControlAsync(string deviceId, NoiseMode mode, NoiseStrength? strength) =>
        _hub is null ? Task.FromResult<string?>("Not ready yet.") : _hub.SetNoiseControlAsync(deviceId, mode, strength);

    /// <summary>From a headset tile's drawer. Returns null on success, else a message for the user.</summary>
    public Task<string?> MakeDefaultSoundDeviceAsync(string deviceId) =>
        _hub is null ? Task.FromResult<string?>("Not ready yet.") : _hub.MakeDefaultSoundDeviceAsync(deviceId);

    public void ToggleWidgetOnTop()
    {
        _settings.WidgetOnTop = !_settings.WidgetOnTop;
        _settings.Save();
        _widget.ApplyLayer();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hub is not null) AppLog.Write("PeripheralHub exiting");
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Tray?.Dispose();
        _hub?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
