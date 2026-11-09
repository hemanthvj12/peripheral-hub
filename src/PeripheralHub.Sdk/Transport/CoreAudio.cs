using System.Runtime.InteropServices;

namespace PeripheralHub.Transport;

/// <summary>One active Windows sound endpoint.</summary>
/// <param name="Id">Windows' endpoint id (what "set as default" takes).</param>
/// <param name="IsOutput">Speakers/headphones (true) or a mic (false).</param>
/// <param name="AdapterName">The device's own name, e.g. "4- CB CryoCore" (Windows numbers repeated installs).</param>
/// <param name="Container">The physical device it belongs to: a headset's speakers and mic share it.</param>
/// <param name="Enumerator">The bus: "USB", "HDAUDIO", "BTHENUM", "ROOT" (software devices)…</param>
/// <param name="FormFactor">
/// Windows' endpoint form factor when known: 1 speakers, 3 headphones, 4 microphone, 5 headset, 6 handset… A classic
/// Bluetooth headset can list two outputs: its stereo one (headphones/speakers) and a hands-free one (headset: call
/// quality, paired with its mic).
/// </param>
public sealed record AudioEndpoint(string Id, bool IsOutput, string AdapterName, Guid Container, string Enumerator, int? FormFactor = null)
{
    public const int FormHeadset = 5, FormHandset = 6;

    /// <summary>A call-quality output (a Bluetooth headset's hands-free profile), not a stereo one.</summary>
    public bool IsHandsFree => IsOutput && FormFactor is FormHeadset or FormHandset;
}

/// <summary>
/// Windows' sound devices (Core Audio, MMDevice API) and the default-device switch. Cheap (no device traffic); works
/// from any thread.
/// </summary>
public static class CoreAudio
{
    private const int Render = 0, Capture = 1, StateActive = 1;
    private const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;

    private static readonly PropertyKey FriendlyAdapterName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey ContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    public static List<AudioEndpoint> ActiveEndpoints() => Endpoints(StateActive);

    /// <summary>
    /// Endpoints in the given DEVICE_STATE_* mask. A paired Bluetooth audio device that isn't connected keeps its
    /// endpoints as "unplugged" (8).
    /// </summary>
    private static List<AudioEndpoint> Endpoints(int stateMask)
    {
        var result = new List<AudioEndpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            foreach (int flow in new[] { Render, Capture })
            {
                if (enumerator.EnumAudioEndpoints(flow, stateMask, out var list) != 0) continue;
                list.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (list.Item(i, out var device) != 0) continue;
                    try
                    {
                        if (device.GetId(out var id) != 0 || device.OpenPropertyStore(0, out var props) != 0) continue;
                        try
                        {
                            result.Add(new AudioEndpoint(id, flow == Render, ReadString(props, FriendlyAdapterName) ?? "",
                                ReadGuid(props, ContainerId) ?? Guid.Empty, ReadString(props, EnumeratorName) ?? "", ReadUInt(props, FormFactorKey)));
                        }
                        finally { Marshal.ReleaseComObject(props); }
                    }
                    finally { Marshal.ReleaseComObject(device); }
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
    /// Disconnects a Bluetooth audio device (every sound endpoint in <paramref name="container"/>) the way Windows'
    /// own "Disconnect" button does: each endpoint's Bluetooth audio driver (the stereo one, the hands-free one for
    /// calls) is sent KSPROPERTY_ONESHOT_DISCONNECT. Once its audio profiles are down, the device drops the Bluetooth
    /// link unless another app still holds a connection to it. Returns how many profiles accepted it.
    /// </summary>
    public static int DisconnectBluetoothAudio(Guid container) => SendBluetoothOneShot(container, StateActive, KsPropertyOneShotDisconnect);

    /// <summary>
    /// Connects a paired Bluetooth audio device, as Windows' "Connect" button does (and ToothTray): its endpoints,
    /// "unplugged" while it's away, are sent KSPROPERTY_ONESHOT_RECONNECT, and Windows pages the device. Returns how
    /// many profiles accepted the request; whether the device answers (not in its case, in range) shows a few seconds
    /// later as Windows' connected flag.
    /// </summary>
    public static int ConnectBluetoothAudio(Guid container) => SendBluetoothOneShot(container, StateActive | StateUnplugged, KsPropertyOneShotReconnect);

    private const int StateUnplugged = 8;
    private const int KsPropertyOneShotReconnect = 0, KsPropertyOneShotDisconnect = 1;

    private static int SendBluetoothOneShot(Guid container, int stateMask, int property)
    {
        if (container == Guid.Empty) return 0;
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            foreach (var e in Endpoints(stateMask).Where(e => e.Container == container && e.Enumerator.Equals("BTHENUM", StringComparison.OrdinalIgnoreCase)))
                if (FilterOf(enumerator, e.Id) is { } f) filters.Add(f);
            int done = 0;
            foreach (var f in filters)
                if (SendBluetoothOneShot(enumerator, f, property)) done++;
            return done;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static readonly Guid KsPropSetIdBtAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");
    private const int ClsctxAll = 0x17;

    /// <summary>The device id of the driver filter an endpoint is connected to.</summary>
    private static string? FilterOf(IMMDeviceEnumerator enumerator, string endpointId)
    {
        if (enumerator.GetDevice(endpointId, out var device) != 0) return null;
        try
        {
            var iid = typeof(IDeviceTopology).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) != 0) return null;
            var topology = (IDeviceTopology)obj;
            try
            {
                if (topology.GetConnector(0, out var connector) != 0) return null;
                try { return connector.GetDeviceIdConnectedTo(out var id) == 0 ? id : null; }
                finally { Marshal.ReleaseComObject(connector); }
            }
            finally { Marshal.ReleaseComObject(topology); }
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    private static bool SendBluetoothOneShot(IMMDeviceEnumerator enumerator, string filterId, int property)
    {
        if (enumerator.GetDevice(filterId, out var device) != 0) return false;
        try
        {
            var iid = typeof(IKsControl).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) != 0) return false;
            var ks = (IKsControl)obj;
            try
            {
                var p = new KsIdentifier { Set = KsPropSetIdBtAudio, Id = property, Flags = 1 /* KSPROPERTY_TYPE_GET */ };
                return ks.KsProperty(ref p, Marshal.SizeOf<KsIdentifier>(), IntPtr.Zero, 0, out _) == 0;
            }
            finally { Marshal.ReleaseComObject(ks); }
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct KsIdentifier { public Guid Set; public int Id; public int Flags; }

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

    private static int? ReadUInt(IPropertyStore props, PropertyKey key)
    {
        var k = key;
        if (props.GetValue(ref k, out var v) != 0) return null;
        try { return v.Type == 19 /* VT_UI4 */ ? (int)(v.Pointer.ToInt64() & 0xFFFFFFFF) : null; }
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
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IntPtr connectTo);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IntPtr connectedTo);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsIdentifier property, int propertyLength, IntPtr data, int dataLength, out int returned);
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
