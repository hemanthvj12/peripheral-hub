using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PeripheralHub.App;

/// <summary>Display-ready view of one <see cref="DeviceSnapshot"/>. All wording/formatting decisions live here.</summary>
public sealed class DeviceViewModel(DeviceSnapshot snapshot) : INotifyPropertyChanged
{
    private DeviceSnapshot _s = snapshot;
    private bool _pinned;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _s.Id;

    /// <summary>When the device joined the list while the app was already running (its tiles grow in); null at startup.</summary>
    public DateTime? AddedAt { get; init; }

    private bool _leaving;

    /// <summary>The device is dropping off the list: its tiles shrink away before it's removed.</summary>
    public bool IsLeaving
    {
        get => _leaving;
        set { if (_leaving != value) { _leaving = value; Raise(); } }
    }
    public DeviceSnapshot Snapshot => _s;

    public void Update(DeviceSnapshot s)
    {
        // The hub publishes every scan (2 s) even when nothing changed; telling WPF "everything changed" each time made
        // every tile re-read all its bindings and rebuild its battery rows, which was most of the app's garbage.
        bool differs = s.DiffersFrom(_s);
        _s = s;
        if (!differs) return;
        _parts = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // everything may have changed
    }

    public bool IsPinned
    {
        get => _pinned;
        set { if (_pinned != value) { _pinned = value; Raise(); Raise(nameof(PinGlyph)); Raise(nameof(PinTooltip)); } }
    }

    public string Name => _s.Name;

    /// <summary>
    /// The device's line-art picture (<see cref="DeviceArt"/>); null (no drawing for it) falls back to <see cref="KindGlyph"/>.
    /// </summary>
    public ImageSource? Image => DeviceArt.For(_s);
    public bool HasImage => Image is not null;

    // Segoe Fluent Icons / Segoe MDL2 Assets glyphs.
    public string KindGlyph => _s.Kind switch
    {
        DeviceKind.Monitor => "", // TVMonitor
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Audio => "",
        _ => "",
    };

    public bool IsOnline => _s.State == LinkState.Online;

    /// <summary>Several batteries (earbuds: left, right, case): the tile shows them in a row instead of one level.</summary>
    public bool HasParts => _s.Parts is { Count: > 0 };
    // Kept until the snapshot changes: a new list makes the tile's battery rows rebuild their templates.
    public IReadOnlyList<PartViewModel> Parts =>
        _parts ??= _s.Parts?.Select(p => new PartViewModel(p.Label, p.Percent, p.Charging, p.Live && IsOnline)).ToList() ?? [];
    private IReadOnlyList<PartViewModel>? _parts;
    /// <summary>The single battery number and icon; hidden for multi-battery devices and monitors.</summary>
    public bool ShowSingleBattery => !HasParts && !IsMonitor;

    // ---- Monitors: resolution and refresh rate where the battery goes; brightness/volume sliders in the drawer ----
    public bool IsMonitor => _s.Kind == DeviceKind.Monitor;
    /// <summary>The badge is a monitor's video link (DisplayPort, HDMI…): its own colour.</summary>
    public bool IsDisplayLink => IsMonitor && _s.Connection is not (ConnectionType.None or ConnectionType.Virtual);
    public bool HasMonitorMode => _s.Monitor is { Width: > 0 };
    public string ResolutionText => _s.Monitor is { Width: > 0 } m ? $"{m.Width}×{m.Height}" : "";
    public string RefreshText => _s.Monitor is { RefreshHz: > 0 } m ? $"{m.RefreshHz} Hz" : "";
    public bool HasBrightness => IsOnline && _s.Monitor?.Brightness is not null;
    public bool HasVolume => IsOnline && _s.Monitor?.Volume is not null;
    public bool HasMonitorControls => HasBrightness || HasVolume;

    private LevelSender? _brightness, _volume;

    /// <summary>Set by the app: changes a monitor setting (device id, setting, 0-100); returns null or an error.</summary>
    public static Func<string, MonitorSetting, int, Task<string?>>? SetMonitorLevel { get; set; }

