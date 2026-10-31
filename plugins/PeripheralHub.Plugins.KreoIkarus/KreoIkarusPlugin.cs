using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.KreoIkarus;

/// <summary>Kreo Ikarus mouse on its cable, 2.4G receiver and Bluetooth.</summary>
public sealed class KreoIkarusPlugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) =>
        [new KreoIkarusDriver(context.Options, context.VendorAppFor("kreo-ikarus"))];
}
