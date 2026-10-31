using PeripheralHub.Devices;

namespace PeripheralHub.Plugins;

/// <summary>Version of the plugin API this SDK provides. A plugin whose manifest asks for a newer one isn't loaded.</summary>
public static class PluginApi
{
    // 2: monitors (DeviceKind.Monitor, display connection types, DeviceSnapshot.Monitor, IMonitorControlDriver) and
    //    sound devices (DeviceSnapshot.Sound, ISoundDeviceDriver).
    // 3: HidConnection.ReportSpanReceived (input reports without a copy each).
    public const int Version = 3;
}

/// <summary>
/// A plugin's entry point. Each plugin assembly has exactly one public class implementing this, with a public
/// parameterless constructor. The hub creates it once at startup and asks it for its drivers.
/// </summary>
public interface IDevicePlugin
{
    IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context);
}

/// <summary>What the hub hands a plugin when creating its drivers.</summary>
public sealed class PluginContext(PeripheralHubOptions options, PluginManifest manifest, string directory)
{
    public PeripheralHubOptions Options { get; } = options;
    public PluginManifest Manifest { get; } = manifest;
    /// <summary>The plugin's folder (where its manifest, assembly and images are).</summary>
    public string Directory { get; } = directory;

    /// <summary>The maker's app for a model, as declared in the manifest's "vendorApps".</summary>
    public VendorApp? VendorAppFor(string model) => Manifest.VendorAppFor(model);
}
