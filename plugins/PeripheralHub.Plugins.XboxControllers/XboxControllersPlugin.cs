using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.XboxControllers;

/// <summary>Xbox controllers (and anything else XInput reports, such as DSX's emulated Xbox 360 controller).</summary>
public sealed class XboxControllersPlugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) => [new XboxControllerDriver(context.Options)];
}
