using System.Runtime.InteropServices;

namespace PeripheralHub.Plugins.UsbHeadsets;

/// <summary>One active Windows sound endpoint.</summary>
/// <param name="Id">Windows' endpoint id (what "set as default" takes).</param>
/// <param name="IsOutput">Speakers/headphones (true) or a mic (false).</param>
/// <param name="AdapterName">The device's own name, e.g. "4- CB CryoCore" (Windows numbers repeated installs).</param>
/// <param name="Container">The physical device it belongs to: a headset's speakers and mic share it.</param>
/// <param name="Enumerator">The bus: "USB", "HDAUDIO", "BTHENUM", "ROOT" (software devices)…</param>
internal sealed record AudioEndpoint(string Id, bool IsOutput, string AdapterName, Guid Container, string Enumerator);

/// <summary>Windows' sound devices (Core Audio, MMDevice API) and the default-device switch.</summary>
internal static class CoreAudio
{
    private const int Render = 0, Capture = 1, StateActive = 1;
    private const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;

    private static readonly PropertyKey FriendlyAdapterName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey ContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    public static List<AudioEndpoint> ActiveEndpoints()
    {
        var result = new List<AudioEndpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            foreach (int flow in new[] { Render, Capture })
            {
                if (enumerator.EnumAudioEndpoints(flow, StateActive, out var list) != 0) continue;
                list.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (list.Item(i, out var device) != 0) continue;
                    if (device.GetId(out var id) != 0 || device.OpenPropertyStore(0, out var props) != 0) continue;
                    result.Add(new AudioEndpoint(id, flow == Render, ReadString(props, FriendlyAdapterName) ?? "",
                        ReadGuid(props, ContainerId) ?? Guid.Empty, ReadString(props, EnumeratorName) ?? ""));
                    Marshal.ReleaseComObject(props);
                    Marshal.ReleaseComObject(device);
                }
                Marshal.ReleaseComObject(list);
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result;
    }

    /// <summary>Windows' default output and mic (the "console" role, what apps use), or null when there's none.</summary>
    public static (string? Output, string? Input) Defaults()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try { return (DefaultId(enumerator, Render), DefaultId(enumerator, Capture)); }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, RoleConsole, out var device) != 0) return null;
        try { return device.GetId(out var id) == 0 ? id : null; }
        finally { Marshal.ReleaseComObject(device); }
    }

    /// <summary>Makes an endpoint the default for every use (apps, media, calls), as Windows' Sound settings do.</summary>
    public static bool SetDefault(string endpointId)
    {
        // IPolicyConfig is what Windows' own Sound settings use; it isn't documented, but it has been stable since
        // Windows 7 and audio switchers (SoundSwitch, EarTrumpet) rely on it.
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            return policy.SetDefaultEndpoint(endpointId, RoleConsole) == 0
                 & policy.SetDefaultEndpoint(endpointId, RoleMultimedia) == 0
                 & policy.SetDefaultEndpoint(endpointId, RoleCommunications) == 0;
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    /// <summary>
    /// The container (physical device) a device instance belongs to, e.g. a HID gamepad's: a DualSense's or DualShock
    /// 4's headphone jack is a Windows sound device in the same container.
    /// </summary>
    public static Guid? ContainerOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out int node, instanceId, 0) != 0) return null;
        var key = new DevPropKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
        var buffer = new byte[16];
        int size = buffer.Length;
        return CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) == 0 && size == 16 ? new Guid(buffer) : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DevPropKey { public Guid Format; public int Id; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int node, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(int node, ref DevPropKey key, out int type, byte[] buffer, ref int size, int flags);

    private static string? ReadString(IPropertyStore props, PropertyKey key)
    {
        var k = key;
        if (props.GetValue(ref k, out var v) != 0) return null;
        try { return v.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.Pointer) : null; }
        finally { PropVariantClear(ref v); }
    }

    private static Guid? ReadGuid(IPropertyStore props, PropertyKey key)
    {
        var k = key;
        if (props.GetValue(ref k, out var v) != 0) return null;
        try { return v.Type == 72 /* VT_CLSID */ ? Marshal.PtrToStructure<Guid>(v.Pointer) : null; }
        finally { PropVariantClear(ref v); }
    }

    // ---- COM interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id) { public Guid Format = format; public int Id = id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public short Type; private short _r1, _r2, _r3; public IntPtr Pointer; private IntPtr _p2; }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant v);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }
}
