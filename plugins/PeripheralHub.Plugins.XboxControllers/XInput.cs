using System.Runtime.InteropServices;

namespace PeripheralHub.Plugins.XboxControllers;

/// <summary>Battery as XInput reports it: a type and one of four levels (no percentage, no charging flag).</summary>
public enum XInputBatteryType : byte { Disconnected = 0, Wired = 1, Alkaline = 2, NiMH = 3, Unknown = 0xFF }

public enum XInputBatteryLevel : byte { Empty = 0, Low = 1, Medium = 2, Full = 3 }

public readonly record struct XInputSlot(int Index, XInputBatteryType Type, XInputBatteryLevel Level)
{
    public bool IsWired => Type == XInputBatteryType.Wired;
}

/// <summary>
/// XInput (xinput1_4.dll, in Windows since 8): up to four Xbox-type controllers in "slots" 0-3, on a cable, the Xbox
/// Wireless Adapter or Bluetooth, plus emulated ones (DSX's Xbox 360 mode). It doesn't say which device is in which slot.
/// </summary>
public static class XInput
{
    private const uint ErrorSuccess = 0;
    private const byte BatteryDevTypeGamepad = 0;

    /// <summary>The connected slots, with their battery.</summary>
    public static IReadOnlyList<XInputSlot> ConnectedSlots()
    {
        var result = new List<XInputSlot>();
        try
        {
            var state = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                if (XInputGetState((uint)i, state) != ErrorSuccess) continue;
                var battery = XInputGetBatteryInformation((uint)i, BatteryDevTypeGamepad, out var info) == ErrorSuccess
                    ? info : new BatteryInformation { Type = (byte)XInputBatteryType.Unknown };
                result.Add(new XInputSlot(i, (XInputBatteryType)battery.Type, (XInputBatteryLevel)Math.Min(battery.Level, (byte)3)));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* no XInput */ }
        return result;
    }

    /// <summary>Rough percentage only for drawing the battery icon; the tile shows the level's name.</summary>
    public static int IconPercent(XInputBatteryLevel level) => level switch
    {
        XInputBatteryLevel.Empty => 5,
        XInputBatteryLevel.Low => 25,
        XInputBatteryLevel.Medium => 60,
        _ => 100,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation { public byte Type, Level; }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint userIndex, byte[] state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint userIndex, byte devType, out BatteryInformation info);
}

/// <summary>Xbox controller device interfaces (XUSB: cable, Xbox Wireless Adapter, emulated), as Windows lists them.</summary>
public static class XusbDevices
{
    private static readonly Guid XusbInterface = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    /// <summary>Instance ids of present XUSB devices, e.g. USB\VID_045E&amp;PID_028E\2&amp;D8604D0&amp;0&amp;1.</summary>
    public static IReadOnlyList<string> InstanceIds()
    {
        var guid = XusbInterface;
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref guid, null, 0) != 0 || len <= 1) return [];
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(Transport.DeviceTree.InstanceIdFromPath).OfType<string>().ToList();
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass, string? deviceId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, int length, int flags);
}
