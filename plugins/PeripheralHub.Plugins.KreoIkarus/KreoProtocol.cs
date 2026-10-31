namespace PeripheralHub.Plugins.KreoIkarus;

/// <summary>
/// Kreo Ikarus vendor protocol (Compx chipset), recovered from the vendor's HIDUsb.dll.
/// Channel: HID usage page 0xFF02 / usage 0x02, 17-byte reports, report ID 0x08.
/// Frame: [0x08, cmd, payload..., checksum] where checksum = 0x55 - sum(bytes 0..15).
/// </summary>
public static class KreoProtocol
{
    public const int ReportLength = 17;
    public const byte ReportId = 0x08;
    public const byte CmdOnline = 0x03;
    public const byte CmdBattery = 0x04;
    /// <summary>Pushed by the mouse when its status changes (bit 6 of byte 6 = battery changed).</summary>
    public const byte CmdStatusChanged = 0x0A;

    public static byte Checksum(ReadOnlySpan<byte> frame)
    {
        byte sum = 0;
        for (int i = 0; i < 16; i++) sum += frame[i];
        return (byte)(0x55 - sum);
    }

    public static byte[] BuildRequest(byte cmd)
    {
        var frame = new byte[ReportLength];
        frame[0] = ReportId;
        frame[1] = cmd;
        frame[16] = Checksum(frame);
        return frame;
    }

    public static bool IsValid(ReadOnlySpan<byte> report) =>
        report.Length >= ReportLength && report[0] == ReportId && report[16] == Checksum(report);

    public static bool IsReplyTo(ReadOnlySpan<byte> report, byte cmd) => IsValid(report) && report[1] == cmd;

    public static bool IsStatusPush(ReadOnlySpan<byte> report) => IsReplyTo(report, CmdStatusChanged);

    /// <summary>Reply to <see cref="CmdOnline"/>: byte 6 = 1 when the mouse is reachable.</summary>
    public static bool? ParseOnline(ReadOnlySpan<byte> report) =>
        IsReplyTo(report, CmdOnline) ? report[6] != 0 : null;

    /// <summary>
    /// Voltage (mV) at 0%, 5%, 10% … 100%, copied from the Kreo app's own config
    /// (<c>Sys64\usersetting\Config.ini</c>, <c>BatteryParam</c>).
    /// </summary>
    public static readonly int[] VoltageTable =
        [3050, 3170, 3230, 3300, 3600, 3660, 3720, 3760, 3800, 3840, 3880, 3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110];

    /// <summary>Battery % from voltage, linearly interpolated between the table's 5% steps (as the Kreo app does).</summary>
    public static int PercentFromMillivolts(int mv)
    {
        if (mv <= VoltageTable[0]) return 0;
        if (mv >= VoltageTable[^1]) return 100;
        int i = 1;
        while (mv > VoltageTable[i]) i++;
        double lo = VoltageTable[i - 1], hi = VoltageTable[i];
        return (int)Math.Round(5 * (i - 1) + 5 * (mv - lo) / (hi - lo), MidpointRounding.AwayFromZero);
    }

    /// <summary>Reply to <see cref="CmdBattery"/>: byte 6 = percent, byte 7 = charging flag, bytes 8-9 = millivolts (big-endian).</summary>
    public static KreoBattery? ParseBattery(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, CmdBattery)) return null;
        int percent = report[6];
        if (percent > 100) return null;
        return new KreoBattery(percent, report[7] != 0, (report[8] << 8) | report[9]);
    }
}

/// <param name="Percent">The level the mouse reports itself (coarse, 5% steps).</param>
public readonly record struct KreoBattery(int Percent, bool Charging, int Millivolts)
{
    /// <summary>
    /// The level the Kreo app shows: interpolated from the voltage with its own table, so the two apps agree.
    /// While charging the voltage reads high (4.16 V on the cable = "100%" while the mouse said 90%), so the
    /// mouse's own <see cref="Percent"/> is used then.
    /// </summary>
    public int DisplayPercent => Charging || Millivolts <= 0 ? Percent : KreoProtocol.PercentFromMillivolts(Millivolts);
}