    /// <summary>Brightness 0-100, two-way bound to the slider: dragging sends the value (at most every 150 ms).</summary>
    public int BrightnessPercent
    {
        get => (_brightness ??= NewSender(MonitorSetting.Brightness)).Shown(_s.Monitor?.Brightness?.Percent ?? 0);
        set => (_brightness ??= NewSender(MonitorSetting.Brightness)).Set(value);
    }

    public int VolumePercent
    {
        get => (_volume ??= NewSender(MonitorSetting.Volume)).Shown(_s.Monitor?.Volume?.Percent ?? 0);
        set => (_volume ??= NewSender(MonitorSetting.Volume)).Set(value);
    }

    // ---- Sound devices (USB headsets, Bluetooth audio): a "Set as default" button in the drawer ----
    public bool HasSound => IsOnline && _s.Sound is not null;
    public bool IsDefaultSound => _s.Sound?.IsDefault == true;
    public bool CanSetDefaultSound => HasSound && !IsDefaultSound;
    public string SoundButtonText => IsDefaultSound ? "Default sound device" : "Set as default";
    public string SoundButtonGlyph => IsDefaultSound ? "" : ""; // CheckMark / Volume
    // USB headsets switch speakers and mic; Bluetooth audio only its speakers (HasInput false).
    public string SoundTooltip => (IsDefaultSound, _s.Sound?.HasInput == true) switch
    {
        (true, true) => "Windows plays sound through this headset and uses its mic",
        (true, false) => "Windows plays sound through this device",
        (false, true) => "Make this headset Windows' sound output and mic",
        (false, false) => "Make this Windows' sound output (the mic stays as it is)",
    };

    // ---- Monitor speaker mute: the speaker icon left of the volume slider is a button ----

    /// <summary>Set by the app: mutes/unmutes a monitor (device id, muted); returns null or an error.</summary>
    public static Func<string, bool, Task<string?>>? SetMonitorMute { get; set; }

    private bool? _muteWanted;
    private DateTime _muteHoldUntil;

    public bool CanMute => HasVolume && _s.Monitor?.Muted is not null;
    /// <summary>Shows the user's click at once; the monitor's own reading takes over after a few seconds.</summary>
    public bool IsMuted => _muteWanted is { } w && DateTime.UtcNow < _muteHoldUntil ? w : _s.Monitor?.Muted == true;
    public string VolumeGlyph => IsMuted ? "" : ""; // Mute / Volume
    public string MuteTooltip => !CanMute ? "Speaker volume" : IsMuted ? "Muted. Click to unmute" : "Click to mute";

    public async Task ToggleMuteAsync()
    {
        if (!CanMute || SetMonitorMute is null) return;
        bool muted = !IsMuted;
        _muteWanted = muted;
        _muteHoldUntil = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        RaiseMute();
        string? error;
        try { error = await SetMonitorMute(Id, muted); }
        catch (Exception) { error = "failed"; }
        if (error is not null) { _muteWanted = null; RaiseMute(); return; }
        // When the hold ends the monitor's own reading shows again; tiles only refresh on changes, so ask for it.
        var left = _muteHoldUntil - DateTime.UtcNow;
        if (left > TimeSpan.Zero) await Task.Delay(left + TimeSpan.FromMilliseconds(50));
        RaiseMute();
    }

    private void RaiseMute() { Raise(nameof(IsMuted)); Raise(nameof(VolumeGlyph)); Raise(nameof(MuteTooltip)); }

    private LevelSender NewSender(MonitorSetting setting) => new(
        v => SetMonitorLevel?.Invoke(Id, setting, v) ?? Task.FromResult<string?>(null),
        () => Raise(setting == MonitorSetting.Brightness ? nameof(BrightnessPercent) : nameof(VolumePercent)));

    public bool HasBattery => _s.BatteryPercent is not null;
    public double BatteryPercent => _s.BatteryPercent ?? 0;
    public string BatteryText =>
        _s.BatteryLabel is { } label && _s.BatteryPercent is not null ? label  // coarse levels, e.g. "Medium" (Xbox)
        : _s.BatteryPercent is { } p ? $"{p}%"
        : LevelBeforeCharging is { } was ? $"was {was}%"
        : _s.BatteryUnavailable && IsOnline ? "Basic mode" // Sony controller on Bluetooth in Basic mode, no level known yet
        : "";

