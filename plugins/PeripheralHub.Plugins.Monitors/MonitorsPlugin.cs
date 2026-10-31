using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.Monitors;

/// <summary>Monitors: name, video connection, resolution and refresh rate, and brightness/volume over DDC/CI.</summary>
public sealed class MonitorsPlugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) => [new MonitorDriver(context.Options)];
}
