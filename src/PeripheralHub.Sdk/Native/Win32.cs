using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralHub.Native;

internal static class Win32
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    public const uint FileShareReadWrite = 0x3, OpenExisting = 3, FileFlagOverlapped = 0x40000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    // ---- hid.dll ----
    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes { public int Size; public ushort VendorId, ProductId, VersionNumber; }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HiddAttributes attrs);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, byte[] caps);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buffer, int length);

    // ---- setupapi.dll ----
    public const int DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail,
        int detailSize, out int requiredSize, IntPtr devInfo);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    // ---- cfgmgr32.dll ----
    public const int CrSuccess = 0, CrBufferSmall = 0x1A;
    // Bluetooth device nodes report as "not present" to cfgmgr32, so they must be located as phantoms.
    public const int CmLocateDevNodePhantom = 0x1;
    public const int CmGetIdListFilterEnumerator = 0x1, CmGetIdListFilterPresent = 0x100;
    public const int DevPropTypeByte = 0x3, DevPropTypeGuid = 0xD, DevPropTypeFileTime = 0x10, DevPropTypeBoolean = 0x11, DevPropTypeString = 0x12,
        DevPropTypeStringList = 0x2012;

    [StructLayout(LayoutKind.Sequential)]
    public struct DevPropKey { public Guid FmtId; public int Pid; public DevPropKey(string g, int pid) { FmtId = new Guid(g); Pid = pid; } }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_List_SizeW(out int length, string filter, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, int length, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out int parent, int devInst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(int devInst, char[] buffer, int length, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_PropertyW(int devInst, ref DevPropKey key, out int type, byte[]? buffer, ref int size, int flags);
}
