using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace PeripheralHub.App;

/// <summary>
/// Updates from GitHub Releases (published by build-installer.ps1). Only active in a copy installed with Setup;
/// a development copy (install.ps1 or dotnet run) has nothing to update.
/// Checks at startup and once a day; a new version is downloaded in the background and installed when the app next
/// starts, or straight away with "Restart to update". Properties change on the UI thread.
/// </summary>
public sealed class AppUpdater : INotifyPropertyChanged
{
    public const string ReleasesRepo = "https://github.com/hemanthvj12/peripheral-hub";
    public const string ReleasesPage = ReleasesRepo + "/releases";

    /// <summary>
    /// Checks GitHub Releases at startup and daily. Set to false to have the tray open the releases page instead
    /// (running a newer Setup upgrades the installed copy in place).
    /// </summary>
    public static bool AutomaticUpdates { get; } = true;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly Dispatcher _ui;
    private readonly UpdateManager? _manager;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _ready;
    private bool _checking;
    private string? _lastResult;

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised on the UI thread when a downloaded update is ready (for a tray notification).</summary>
    public event Action<string>? UpdateReady;

    public AppUpdater(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleasesRepo, accessToken: null, prerelease: false));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception) { /* not an installed copy */ }
        _timer = new DispatcherTimer(CheckInterval, DispatcherPriority.Background, async (_, _) => await CheckAsync(manual: false), ui);
    }

    /// <summary>True for a copy installed with Setup (only then are updates possible).</summary>
    public bool IsInstalled => _manager is not null;
    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? "dev";
    public bool Checking { get => _checking; private set { _checking = value; Raise(); } }
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();
    /// <summary>Result of the last check the user asked for, e.g. "Up to date".</summary>
    public string? LastResult { get => _lastResult; private set { _lastResult = value; Raise(); } }

    public void Start()
    {
        if (!IsInstalled || !AutomaticUpdates) return;
        _timer.Start();
        _ = CheckAsync(manual: false);
    }

    /// <summary>Checks for a newer release and downloads it. Silent unless <paramref name="manual"/>.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_manager is null || !AutomaticUpdates || Checking) return;
        if (_ready is not null) { if (manual) LastResult = $"Version {ReadyVersion} is ready"; return; }
        Checking = true;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null) { if (manual) LastResult = "Up to date"; return; }
            AppLog.Write($"Update {update.TargetFullRelease.Version} found, downloading");
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            LastResult = null;
            Raise(nameof(ReadyVersion));
            UpdateReady?.Invoke(ReadyVersion!);
        }
        catch (Exception ex)
        {
            // Offline, GitHub unreachable, or the releases repository isn't public.
            if (manual) LastResult = "Couldn't check for updates";
            AppLog.Write($"Update check failed: {ex.Message}");
        }
        finally { Checking = false; }
    }

    /// <summary>Installs the downloaded update and restarts the app.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is not null && _ready is not null) _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
