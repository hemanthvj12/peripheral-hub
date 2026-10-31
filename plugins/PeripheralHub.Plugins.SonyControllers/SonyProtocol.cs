namespace PeripheralHub.Plugins.SonyControllers;

public enum SonyModel { DualSense, DualSenseEdge, DualShock4, DualShock4Dongle }

/// <summary>
/// Battery decoding for Sony controllers, following the Linux hid-playstation / hid-sony drivers.
/// Over Bluetooth both controllers send a short report (0x01) without battery until a calibration
/// feature report is read once (DualSense 0x05, DualShock 4 0x02); they then send the full report.
/// </summary>
public static class SonyProtocol
{
    public const ushort SonyVid = 0x054C;

    public static SonyModel? ModelFor(ushort pid) => pid switch
    {
        0x0CE6 => SonyModel.DualSense,
        0x0DF2 => SonyModel.DualSenseEdge,
        0x05C4 or 0x09CC => SonyModel.DualShock4,
        0x0BA0 => SonyModel.DualShock4Dongle,
        _ => null,
    };

    public static bool IsDualSense(SonyModel m) => m is SonyModel.DualSense or SonyModel.DualSenseEdge;

    /// <summary>Feature report that switches a Bluetooth controller into full reports.</summary>
    public static (byte Id, int Length) BluetoothEnableFeature(SonyModel m) =>
        IsDualSense(m) ? ((byte)0x05, 41) : ((byte)0x02, 37);

    /// <param name="bluetooth">
    /// How the controller is connected (null: either). Matters because Windows pads every report to the full input
    /// length: a Bluetooth controller in basic mode sends 0x01 reports that look like long USB ones but hold no battery
    /// (reading the padding as a USB status showed 5% for a 75% DualSense, 2026-10-29).
    /// </param>
    public static SonyBattery? ParseBattery(SonyModel model, ReadOnlySpan<byte> report, bool? bluetooth = null) =>
        IsDualSense(model) ? ParseDualSense(report, bluetooth) : ParseDualShock4(report, bluetooth);

    /// <summary>USB report 0x01: status at byte 53. Bluetooth report 0x31: status at byte 54.</summary>
    public static SonyBattery? ParseDualSense(ReadOnlySpan<byte> report, bool? bluetooth = null)
    {
        int offset;
        if (bluetooth != true && report.Length >= 64 && report[0] == 0x01) offset = 53;
        else if (bluetooth != false && report.Length >= 78 && report[0] == 0x31) offset = 54;
        else return null;

        byte status = report[offset];
        int level = status & 0x0F;
        int capacity = Math.Min(level * 10 + 5, 100);
        return (status >> 4) switch
        {
            0x0 => new SonyBattery(capacity, ChargeState.Discharging, false),
            0x1 => new SonyBattery(capacity, ChargeState.Charging, true),
            0x2 => new SonyBattery(100, ChargeState.Full, true),
            0xA or 0xB => new SonyBattery(null, ChargeState.Error, true), // voltage / temperature fault
            _ => new SonyBattery(null, ChargeState.Unknown, false),
        };
    }

    /// <summary>
    /// USB/dongle report 0x01: status at byte 30. Bluetooth report 0x11: status at byte 32.
    /// Low nibble = level, bit 4 = cable/power connected. With power connected, 11 means full.
    /// </summary>
    public static SonyBattery? ParseDualShock4(ReadOnlySpan<byte> report, bool? bluetooth = null)
    {
        int offset;
        if (bluetooth != true && report.Length >= 64 && report[0] == 0x01) offset = 30;
        else if (bluetooth != false && report.Length >= 78 && report[0] == 0x11) offset = 32;
        else return null;

        byte status = report[offset];
        int level = status & 0x0F;
        bool cable = (status & 0x10) != 0;
        if (!cable)
            return new SonyBattery(level < 10 ? level * 10 + 5 : 100, ChargeState.Discharging, false);
        return level switch
        {
            < 10 => new SonyBattery(level * 10 + 5, ChargeState.Charging, true),
            10 => new SonyBattery(100, ChargeState.Charging, true),
            11 => new SonyBattery(100, ChargeState.Full, true),
            _ => new SonyBattery(null, ChargeState.Error, true),
        };
    }

    /// <summary>
    /// Sony wireless adapter (0BA0): whether a controller is paired and talking to it.
    /// Per hid-sony, byte 31 bit 2 is set while no controller is connected. Not verified on real hardware.
    /// </summary>
    public static bool? DongleHasController(ReadOnlySpan<byte> report) =>
        report.Length >= 64 && report[0] == 0x01 ? (report[31] & 0x04) == 0 : null;

    /// <summary>
    /// MAC address over USB: DualSense feature 0x09 / DualShock 4 feature 0x12, bytes 1..6 little-endian.
    /// </summary>
    public static (byte Id, int Length) UsbMacFeature(SonyModel m) =>
        IsDualSense(m) ? ((byte)0x09, 20) : ((byte)0x12, 16);

    public static string? ParseUsbMac(ReadOnlySpan<byte> feature)
    {
        if (feature.Length < 7) return null;
        Span<char> chars = stackalloc char[12];
        bool allZero = true;
        for (int i = 0; i < 6; i++)
        {
            byte b = feature[6 - i];
            if (b != 0) allZero = false;
            b.TryFormat(chars.Slice(i * 2, 2), out _, "x2");
        }
        return allZero ? null : new string(chars);
    }
}

public readonly record struct SonyBattery(int? Percent, ChargeState Charging, bool PowerConnected);
