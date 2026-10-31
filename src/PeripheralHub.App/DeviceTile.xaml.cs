using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PeripheralHub.App;

public partial class DeviceTile : UserControl
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(DeviceTile), new PropertyMetadata(false, (d, _) => ((DeviceTile)d).ApplyLayout()));

    /// <summary>Smaller layout without the pin button, used in the widget.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public DeviceTile()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
            SyncDrawer(animate: false);
        };
        Loaded += (_, _) => GrowInIfNew();
    }

    /// <summary>
    /// A device that just joined the list grows in like the drawer opens (owner, 2026-10-28). Only right after it was
    /// added: a hidden panel opened later just shows it.
    /// </summary>
    private void GrowInIfNew()
    {
        if (DataContext is not DeviceViewModel { AddedAt: { } added } || DateTime.UtcNow - added > TimeSpan.FromSeconds(1.5)) return;
        var scale = new ScaleTransform(1, 0);
        LayoutTransform = scale;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        grow.Completed += (_, _) => LayoutTransform = Transform.Identity;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    private void ApplyLayout()
    {
        Drawer.Margin = Compact ? new Thickness(6, 0, 8, 6) : new Thickness(8, 0, 8, 8);
        NoiseRow.LayoutTransform = Compact ? new ScaleTransform(0.85, 0.85) : Transform.Identity;
        FullLayout.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactLayout.Visibility = Compact ? Visibility.Visible : Visibility.Collapsed;
        Card.BorderThickness = new Thickness(Compact ? 0 : 1);
        if (Compact) Card.Background = Brushes.Transparent;
        else Card.SetResourceReference(Border.BackgroundProperty, "CardBg");
    }

    // ---- Drawer (extra controls under the tile): opens with a short slide + fade ----

    private bool _drawerOpen;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every device update raises "" (everything may have changed); only animate when the drawer state really changes.
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DeviceViewModel.ShowDrawer)) SyncDrawer(animate: true);
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DeviceViewModel.IsLeaving)) SyncLeaving();
    }

    private bool _shrinking;

    /// <summary>A device dropping off the list: shrink and fade away (the list removes it right after), or back if it returned.</summary>
    private void SyncLeaving()
    {
        bool leaving = DataContext is DeviceViewModel { IsLeaving: true };
        if (leaving == _shrinking || !IsLoaded) return;
        _shrinking = leaving;
        var scale = LayoutTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        LayoutTransform = scale;
        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = leaving ? EasingMode.EaseIn : EasingMode.EaseOut };
        double from = scale.ScaleY, to = leaving ? 0 : 1;
        var anim = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        if (!leaving) anim.Completed += (_, _) => { if (!_shrinking) LayoutTransform = Transform.Identity; };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, leaving ? 0 : 1, duration) { EasingFunction = ease });
    }

    private void SyncDrawer(bool animate)
    {
        bool open = DataContext is DeviceViewModel { ShowDrawer: true };
        if (open == _drawerOpen) return;
        _drawerOpen = open;

        var scale = new ScaleTransform(1, open ? 0 : 1);
        Drawer.LayoutTransform = scale;
        if (!animate || !IsLoaded)
        {
            Drawer.BeginAnimation(OpacityProperty, null);
            Drawer.Opacity = 1;
            scale.ScaleY = 1;
            Drawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(open ? 200 : 150);
        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        var grow = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, duration) { EasingFunction = ease };
        if (!open) grow.Completed += (_, _) => { if (!_drawerOpen) Drawer.Visibility = Visibility.Collapsed; };
        if (open) Drawer.Visibility = Visibility.Visible;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Drawer.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, duration) { EasingFunction = ease });
    }

    /// <summary>Opens/closes the drawer (tile click, or a widget click that didn't drag).</summary>
    public bool ToggleDrawer()
    {
        if (DataContext is not DeviceViewModel { HasExpandableControls: true } vm) return false;
        ((App)Application.Current).Devices.ToggleExpanded(vm);
        return true;
    }

    // Clicks on the drawer's background don't count as tile clicks (open/close) or widget drags.
    private void OnDrawerMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void OnDrawerMouseUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async void OnNoiseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel vm || sender is not Button { CommandParameter: string p }) return;
        NoiseMode mode; NoiseStrength? strength = null;
        if (Enum.TryParse(p, out NoiseStrength s)) { mode = NoiseMode.Cancellation; strength = s; }
        else if (!Enum.TryParse(p, out mode)) return;
        if (await ((App)Application.Current).SetNoiseControlAsync(vm.Id, mode, strength) is { } error)
            MessageBox.Show(error, "PeripheralHub", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Scrolling over a monitor slider moves it 5 at a time (instead of scrolling the panel).</summary>
    private void OnSliderWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.Value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * slider.SmallChange, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private async void OnSetDefaultSoundClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel vm) return;
        if (await ((App)Application.Current).MakeDefaultSoundDeviceAsync(vm.Id) is { } error)
            MessageBox.Show(error, "PeripheralHub", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel vm) await vm.ToggleMuteAsync();
    }

    private void OnOpenVendorClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel { VendorApp: { } app }) return;
        if (VendorLauncher.Open(app) is { } error)
            MessageBox.Show(error, "PeripheralHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (!Compact)
            Window.GetWindow(this)?.Hide(); // close the panel; the maker's app is in front now
    }

    /// <summary>Starts LightHub with --show: a running LightHub pops up its menu; otherwise LightHub starts and shows it.</summary>
    private void OnOpenLightHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DeviceViewModel.LightHubExe, "--show") { UseShellExecute = true });
            if (!Compact) Window.GetWindow(this)?.Hide(); // close the panel; LightHub's menu is in front now
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't open LightHub: " + ex.Message, "PeripheralHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel vm) ((App)Application.Current).Devices.TogglePin(vm);
    }

    // ---- Click a panel tile to open/close its drawer (in the widget, WidgetWindow tells a click from a drag). ----

    private DeviceViewModel? ClickableDevice => !Compact && DataContext is DeviceViewModel { HasExpandableControls: true } vm ? vm : null;

    private void OnCardEnter(object sender, MouseEventArgs e)
    {
        if (ClickableDevice is null) return;
        Card.SetResourceReference(Border.BackgroundProperty, "CardHoverBg");
        Card.Cursor = Cursors.Hand;
    }

    private void OnCardLeave(object sender, MouseEventArgs e)
    {
        if (Compact) return;
        Card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        Card.Cursor = null;
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (ClickableDevice is not null) ToggleDrawer();
    }
}
