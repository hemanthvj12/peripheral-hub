using System.Text.Json;

namespace PeripheralHub.Plugins;

/// <summary>
/// Remembers which plugins have been turned on (their device was detected at least once), so they start right away
/// next time, e.g. to show a mouse that is asleep or a Bluetooth device that is off as Disconnected.
/// Stored as a JSON list of plugin ids.
/// </summary>
internal sealed class ActivatedPlugins(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeripheralHub", "activated-plugins.json");

    public string FilePath { get; } = path;

    public HashSet<string> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) is { } ids)
                return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <returns>An error message, or null when saved.</returns>
    public string? Save(IEnumerable<string> ids)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ids.Order(StringComparer.OrdinalIgnoreCase).ToList()));
            File.Move(tmp, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }
}
