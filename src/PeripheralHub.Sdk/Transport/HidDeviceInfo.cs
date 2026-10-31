using System.Runtime.InteropServices;
using System.Text;
using PeripheralHub.Native;

namespace PeripheralHub.Transport;

/// <summary>One HID top-level collection (Windows exposes each collection as its own device path).</summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    string? SerialNumber,
    string? Manufacturer = null,
    VirtualOrigin? Virtual = null)
{
    /// <summary>Created by software (game streaming, remapping), not real hardware; see <see cref="DeviceTree.FindVirtualOrigin"/>.</summary>
    public bool IsVirtual => Virtual is not null;

    /// <summary>Classic Bluetooth HID (HID profile UUID 0x1124 in the path).</summary>
    public bool IsBluetoothClassic => Path.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bluetooth LE HID-over-GATT (service UUID 0x1812 in the path).</summary>
    public bool IsBluetoothLe => Path.Contains("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase);

    public bool IsBluetooth => IsBluetoothClassic || IsBluetoothLe;

    public bool Matches(ushort vid, ushort pid) => VendorId == vid && ProductId == pid;
}

/// <summary>Enumerates HID collections. Attributes are cached per path, so repeated scans only touch new devices.</summary>
public sealed class HidEnumerator
{
    private readonly Dictionary<string, HidDeviceInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        var paths = EnumeratePaths();
        var result = new List<HidDeviceInfo>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            seen.Add(path);
            if (!_cache.TryGetValue(path, out var info))
            {
                info = Describe(path);
                // Don't cache failures: a device may not be ready on its first appearance.
                if (info is not null) _cache[path] = info;
            }
            if (info is not null) result.Add(info);
        }
        foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(gone);
        return result;
    }

    internal static List<string> EnumeratePaths()
    {
        var paths = new List<string>();
        Win32.HidD_GetHidGuid(out var guid);
        var set = Win32.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Win32.DigcfPresent | Win32.DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new Win32.SpDeviceInterfaceData { Size = Marshal.SizeOf<Win32.SpDeviceInterfaceData>() };
                if (!Win32.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                Win32.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Win32.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Win32.SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    /// <summary>Reads attributes and capabilities with a zero-access handle (works even for keyboards/mice Windows holds exclusively).</summary>
    internal static HidDeviceInfo? Describe(string path)
    {
        using var h = Win32.CreateFileW(path, 0, Win32.FileShareReadWrite, IntPtr.Zero, Win32.OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var attrs = new Win32.HiddAttributes { Size = Marshal.SizeOf<Win32.HiddAttributes>() };
        if (!Win32.HidD_GetAttributes(h, ref attrs)) return null;
        if (!Win32.HidD_GetPreparsedData(h, out var pre)) return null;
        var caps = new byte[64];
        try { Win32.HidP_GetCaps(pre, caps); }
        finally { Win32.HidD_FreePreparsedData(pre); }

        return new HidDeviceInfo(path, attrs.VendorId, attrs.ProductId,
            UsagePage: BitConverter.ToUInt16(caps, 2), Usage: BitConverter.ToUInt16(caps, 0),
            InputReportLength: BitConverter.ToUInt16(caps, 4), OutputReportLength: BitConverter.ToUInt16(caps, 6),
            FeatureReportLength: BitConverter.ToUInt16(caps, 8),
            SerialNumber: ReadString(h, Win32.HidD_GetSerialNumberString),
            Manufacturer: ReadString(h, Win32.HidD_GetManufacturerString),
            Virtual: DeviceTree.InstanceIdFromPath(path) is { } id ? DeviceTree.FindVirtualOrigin(id) : null);
    }

    private static string? ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle h, Func<Microsoft.Win32.SafeHandles.SafeFileHandle, byte[], int, bool> get)
    {
        var buf = new byte[256];
        return get(h, buf, buf.Length) && Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim() is { Length: > 0 } s ? s : null;
    }
}
