using System.Windows.Threading;
using PeripheralHub.Integration;

namespace PeripheralHub.App;

/// <summary>
/// Asks HapticLink's service for its status every 3 s (a pipe round trip; nothing when it isn't installed) and hands it
/// to <see cref="DevicesViewModel.ApplyHapticLink"/> for the waves and the menu switch. Says hello whenever HapticLink
/// (re)appears and once a minute, so HapticLink keeps PeripheralHub on HidHide's allowed list while it hides the real
/// controller from games.
/// </summary>
public sealed class HapticLinkWatcher : IDisposable
{
    private readonly DevicesViewModel _devices;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _helloAt = DateTime.MinValue;
    private bool _polling;

    public HapticLinkWatcher(DevicesViewModel devices)
    {
        _devices = devices;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            bool hello = DateTime.UtcNow - _helloAt > TimeSpan.FromMinutes(1);
            var status = await Task.Run(() =>
            {
                if (hello && HapticLink.Hello() is { } version)
                {
                    _helloAt = DateTime.UtcNow;
                    if (_devices.HapticLink is null) AppLog.Write($"HapticLink {version} found");
                }
                return HapticLink.Status();
            });
            if (status is null) _helloAt = DateTime.MinValue; // say hello again as soon as it's back
            if (status?.State != _devices.HapticLink?.State)
                AppLog.Write($"HapticLink: {status?.State ?? "not running"}");
            _devices.ApplyHapticLink(status);
        }
        finally { _polling = false; }
    }

    /// <summary>The menu switch: tells HapticLink, then reads its status back at once.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        bool ok = await Task.Run(() => HapticLink.SetEnabled(enabled));
        AppLog.Write($"HapticLink: switched {(enabled ? "on" : "off")}{(ok ? "" : " (not reachable)")}");
        await PollAsync();
    }

    public void Dispose() => _timer.Stop();
}
