using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.AulaF75;

/// <summary>AULA F75 keyboard on its cable, 2.4G receiver and Bluetooth.</summary>
public sealed class AulaF75Plugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) =>
        [new AulaF75Driver(context.Options, context.VendorAppFor("aula-f75"))];
}
