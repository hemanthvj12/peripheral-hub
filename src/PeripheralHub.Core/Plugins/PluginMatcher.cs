using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins;

/// <summary>
/// Decides from a plugin's manifest alone (no plugin code runs) whether one of its devices is here right now:
/// a "usb" entry needs a present USB HID device (cable or receiver), a "bluetooth" entry a connected Bluetooth device.
/// Paired-but-off Bluetooth devices don't count.
/// </summary>
public static class PluginMatcher
{
    public static bool IsDetected(PluginManifest manifest, ScanContext context) => Detected(manifest, context) is not null;

    /// <summary>A short description of the first matching device, or null if none is here.</summary>
    public static string? Detected(PluginManifest manifest, ScanContext context)
    {
        foreach (var d in manifest.Devices)
        {
            if (d.IsUsb)
            {
                // Bluetooth HID collections are judged by the Bluetooth entries below (they may linger while disconnected).
                var hid = context.Hid.FirstOrDefault(h => !h.IsBluetooth && d.Matches(h.VendorId, h.ProductId) &&
                    (d.Manufacturer is null || string.Equals(d.Manufacturer, h.Manufacturer?.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (hid is not null) return $"USB {hid.VendorId:X4}:{hid.ProductId:X4}";
            }
            else if (d.IsBluetooth)
            {
                var le = context.BluetoothLe.FirstOrDefault(b => b.Connected && d.Matches(b.VendorId, b.ProductId));
                if (le is not null) return $"Bluetooth {le.Name ?? le.Mac}";
                var classic = context.BluetoothClassic.FirstOrDefault(b =>
                    b.Connected && b.VendorId is { } vid && d.Matches(vid, b.ProductId));
                if (classic is not null) return $"Bluetooth {classic.Name ?? classic.Mac}";
            }
        }
        return null;
    }
}
