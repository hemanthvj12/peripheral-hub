using System.Windows;
using System.Windows.Controls;

namespace PeripheralHub.App;

/// <summary>
/// The menu's contents. It only shows state and reports clicks; <see cref="TrayIcon"/> owns the actions and calls
/// <see cref="Refresh"/>. Settings rows flip their switch and keep the menu open; <see cref="ActionInvoked"/> tells the
/// host to close it after an action.
/// </summary>
public partial class AppMenuView : UserControl
{
    public event Action? WidgetToggled, OnTopToggled, VirtualToggled, HidHideToggled, StartupToggled, UpdateClicked, LogClicked, ExitClicked;
    /// <summary>"Show Sony controller battery over Bluetooth" clicked.</summary>
    public event Action? SonyBatteryToggled;
    /// <summary>"Full DualSense features over Bluetooth" (HapticLink's switch) clicked.</summary>
    public event Action? FullDualSenseToggled;

    /// <summary>An action row was clicked: the host closes the menu (before the action runs).</summary>
    public event Action? ActionInvoked;

    public AppMenuView()
    {
        InitializeComponent();
        VersionText.Text = $"PeripheralHub {AppLog.Version}";
    }

    /// <summary>What the rows show.</summary>
    public sealed record State(bool Widget, bool OnTop, bool Virtual, bool SonyBatteryVisible, bool SonyBattery, bool Startup,
        bool HidHideVisible, bool HidHideOn, bool HidHideEnabled, string? HidHideTooltip,
        bool UpdateVisible, bool UpdateEnabled, string UpdateText, string? UpdateTooltip,
        bool FullDualSenseVisible = false, bool FullDualSense = false, bool FullDualSenseEnabled = true, string? FullDualSenseTooltip = null);

    public void Refresh(State s)
    {
        WidgetRow.Tag = s.Widget;
        OnTopRow.Tag = s.OnTop;
        VirtualRow.Tag = s.Virtual;
        SonyBatteryRow.Visibility = s.SonyBatteryVisible ? Visibility.Visible : Visibility.Collapsed;
        SonyBatteryRow.Tag = s.SonyBattery;
        FullDualSenseRow.Visibility = s.FullDualSenseVisible ? Visibility.Visible : Visibility.Collapsed;
        FullDualSenseRow.Tag = s.FullDualSense;
        FullDualSenseRow.IsEnabled = s.FullDualSenseEnabled;
        FullDualSenseRow.ToolTip = s.FullDualSenseTooltip;
        StartupRow.Tag = s.Startup;
        HidHideRow.Visibility = s.HidHideVisible ? Visibility.Visible : Visibility.Collapsed;
        HidHideRow.Tag = s.HidHideOn;
        HidHideRow.IsEnabled = s.HidHideEnabled;
        HidHideRow.ToolTip = s.HidHideTooltip;
        UpdateRow.Visibility = s.UpdateVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateRow.IsEnabled = s.UpdateEnabled;
        UpdateText.Text = s.UpdateText;
        UpdateRow.ToolTip = s.UpdateTooltip;
    }

    private void OnWidgetClick(object sender, RoutedEventArgs e) => WidgetToggled?.Invoke();
    private void OnOnTopClick(object sender, RoutedEventArgs e) => OnTopToggled?.Invoke();
    private void OnVirtualClick(object sender, RoutedEventArgs e) => VirtualToggled?.Invoke();
    private void OnSonyBatteryClick(object sender, RoutedEventArgs e) => SonyBatteryToggled?.Invoke();
    private void OnFullDualSenseClick(object sender, RoutedEventArgs e) => FullDualSenseToggled?.Invoke();
    private void OnHidHideClick(object sender, RoutedEventArgs e) => HidHideToggled?.Invoke();
    private void OnStartupClick(object sender, RoutedEventArgs e) => StartupToggled?.Invoke();
    private void OnUpdateClick(object sender, RoutedEventArgs e) { ActionInvoked?.Invoke(); UpdateClicked?.Invoke(); }
    private void OnLogClick(object sender, RoutedEventArgs e) { ActionInvoked?.Invoke(); LogClicked?.Invoke(); }
    private void OnExitClick(object sender, RoutedEventArgs e) { ActionInvoked?.Invoke(); ExitClicked?.Invoke(); }
}
