using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.SonyControllers;

/// <summary>DualSense, DualSense Edge and DualShock 4 over USB, Bluetooth or Sony's wireless adapter.</summary>
public sealed class SonyControllersPlugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) => [new SonyControllerDriver(context.Options)];
}
