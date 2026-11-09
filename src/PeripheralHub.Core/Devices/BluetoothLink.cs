using System.Globalization;
using System.Runtime.InteropServices;

namespace PeripheralHub.Devices;

/// <summary>
/// Drops a Bluetooth device's whole link (the ACL connection) with IOCTL_BTH_DISCONNECT_DEVICE on the radio. The
/// pairing stays. Disconnecting only the audio profiles isn't enough: Ear (2) kept the link up, Windows kept showing it
/// connected, and the earbuds brought their audio back over it within a minute (owner, 2026-11-08).
/// </summary>
internal static class BluetoothLink
{
    private const uint IoctlBthDisconnectDevice = 0x0041000C; // CTL_CODE(FILE_DEVICE_BLUETOOTH, 0x03, METHOD_BUFFERED, FILE_ANY_ACCESS)

    /// <summary>Null when a radio accepted it, else Windows' error code as text.</summary>
    public static string? Disconnect(string mac)
    {
        if (!ulong.TryParse(mac, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)) return "bad address";
        int lastError = 0;
        foreach (var radio in BluetoothNames.Radios())
            using (radio)
            {
                if (DeviceIoControl(radio, IoctlBthDisconnectDevice, ref address, sizeof(ulong), IntPtr.Zero, 0, out _, IntPtr.Zero)) return null;
                lastError = Marshal.GetLastWin32Error();
            }
        return lastError == 0 ? "no Bluetooth radio" : $"error {lastError}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(BluetoothNames.SafeRadioHandle device, uint code, ref ulong input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