    private int? _levelBeforeCharging;

    /// <summary>
    /// For devices that hide their level while charging (the F75 reports a pinned 100): the last level seen before
    /// the cable went in, shown as "was 82%". Also used for a level that can't be read right now
    /// (<see cref="DeviceSnapshot.BatteryUnavailable"/>). Set by <see cref="DevicesViewModel"/>.
    /// </summary>
    public int? LevelBeforeCharging
    {
        get => _levelBeforeCharging;
        set
        {
            if (_levelBeforeCharging == value) return;
            _levelBeforeCharging = value;
            Raise(); Raise(nameof(BatteryText)); Raise(nameof(IsLevelBeforeCharging)); Raise(nameof(IsBatteryTextDim));
        }
    }

    public bool IsLevelBeforeCharging => !HasBattery && LevelBeforeCharging is not null;
    /// <summary>The battery text is a stand-in ("was 82%", "Basic mode"), shown smaller and grey.</summary>
    public bool IsBatteryTextDim => !HasBattery && (LevelBeforeCharging is not null || _s.BatteryUnavailable);
    /// <summary>Fill width inside the battery icon (its inner width is 17 units, see BatteryIcon in App.xaml).</summary>
    public double BatteryFillWidth => BatteryPercent <= 0 ? 0 : Math.Max(2, 17 * BatteryPercent / 100);

    /// <summary>Show the battery icon when there's a level, or an empty one with a bolt while charging without a level.</summary>
    public bool ShowBatteryIcon => HasBattery || ShowBolt;

    /// <summary>Colour band for the battery icon: Charging (green), High ≥ 50 (green), Medium 21-49 (amber), Low ≤ 20 (red), Away (grey).</summary>
    public string BatteryLevel =>
        !IsOnline ? "Away"
        : ShowBolt ? "Charging"
        : _s.BatteryPercent switch { >= 50 => "High", > 20 => "Medium", _ => "Low" };

    public bool IsCharging => IsOnline && _s.Charging is ChargeState.Charging;
    public bool IsFull => IsOnline && _s.Charging is ChargeState.Full;
    public bool ShowBolt => IsCharging || IsFull;
    public bool IsLow => IsOnline && !IsCharging && _s.BatteryPercent is <= 20;
    public bool HasError => IsOnline && _s.Charging is ChargeState.Error;

    // ---- Noise control row (Nothing Ear (2); Abyss X-style pill + strength dots, owner 2026-10-28) ----
    public bool HasNoiseControl => _s.NoiseControl is not null;
    public bool CanChangeNoise => _s.NoiseControl is { CanChange: true };
    public bool IsNoiseCancellation => _s.NoiseControl?.Mode == NoiseMode.Cancellation;
    public bool IsNoiseTransparency => _s.NoiseControl?.Mode == NoiseMode.Transparency;
    public bool IsNoiseOff => _s.NoiseControl?.Mode == NoiseMode.Off;
    private NoiseStrength? Strength => _s.NoiseControl?.Strength;
    public bool IsStrengthHigh => Strength == NoiseStrength.High;
    public bool IsStrengthMid => Strength == NoiseStrength.Mid;
    public bool IsStrengthLow => Strength == NoiseStrength.Low;
    public bool IsStrengthAdaptive => Strength == NoiseStrength.Adaptive;
    private bool _noiseExpanded;

    /// <summary>
    /// The tile's drawer (noise control, "Open &lt;maker's app&gt;") is folded away by default; a click on the tile or the
    /// chevron opens/closes it (owner, 2026-10-28).
    /// </summary>
    public bool IsExpanded
    {
        get => _noiseExpanded;
        set { if (_noiseExpanded != value) { _noiseExpanded = value; Raise(); Raise(nameof(ShowDrawer)); Raise(nameof(Tooltip)); } }
    }
    public bool ShowDrawer => HasExpandableControls && IsExpanded;
    /// <summary>
    /// The device has a drawer: noise control, monitor sliders, or a maker's app to open. Not while disconnected (it
    /// folds away).
    /// </summary>
    public bool HasExpandableControls => (HasNoiseControl || HasMonitorControls || HasSound || HasVendorApp || HasLightHub) && _s.State != LinkState.Disconnected;

