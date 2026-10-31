using System.Text;
using System.Text.RegularExpressions;
using PeripheralHub.Native;

namespace PeripheralHub.Transport;

/// <summary>A Bluetooth LE device Windows knows about (paired), with the battery level Windows keeps for it.</summary>
/// <param name="BatteryPercent">Windows' cached Battery Service value. Stale while <see cref="Connected"/> is false.</param>
/// <param name="Category">Windows' device categories, e.g. "Input.Mouse", "Communication.Headset.Bluetooth".</param>
/// <param name="Container">
/// Windows' container id of the device: its other parts (e.g. a headset's sound endpoints) share it.
/// </param>
public sealed record BleDeviceInfo(string Mac, ushort VendorId, ushort ProductId, string? Name, bool Connected,
    int? BatteryPercent, DateTimeOffset? BatteryUpdatedAt, IReadOnlyList<string>? Category = null, Guid? Container = null);

/// <summary>
/// A classic Bluetooth device Windows knows about (paired): earphones, headsets, controllers…
/// Classic devices report battery through the hands-free profile; Windows stores it on that profile's device node.
/// </summary>
/// <param name="VendorId">From the device's ID record, when it has one (e.g. 0x054C for Sony controllers).</param>
/// <param name="BatteryPercent">Stale while <see cref="Connected"/> is false.</param>
/// <param name="Services">
/// Service class IDs the device offers (one Windows node each), e.g. hands-free 0000111E-… or a maker's own serial
/// service such as Nothing's AEAC4A03-… ("NT LINK"). Lets plugins recognise devices they can talk to.
/// </param>
/// <param name="Container">
/// Windows' container id of the device: its other parts (e.g. a headset's sound endpoints) share it.
/// </param>
public sealed record ClassicBtDeviceInfo(string Mac, ushort? VendorId, ushort? ProductId, string? Name, bool Connected,
    int? BatteryPercent, DateTimeOffset? BatteryUpdatedAt, IReadOnlyList<string>? Category, IReadOnlyList<Guid>? Services = null,
    Guid? Container = null)
{
    public bool HasService(Guid service) => Services?.Contains(service) == true;
}

