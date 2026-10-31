using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.NothingEar;

/// <summary>Nothing earbuds (e.g. Ear (2)): left, right and case battery over their own Bluetooth serial channel.</summary>
public sealed class NothingEarPlugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) =>
        [new NothingEarDriver(context.Options, context.VendorAppFor(NothingEarDriver.Model))];
}