    /// <summary>The tray tooltip lists battery devices: monitors and wired sound devices have nothing to show there.</summary>
    public bool InBatteryList => !IsMonitor && !(_s.Sound is not null && !HasBattery);
    public string OpenVendorText => $"Open {VendorApp?.DisplayName ?? "the maker's app"}";

    public string NoiseTooltip => _s.NoiseControl switch
    {
        null => "",
        { Busy: true } => "Switching…",
        { Reason: { } reason } => reason,
        _ => "Noise control",
    };

    public ConnectionType Connection => _s.Connection;
    public bool HasConnection => _s.Connection != ConnectionType.None;

    // ---- HapticLink (the owner's separate app) is giving games this DualSense as a wired one: animated waves on its
    //      picture (owner's idea, 2026-11-01) and a tooltip line. Set by DevicesViewModel from HapticLink's status. ----
    private bool _hapticLinked;
    public bool IsHapticLinked
    {
        get => _hapticLinked;
        set { if (_hapticLinked != value) { _hapticLinked = value; Raise(); Raise(nameof(HasMarker)); Raise(nameof(Tooltip)); } }
    }
    /// <summary>The vibration waves on the picture.</summary>
    public bool HasMarker => _hapticLinked && IsOnline;

    public string ConnectionText => _s.Connection switch
    {
        ConnectionType.Wired => "Wired",
        ConnectionType.Dongle => "2.4G",
        ConnectionType.Bluetooth => "Bluetooth",
        // The grey badge with the cloud icon marks it as virtual; its text names the app that made it when known
        // ("Punktfunk"), so it fits the narrow widget (owner, 2026-10-28).
        ConnectionType.Virtual => _s.Source is { Length: > 0 } src ? $"Virtual ({src})" : "Virtual",
        ConnectionType.DisplayPort => "DisplayPort",
        ConnectionType.Hdmi => "HDMI",
        ConnectionType.UsbC => "USB-C",
        ConnectionType.Dvi => "DVI",
        ConnectionType.Vga => "VGA",
        ConnectionType.BuiltIn => "Built-in",
        ConnectionType.Wireless => "Wireless",
        _ => "",
    };

    private string DisplayLinkGlyph => _s.Connection switch
    {
        ConnectionType.Wireless => "", // Cast (Miracast)
        ConnectionType.BuiltIn => "",  // laptop
        _ => "",                      // Connect: a video cable
    };

    public string ConnectionGlyph => IsDisplayLink ? DisplayLinkGlyph : _s.Connection switch
    {
        ConnectionType.Wired => "",     // USB
        ConnectionType.Dongle => "",    // wireless
        ConnectionType.Bluetooth => "", // Bluetooth
        ConnectionType.Virtual => "",  // cloud: created by software (game streaming, remapping)
        _ => "",
    };

    /// <summary>Text after the connection badge.</summary>
    public string StatusText => _s.State switch
    {
        // Charging/full is shown by the bolt in the battery icon, and Paused (vendor app open) by the tooltip.
        // Only a charging fault needs words.
        LinkState.Online => _s.Charging == ChargeState.Error ? "Charging error" : "",
        LinkState.Asleep => "Asleep",
        LinkState.Offline => "Offline",
        _ => "Disconnected",
    };

    /// <summary>The device maker's app, when it is installed (clicking the tile opens it).</summary>
    public VendorApp? VendorApp => _vendorApp ??= Lookup(_s.Model);
    private VendorApp? _vendorApp;
    public bool HasVendorApp => VendorApp is not null;

    private static VendorApp? Lookup(string? model) =>
        PluginAssets.Plugins.VendorAppFor(model) is { } app && app.FindExecutable() is not null ? app : null;

    // ---- LightHub (the owner's own lighting app) owns the lights; the keyboard's drawer opens it.
    //      Owner's decision 2026-10-31: no lights switch in PeripheralHub, just "Open LightHub". ----
    public static readonly string LightHubExe = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LightHub", "LightHub.exe");
    public bool HasLightHub => _s.Id == "aula-f75" && System.IO.File.Exists(LightHubExe);