/// <summary>Bluetooth state from Windows' device properties (no radio traffic of our own).</summary>
public static partial class BluetoothDevices
{
    // DEVPKEY_Device_IsConnected-like flag Windows sets on Bluetooth device nodes.
    private static readonly Win32.DevPropKey Connected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    // Bluetooth battery level (Battery Service 0x180F for LE; hands-free battery for classic), and when Windows last refreshed it.
    private static readonly Win32.DevPropKey Battery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    private static readonly Win32.DevPropKey BatteryTime = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 7);
    internal static readonly Win32.DevPropKey FriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    internal static readonly Win32.DevPropKey DeviceDesc = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
    // DEVPKEY_DeviceContainer_Category: Windows' own device categories (string list).
    private static readonly Win32.DevPropKey ContainerCategory = new("78C34FC8-104A-4ACA-9EA4-524D52996E57", 90);
    // DEVPKEY_Device_ContainerId.
    private static readonly Win32.DevPropKey ContainerId = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);

    // e.g. BTHLEDEVICE\{00001800-...}_DEV_VID&023554_PID&FA07_REV&6701_E3669A310B47\...
    [GeneratedRegex(@"_DEV_VID&02([0-9A-F]{4})_PID&([0-9A-F]{4})_REV&[0-9A-F]+_([0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex LeChildPattern();

    // e.g. BTHENUM\{0000111E-...}_VID&000102B0_PID&0000\A&1D0D9AA7&3&A1B2C3D4E5F6_C00000000
    // VID&ssssvvvv: ssss = ID source (0001 Bluetooth SIG, 0002 USB-IF), vvvv = vendor.
    [GeneratedRegex(@"^BTHENUM\\\{(?<svc>[0-9A-F-]+)\}_VID&[0-9A-F]{4}([0-9A-F]{4})_PID&([0-9A-F]{4})\\.*&([0-9A-F]{12})_C", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicChildPattern();

    public static IReadOnlyList<BleDeviceInfo> EnumerateLe()
    {
        // VID/PID live on the GATT service children; battery/connected live on the BTHLE\DEV_<mac> parent.
        var ids = new Dictionary<string, (ushort Vid, ushort Pid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in DeviceIds("BTHLEDEVICE"))
        {
            var m = LeChildPattern().Match(child);
            if (m.Success)
                ids[m.Groups[3].Value] = (Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16));
        }

        var result = new List<BleDeviceInfo>();
        foreach (var node in DeviceIds("BTHLE"))
        {
            if (!node.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)) continue;
            string mac = node.Substring(10, 12);
            if (!ids.TryGetValue(mac, out var vp)) continue;
            if (Win32.CM_Locate_DevNodeW(out int inst, node, Win32.CmLocateDevNodePhantom) != Win32.CrSuccess) continue;
            result.Add(new BleDeviceInfo(mac.ToLowerInvariant(), vp.Vid, vp.Pid,
                ReadString(inst, FriendlyName), ReadBool(inst, Connected) ?? false,
                ReadByte(inst, Battery), ReadFileTime(inst, BatteryTime), ReadStringList(inst, ContainerCategory), ReadGuid(inst, ContainerId)));
        }
        return result;
    }

    /// <summary>Paired classic Bluetooth devices: connection, category and (from the hands-free profile node) battery.</summary>
    public static IReadOnlyList<ClassicBtDeviceInfo> EnumerateClassic()
    {
        var ids = DeviceIds("BTHENUM").ToList();

        // Profile nodes carry the vendor ID and, for headsets, the battery.
        var vendor = new Dictionary<string, (ushort Vid, ushort Pid)>(StringComparer.OrdinalIgnoreCase);
        var battery = new Dictionary<string, (int Percent, DateTimeOffset? At)>(StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in ids)
        {
            if (ParseClassicChildId(child) is not { } c) continue;
            if (c.VendorId != 0) vendor.TryAdd(c.Mac, (c.VendorId, c.ProductId));
            if (c.Service is { } svc)
            {
                if (!services.TryGetValue(c.Mac, out var list)) services[c.Mac] = list = [];
                if (!list.Contains(svc)) list.Add(svc);
            }
            if (Win32.CM_Locate_DevNodeW(out int inst, child, Win32.CmLocateDevNodePhantom) == Win32.CrSuccess &&
                ReadByte(inst, Battery) is { } level)
                battery[c.Mac] = (level, ReadFileTime(inst, BatteryTime));
        }

        var result = new List<ClassicBtDeviceInfo>();
        foreach (var node in ids)
        {
            if (!node.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Win32.CM_Locate_DevNodeW(out int inst, node, Win32.CmLocateDevNodePhantom) != Win32.CrSuccess) continue;
            string mac = node.Substring(12, 12).ToLowerInvariant();
            vendor.TryGetValue(mac, out var vp);
            battery.TryGetValue(mac, out var b);
            result.Add(new ClassicBtDeviceInfo(mac,
                vendor.ContainsKey(mac) ? vp.Vid : null, vendor.ContainsKey(mac) ? vp.Pid : null,
                ReadString(inst, FriendlyName), ReadBool(inst, Connected) ?? false,
                battery.ContainsKey(mac) ? b.Percent : null, battery.ContainsKey(mac) ? b.At : null,
                ReadStringList(inst, ContainerCategory), services.GetValueOrDefault(mac), ReadGuid(inst, ContainerId)));
        }
        return result;
    }

    /// <summary>Parses a classic profile node ID into (lower-case MAC, vendor ID, product ID, service class ID).</summary>
    internal static (string Mac, ushort VendorId, ushort ProductId, Guid? Service)? ParseClassicChildId(string id)
    {
        var m = ClassicChildPattern().Match(id);
        return m.Success
            ? (m.Groups[3].Value.ToLowerInvariant(), Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16),
               Guid.TryParse(m.Groups["svc"].Value, out var svc) ? svc : null)
            : null;
    }

    private static IEnumerable<string> DeviceIds(string enumerator)
    {
        // CM_GETIDLIST_FILTER_PRESENT returns an empty list for these enumerators on Windows 11, so list everything;
        // paired-but-off devices are told apart by their Connected property instead.
        int flags = Win32.CmGetIdListFilterEnumerator;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.CM_Get_Device_ID_List_SizeW(out int len, enumerator, flags) != Win32.CrSuccess || len <= 1) return [];
            var buf = new char[len];
            int cr = Win32.CM_Get_Device_ID_ListW(enumerator, buf, len, flags);
            if (cr == Win32.CrBufferSmall) continue; // list grew between calls
            if (cr != Win32.CrSuccess) return [];
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    private static byte[]? Read(int inst, Win32.DevPropKey key, int expectedType)
    {
        int size = 0;
        Win32.CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (size <= 0) return null;
        var buf = new byte[size];
        if (Win32.CM_Get_DevNode_PropertyW(inst, ref key, out int type, buf, ref size, 0) != Win32.CrSuccess) return null;
        return type == expectedType ? buf : null;
    }

    private static int? ReadByte(int inst, Win32.DevPropKey key) => Read(inst, key, Win32.DevPropTypeByte) is { } b ? b[0] : null;
    private static Guid? ReadGuid(int inst, Win32.DevPropKey key) => Read(inst, key, Win32.DevPropTypeGuid) is { Length: 16 } b ? new Guid(b) : null;
    private static bool? ReadBool(int inst, Win32.DevPropKey key) => Read(inst, key, Win32.DevPropTypeBoolean) is { } b ? b[0] != 0 : null;
    internal static string? ReadString(int inst, Win32.DevPropKey key) =>
        Read(inst, key, Win32.DevPropTypeString) is { } b ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;
    private static IReadOnlyList<string>? ReadStringList(int inst, Win32.DevPropKey key) =>
        Read(inst, key, Win32.DevPropTypeStringList) is { } b
            ? Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : null;
    private static DateTimeOffset? ReadFileTime(int inst, Win32.DevPropKey key) =>
        Read(inst, key, Win32.DevPropTypeFileTime) is { Length: >= 8 } b ? DateTimeOffset.FromFileTime(BitConverter.ToInt64(b)) : null;
}
