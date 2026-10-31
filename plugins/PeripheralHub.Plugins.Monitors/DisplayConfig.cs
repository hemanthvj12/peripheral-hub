using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PeripheralHub.Plugins.Monitors;

/// <summary>One monitor Windows is showing the desktop on right now.</summary>
/// <param name="SourceName">The GDI name of the desktop it shows, e.g. \\.\DISPLAY1 (to find its DDC/CI handle).</param>
/// <param name="InstanceId">The monitor's device instance id, e.g. DISPLAY\ACR0750\7&amp;315967f&amp;1&amp;UID512.</param>
/// <param name="FriendlyName">The name from the monitor's EDID, e.g. "VG240Y S"; empty for most built-in screens.</param>
/// <param name="OutputTechnology">DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY: how it's connected.</param>
internal sealed record ActiveDisplay(string SourceName, string InstanceId, string FriendlyName, uint OutputTechnology,
    int Width, int Height, double RefreshHz);

/// <summary>Active monitors from Windows' display configuration (QueryDisplayConfig): cheap, no talking to the monitors.</summary>
internal static class DisplayConfig
{
    public static List<ActiveDisplay> Active()
    {
        var result = new List<ActiveDisplay>();
        PathInfo[] paths;
        ModeInfo[] modes;
        while (true)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return result;
            paths = new PathInfo[pathCount];
            modes = new ModeInfo[modeCount];
            int rc = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (rc == ErrorInsufficientBuffer) continue; // displays changed between the two calls
            if (rc != 0) return result;
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            break;
        }

        foreach (var p in paths)
        {
            var target = new TargetDeviceName { Header = Header(GetTargetName, p.Target.AdapterId, p.Target.Id, Marshal.SizeOf<TargetDeviceName>()) };
            if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;
            var source = new SourceDeviceName { Header = Header(GetSourceName, p.Source.AdapterId, p.Source.Id, Marshal.SizeOf<SourceDeviceName>()) };
            if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

            int width = 0, height = 0;
            if (p.Source.ModeInfoIdx < modes.Length && modes[p.Source.ModeInfoIdx].InfoType == ModeInfoTypeSource)
            {
                width = (int)modes[p.Source.ModeInfoIdx].SourceWidth;
                height = (int)modes[p.Source.ModeInfoIdx].SourceHeight;
            }
            double hz = p.Target.RefreshRate.Denominator == 0 ? 0 : (double)p.Target.RefreshRate.Numerator / p.Target.RefreshRate.Denominator;

            var instanceId = Transport.DeviceTree.InstanceIdFromPath(target.MonitorDevicePath ?? "") ?? "";
            result.Add(new ActiveDisplay(source.ViewGdiDeviceName ?? "", instanceId, target.MonitorFriendlyDeviceName ?? "",
                target.OutputTechnology, width, height, hz));
        }
        return result;
    }

    /// <summary>The monitor's EDID as Windows stored it, when readable.</summary>
    public static byte[]? ReadEdid(string instanceId)
    {
        if (instanceId.Length == 0) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch (Exception) { return null; }
    }

    private static DeviceInfoHeader Header(uint type, Luid adapter, uint id, int size) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

    // ---- user32 display configuration ----
    private const uint QdcOnlyActivePaths = 0x2;
    private const int ErrorInsufficientBuffer = 122;
    private const uint ModeInfoTypeSource = 1;
    private const uint GetSourceName = 1, GetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology; public uint Rotation;
        public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo Source; public PathTargetInfo Target; public uint Flags; }

    /// <summary>DISPLAYCONFIG_MODE_INFO (64 bytes); only the source-mode part of the union is read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public uint SourceWidth;
        [FieldOffset(20)] public uint SourceHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader { public uint Type; public uint Size; public Luid AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount,
        [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);
}

/// <summary>
/// DDC/CI: the standard way to read and set a monitor's own settings (the ones in its on-screen menu) over the video
/// cable. Codes are MCCS VCP codes: 0x10 brightness, 0x62 speaker volume, 0x8D speaker mute. Only those three are
/// ever read or written.
/// Each call takes tens of milliseconds, so callers keep it off the UI and scan threads.
/// </summary>
internal static class Ddc
{
    public const byte Brightness = 0x10, Volume = 0x62, Mute = 0x8D; // mute: 1 = muted, 2 = not muted (MCCS)

    /// <summary>Runs <paramref name="action"/> with the DDC/CI handles of the monitors showing GDI source <paramref name="sourceName"/>.</summary>
    public static T? WithMonitors<T>(string sourceName, Func<IntPtr[], T> action)
    {
        var hmonitor = MonitorFor(sourceName);
        if (hmonitor == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, out uint n) || n == 0) return default;
        var arr = new PhysicalMonitor[n];
        if (!GetPhysicalMonitorsFromHMONITOR(hmonitor, n, arr)) return default;
        try { return action(arr.Select(a => a.Handle).ToArray()); }
        finally { DestroyPhysicalMonitors(n, arr); }
    }

    public static MonitorLevel? Read(IntPtr handle, byte code) =>
        Locked(() => GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out uint current, out uint max) && max > 0
            ? new MonitorLevel((int)current, (int)max)
            : null);

    /// <summary>Speaker mute; null when the monitor doesn't answer with one of the two defined values.</summary>
    public static bool? ReadMute(IntPtr handle) =>
        Locked(() => GetVCPFeatureAndVCPFeatureReply(handle, Mute, IntPtr.Zero, out uint current, out _)
            ? current switch { 1 => true, 2 => false, _ => (bool?)null }
            : null);

    public static bool Write(IntPtr handle, byte code, int value) => Locked(() => SetVCPFeature(handle, code, (uint)value));

    /// <summary>
    /// DDC/CI goes through the graphics driver's I2C, which LightHub also uses for the GPU's lighting chip (that chip
    /// locked up on 2026-11-01). Both apps take this lock around each transfer so they never use it at the same moment.
    /// If it stays busy for a second, the call is skipped like a monitor that didn't answer.
    /// </summary>
    private static readonly Mutex I2CLock = new(false, @"Local\AmdDisplayI2C");

    private static T? Locked<T>(Func<T> transfer)
    {
        bool owned;
        try { owned = I2CLock.WaitOne(TimeSpan.FromSeconds(1)); }
        catch (AbandonedMutexException) { owned = true; } // the other app ended while holding it: ours now
        if (!owned) return default;
        try { return transfer(); }
        finally { I2CLock.ReleaseMutex(); }
    }

    private static IntPtr MonitorFor(string sourceName)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hm, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(hm, ref info) && string.Equals(info.Device, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hm;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size; public Rect Monitor; public Rect Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr hmonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hmonitor, ref MonitorInfoEx info);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hmonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hmonitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