    public string? Tooltip => Join(_s.Note,
        HasMarker ? "Full DualSense features are on (HapticLink): games see it as a wired controller" : null,
        HasExpandableControls ? (IsExpanded ? "Click to collapse" : "Click to expand") : null) is { Length: > 0 } t ? t : null;

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Virtual controllers can't be kept: they only exist while the app that made them runs (owner, 2026-10-28).</summary>
    public bool CanPin => _s.Connection != ConnectionType.Virtual;

    public string PinGlyph => IsPinned ? "" : "";
    public string PinTooltip => IsPinned
        ? "Kept: stays on the list while disconnected and after a restart. Click to stop keeping it."
        : "Keep on the list while disconnected and after a restart";

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One battery of a multi-battery device: a row "L 80% [battery icon]" on the tile. It exposes the same properties as
/// <see cref="DeviceViewModel"/> for the shared BatteryIcon template (fill, colour band, bolt). Not live = last known
/// level (e.g. the case is closed): grey.
/// </summary>
public sealed record PartViewModel(string Label, int? Percent, bool Charging, bool Live)
{
    /// <summary>One letter on the tile ("Case" → "C"); the tooltip spells it out.</summary>
    public string ShortLabel => Label.Length > 1 ? Label[..1] : Label;
    public string Value => Percent is { } v ? $"{v}%" : "–";
    public string Tooltip => $"{(Label == "L" ? "Left" : Label == "R" ? "Right" : Label)}: {Value}" +
                             (Charging ? ", charging" : "") + (Live ? "" : Percent is null ? " (not read yet)" : " (last known)");

    public double BatteryFillWidth => Percent is { } v && v > 0 ? Math.Max(2, 17 * v / 100.0) : 0;
    public bool ShowBolt => Charging && Live;
    public string BatteryLevel =>
        !Live ? "Away"
        : Charging ? "Charging"
        : Percent switch { >= 50 => "High", > 20 => "Medium", _ => "Low" };
}

/// <summary>
/// Sends a slider's value to a monitor while it's dragged: at most one change every 150 ms (DDC/CI is slow), always
/// ending on the last value. Until the monitor's next reading arrives, the slider keeps showing what the user picked,
/// so it doesn't jump back to the old value for a moment.
/// </summary>
internal sealed class LevelSender(Func<int, Task<string?>> send, Action changed)
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HoldAfterSend = TimeSpan.FromSeconds(1.5);

    private int? _wanted;           // picked but not sent yet, or being sent
    private int? _sent;             // last value sent, shown for a moment after
    private DateTime _sentAt;
    private bool _sending;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public int Shown(int fromDevice) =>
        _wanted ?? (_sent is { } s && DateTime.UtcNow - _sentAt < HoldAfterSend ? s : fromDevice);

    public void Set(int value)
    {
        if (_wanted == value) return;
        _wanted = value;
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled && !_sending) _timer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = Throttle };
        t.Tick += async (_, _) =>
        {
            t.Stop();
            if (_wanted is not { } v) return;
            _sending = true;
            try { await send(v); }
            catch (Exception) { /* logged by the hub; the slider falls back to the monitor's value */ }
            finally { _sending = false; }
            _sent = v;
            _sentAt = DateTime.UtcNow;
            if (_wanted != v) { t.Start(); return; } // dragged further meanwhile
            _wanted = null;
            changed();
            // After the hold the monitor's reading shows again; tiles only refresh on changes, so ask once more.
            await Task.Delay(HoldAfterSend + TimeSpan.FromMilliseconds(50));
            if (_wanted is null && !_sending) changed();
        };
        return t;
    }
}

/// <summary>What the plugins declare: the maker's app per model. <see cref="App"/> sets <see cref="Plugins"/> when the hub starts.</summary>
public static class PluginAssets
{
    public static PeripheralHub.Plugins.PluginSet Plugins { get; set; } = PeripheralHub.Plugins.PluginSet.Empty;
}
