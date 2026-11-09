using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeripheralHub.Devices;

/// <summary>
/// Names for Bluetooth addresses this PC hasn't paired with (the other devices a controller is paired with), and this
/// PC's own Bluetooth addresses. Asking takes the PC's Bluetooth radio for several seconds, which can make a
/// controller's haptics stutter, so it only runs when the user asks.
/// </summary>
public static class BluetoothNames
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>This PC's Bluetooth adapters' addresses (12 lower-case hex digits).</summary>
    public static IReadOnlySet<string> LocalAddresses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var radio in Radios())
            {
                using (radio)
                {
                    var info = new RadioInfo { dwSize = Marshal.SizeOf<RadioInfo>() };
                    if (BluetoothGetRadioInfo(radio, ref info) == 0 && info.Address != 0) result.Add(Format(info.Address));
                }
            }
        }
        catch (Exception) { /* no Bluetooth stack: nothing is "this PC" */ }
        return result;
    }

    /// <summary>
    /// Asks for the names of <paramref name="macs"/>: first what Windows already knows, then a short search for
    /// discoverable devices, then each remaining address directly (a device that's on and in range answers even when
    /// it isn't discoverable; one that doesn't answer costs about 5 s). Returns the names found, by address.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> LookUpAsync(IEnumerable<string> macs, Action<string>? log = null)
    {
        var wanted = macs.Select(m => m.ToLowerInvariant()).Distinct().ToList();
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LookUp(wanted, log)).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static Dictionary<string, string> LookUp(List<string> wanted, Action<string>? log)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var radios = Radios();
        try
        {
            if (radios.Count == 0) { log?.Invoke("Bluetooth names: no Bluetooth adapter"); return found; }
            LookUp(radios[0], wanted, found, log);
        }
        finally { radios.ForEach(r => r.Dispose()); }
        return found;
    }

    private static void LookUp(SafeRadioHandle radio, List<string> wanted, Dictionary<string, string> found, Action<string>? log)
    {
        var sw = Stopwatch.StartNew();

        void TryCached(string mac)
        {
            if (!found.ContainsKey(mac) && CachedName(radio, mac) is { } name) found[mac] = name;
        }

        wanted.ForEach(TryCached);
        if (found.Count < wanted.Count)
        {
            // Devices that are discoverable right now (Windows remembers their names afterwards).
            foreach (var (mac, name) in Search(5))
                if (wanted.Contains(mac) && !found.ContainsKey(mac)) found[mac] = name;
        }
        foreach (var mac in wanted.Where(m => !found.ContainsKey(m)))
        {
            Page(mac);
            TryCached(mac);
        }
        log?.Invoke($"Bluetooth names: {found.Count} of {wanted.Count} found in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>What Windows has stored for the address (paired, recently found or connected); null if no name.</summary>
    private static string? CachedName(SafeRadioHandle radio, string mac)
    {
        var info = new DeviceInfo { dwSize = Marshal.SizeOf<DeviceInfo>(), Address = ulong.Parse(mac, System.Globalization.NumberStyles.HexNumber) };
        if (BluetoothGetDeviceInfo(radio, ref info) != 0) return null;
        var name = info.szName?.Trim();
        return string.IsNullOrEmpty(name) || name.Replace(":", "").Equals(mac, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>A Bluetooth search: discoverable devices nearby, with their names (about 1.28 s × <paramref name="units"/>).</summary>
    private static List<(string Mac, string Name)> Search(byte units)
    {
        var list = new List<(string, string)>();
        var search = new SearchParams
        {
            dwSize = Marshal.SizeOf<SearchParams>(),
            fReturnAuthenticated = 1, fReturnRemembered = 0, fReturnUnknown = 1, fReturnConnected = 1,
            fIssueInquiry = 1, cTimeoutMultiplier = units,
        };
        var info = new DeviceInfo { dwSize = Marshal.SizeOf<DeviceInfo>() };
        var find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero) return list;
        try
        {
            do
            {
                if (info.szName is { Length: > 0 } n) list.Add((Format(info.Address), n.Trim()));
                info = new DeviceInfo { dwSize = Marshal.SizeOf<DeviceInfo>() };
            } while (BluetoothFindNextDevice(find, ref info));
        }
        finally { BluetoothFindDeviceClose(find); }
        return list;
    }

    /// <summary>
    /// Connects to the address briefly (a service lookup), which works for a device that is on and in range even when
    /// it isn't discoverable; Windows asks for its name on the way. Gives up after about 5 s when nothing answers.
    /// </summary>
    private static void Page(string mac)
    {
        if (WSAStartup(0x0202, new byte[512]) != 0) return;
        var l2cap = new Guid("00000100-0000-1000-8000-00805F9B34FB").ToByteArray();
        var classId = Marshal.AllocHGlobal(16);
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            Marshal.Copy(l2cap, 0, classId, 16);
            var query = new QuerySet
            {
                dwSize = Marshal.SizeOf<QuerySet>(),
                dwNameSpace = 16, // NS_BTH
                lpServiceClassId = classId,
                lpszContext = "(" + string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))) + ")",
            };
            const int returnAll = 0x0FF0, flushCache = 0x1000;
            if (WSALookupServiceBeginW(ref query, returnAll | flushCache, out var lookup) != 0) return;
            try
            {
                int length = 8192;
                while (WSALookupServiceNextW(lookup, returnAll, ref length, buffer) == 0) length = 8192;
            }
            finally { WSALookupServiceEnd(lookup); }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(classId);
            WSACleanup();
        }
    }

    private static string Format(ulong address) => address.ToString("x12");

    internal static List<SafeRadioHandle> Radios()
    {
        var list = new List<SafeRadioHandle>();
        var p = new FindRadioParams { dwSize = Marshal.SizeOf<FindRadioParams>() };
        var find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find == IntPtr.Zero) return list;
        try
        {
            do { list.Add(radio); } while (BluetoothFindNextRadio(find, out radio));
        }
        finally { BluetoothFindRadioClose(find); }
        return list;
    }

    // ---- Win32 ----

    internal sealed class SafeRadioHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)] private struct FindRadioParams { public int dwSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RadioInfo
    {
        public int dwSize;
        public ulong Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        public uint ulClassofDevice;
        public ushort lmpSubversion;
        public ushort manufacturer;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected, fRemembered, fAuthenticated;
        public SystemTime stLastSeen, stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParams
    {
        public int dwSize;
        public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QuerySet
    {
        public int dwSize;
        public IntPtr lpszServiceInstanceName, lpServiceClassId, lpVersion, lpszComment;
        public int dwNameSpace;
        public IntPtr lpNSProviderId;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszContext;
        public int dwNumberOfProtocols;
        public IntPtr lpafpProtocols, lpszQueryString;
        public int dwNumberOfCsAddrs;
        public IntPtr lpcsaBuffer;
        public int dwOutputFlags;
        public IntPtr lpBlob;
    }

    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out SafeRadioHandle radio);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindNextRadio(IntPtr find, out SafeRadioHandle radio);
    [DllImport("bthprops.cpl")] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("bthprops.cpl")] private static extern int BluetoothGetRadioInfo(SafeRadioHandle radio, ref RadioInfo info);
    [DllImport("bthprops.cpl")] private static extern int BluetoothGetDeviceInfo(SafeRadioHandle radio, ref DeviceInfo info);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref SearchParams p, ref DeviceInfo info);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);
    [DllImport("bthprops.cpl")] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, byte[] data);
    [DllImport("ws2_32.dll")] private static extern int WSACleanup();
    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode)] private static extern int WSALookupServiceBeginW(ref QuerySet q, int flags, out IntPtr lookup);
    [DllImport("ws2_32.dll")] private static extern int WSALookupServiceNextW(IntPtr lookup, int flags, ref int length, IntPtr buffer);
    [DllImport("ws2_32.dll")] private static extern int WSALookupServiceEnd(IntPtr lookup);
}
