using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralHub.Plugins;

/// <summary>
/// A plugin's <c>plugin.json</c>: who it is, which devices it handles, its artwork and the makers' apps.
/// The device list is data the hub can use without running plugin code (e.g. to keep the generic Bluetooth driver
/// away from devices the plugin handles itself).
/// </summary>
public sealed record PluginManifest
{
    public const string FileName = "plugin.json";

    /// <summary>Unique, lower-case, e.g. "kreo-ikarus". When two folders hold the same id, the higher version wins.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    /// <summary>The <see cref="PluginApi.Version"/> the plugin was built for.</summary>
    public int SdkVersion { get; init; } = 1;
    /// <summary>The plugin's assembly file, relative to its folder.</summary>
    public required string Assembly { get; init; }
    public IReadOnlyList<DeviceMatch> Devices { get; init; } = [];
    /// <summary>
    /// Start with the app instead of waiting for one of <see cref="Devices"/> to appear. For plugins whose devices
    /// aren't visible to the hub's scan (e.g. Xbox controllers on a cable or the Xbox Wireless Adapter aren't HID
    /// devices), so they find their own; keep such plugins cheap.
    /// </summary>
    public bool AlwaysOn { get; init; }
    // No "models"/pictures: the app draws every device itself (Art\ in the app, owner 2026-10-29); an old manifest's
    // "models" entry is simply ignored.
    public IReadOnlyList<VendorAppInfo> VendorApps { get; init; } = [];

    /// <summary>Device IDs the plugin handles.</summary>
    /// <param name="Connection">"usb" (cable or receiver) or "bluetooth".</param>
    /// <param name="VendorId">Hex, e.g. "3554".</param>
    /// <param name="ProductId">Hex; omitted means every product from that vendor.</param>
    /// <param name="Manufacturer">Also required to match, for generic chip-maker IDs shared by unrelated products.</param>
    public sealed record DeviceMatch(string Connection, string VendorId, string? ProductId = null, string? Manufacturer = null)
    {
        [JsonIgnore] public ushort Vid => ParseHex(VendorId);
        [JsonIgnore] public ushort? Pid => ProductId is null ? null : ParseHex(ProductId);
        [JsonIgnore] public bool IsBluetooth => string.Equals(Connection, "bluetooth", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsUsb => string.Equals(Connection, "usb", StringComparison.OrdinalIgnoreCase);

        public bool Matches(ushort vid, ushort? pid) => vid == Vid && (Pid is null || Pid == pid);
    }

    /// <summary>See <see cref="VendorApp"/>. Paths may use "/" (easier in JSON than "\\").</summary>
    public sealed record VendorAppInfo(string Model, string DisplayName, string InstalledName, string[] ExePaths,
        string ProcessName, string DefaultInstallFolder)
    {
        public VendorApp ToVendorApp() => new(Model, DisplayName, InstalledName, ExePaths.Select(Native).ToArray(),
            ProcessName, Native(DefaultInstallFolder));

        private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);
    }

    public VendorApp? VendorAppFor(string? model) =>
        VendorApps.FirstOrDefault(a => string.Equals(a.Model, model, StringComparison.OrdinalIgnoreCase))?.ToVendorApp();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <exception cref="InvalidDataException">The manifest is missing fields or malformed.</exception>
    public static PluginManifest Parse(string json)
    {
        PluginManifest? m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid {FileName}: {ex.Message}", ex); }
        if (m is null) throw new InvalidDataException($"Empty {FileName}");
        if (string.IsNullOrWhiteSpace(m.Id)) throw new InvalidDataException($"{FileName} has no id");
        if (!System.Version.TryParse(m.Version, out _)) throw new InvalidDataException($"{m.Id}: version \"{m.Version}\" isn't like 1.2.3");
        foreach (var d in m.Devices)
        {
            if (!d.IsBluetooth && !d.IsUsb) throw new InvalidDataException($"{m.Id}: connection \"{d.Connection}\" must be usb or bluetooth");
            _ = d.Vid; _ = d.Pid; // validates the hex
        }
        return m;
    }

    public static PluginManifest Load(string path) => Parse(File.ReadAllText(path));

    private static ushort ParseHex(string s) =>
        ushort.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidDataException($"\"{s}\" isn't a 4-digit hex ID");
}
