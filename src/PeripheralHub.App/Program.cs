using Velopack;

namespace PeripheralHub.App;

/// <summary>
/// Entry point. Velopack (the installer/updater) must run first: when Setup or the uninstaller starts the exe with one
/// of its <c>--veloapp-*</c> arguments, it runs the matching hook and exits before any window is created. It also
/// applies a downloaded update on startup.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnFirstRun(_ => Installation.FirstRun())
            .OnBeforeUninstallFastCallback(_ => Installation.BeforeUninstall())
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
