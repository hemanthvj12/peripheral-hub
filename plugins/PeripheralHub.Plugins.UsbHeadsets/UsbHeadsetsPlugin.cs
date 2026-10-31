using PeripheralHub.Devices;

namespace PeripheralHub.Plugins.UsbHeadsets;

/// <summary>USB headsets (any maker): shown while plugged in, with a button to make them Windows' default sound device.</summary>
public sealed class UsbHeadsetsPlugin : IDevicePlugin
{
    public IEnumerable<IDeviceDriver> CreateDrivers(PluginContext context) => [new UsbHeadsetDriver(context.Options)];
}
