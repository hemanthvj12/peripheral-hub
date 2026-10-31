using System.IO;
using System.Reflection;
using System.Text;

namespace PeripheralHub.App;

/// <summary>
/// Plain-text log at %APPDATA%\PeripheralHub\logs\peripheralhub.log (outside the install folder, which Setup wipes on
/// reinstall; the tray's "Open log folder" opens it):
/// startup info, plugin activity, update results and crashes. When the file passes 1 MB it is moved to
/// peripheralhub.1.log, so at most about 2 MB is kept. Never throws.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Lock = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeripheralHub", "logs");

    private static string FilePath => Path.Combine(Folder, "peripheralhub.log");

    /// <summary>Version for display, e.g. "1.0.0" (from the build; "dev" builds show the project version).</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0]; // drop the "+commit" suffix the SDK appends

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Folder, "peripheralhub.1.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never break the app */ }
        System.Diagnostics.Debug.WriteLine(message);
    }

    /// <summary>Logs crashes from any thread (the app may still close afterwards).</summary>
    public static void CatchUnhandled(System.Windows.Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"CRASH (unhandled): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Write($"Unobserved task error: {e.Exception}"); e.SetObserved(); };
        app.DispatcherUnhandledException += (_, e) => Write($"CRASH (UI thread): {e.Exception}");
    }
}
