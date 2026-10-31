namespace PeripheralHub.Plugins.AulaF75;

/// <summary>
/// AULA F75 2.4G receiver protocol ("3632" family in the vendor's OemDrv.exe).
/// Channel: HID usage page 0xFF02 / usage 0x02, 20-byte reports, report ID 0x13.
/// Frame: [0x13, cmd, b2, 0, ..., checksum] where checksum = sum(bytes 0..18).
/// Reply: byte 1 = cmd (bit 7 set means the receiver flagged a CRC error), byte 4 = payload length, payload from byte 5.
/// </summary>
public static class AulaProtocol
{
    public const int ReportLength = 20;
    public const byte ReportId = 0x13;
    /// <summary>Is the keyboard linked to the receiver. Sent by the vendor app with b2 = 1.</summary>
    public const byte CmdOnline = 0x07;
    /// <summary>
    /// Battery query. Not used by the vendor app; documented by the community
    /// (ushineko/sanshoku #36, PyFlat/Device-Battery-Info #6) and verified on this F75.
    /// </summary>
    public const byte CmdBattery = 0x4A;

    /// <summary>Pushed by the receiver on link and power changes (see <see cref="ParseStatusPush"/>).</summary>
    public const byte CmdStatusPush = 0x0A;

    public const byte StateOnBattery = 0x01;
    public const byte StateCableIn = 0x10;

    public static byte Checksum(ReadOnlySpan<byte> frame)
    {
        byte sum = 0;
        for (int i = 0; i < 19; i++) sum += frame[i];
        return sum;
    }

    public static byte[] BuildRequest(byte cmd, byte b2 = 0)
    {
        var frame = new byte[ReportLength];
        frame[0] = ReportId;
        frame[1] = cmd;
        frame[2] = b2;
        frame[19] = Checksum(frame);
        return frame;
    }

    public static byte[] OnlineRequest() => BuildRequest(CmdOnline, 0x01);
    public static byte[] BatteryRequest() => BuildRequest(CmdBattery);

    public static bool IsValid(ReadOnlySpan<byte> report) =>
        report.Length >= ReportLength && report[0] == ReportId && report[19] == Checksum(report);

    /// <summary>Matches a reply to <paramref name="cmd"/>, including one flagged with a CRC error (bit 7).</summary>
    public static bool IsReplyTo(ReadOnlySpan<byte> report, byte cmd) => IsValid(report) && (report[1] & 0x7F) == cmd;

    public static bool HasCrcError(ReadOnlySpan<byte> report) => (report[1] & 0x80) != 0;

    public static bool? ParseOnline(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, CmdOnline) || HasCrcError(report) || report[4] < 1) return null;
        return report[5] != 0;
    }

    /// <summary>byte 5 = level 1-100, byte 6 = state (0x01 on battery, 0x10 cable in; level is pinned at 100 then).</summary>
    public static AulaBattery? ParseBattery(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, CmdBattery) || HasCrcError(report)) return null;
        int level = report[5];
        // The state is a set of flags: 0x10 = external power. At the moment of plugging in it briefly reads 0x11.
        bool cable = (report[6] & StateCableIn) != 0;
        return ToBattery(level, report[6]);
    }

    private static AulaBattery ToBattery(int level, byte state)
    {
        // The state is a set of flags: 0x10 = external power. At the moment of plugging in it briefly reads 0x11.
        bool cable = (state & StateCableIn) != 0;
        int? percent = level is >= 1 and <= 100 && !cable ? level : null;
        return new AulaBattery(percent, cable, state);
    }

    /// <summary>
    /// Status frames the receiver pushes on its own (cmd 0x0A, payload length in byte 4, kind in byte 5):
    /// kind 0x02 = link state (byte 6: 1 linked, 0 dropped); kind 0x05 = battery (byte 6 level, byte 7 state),
    /// sent when the keyboard is switched on. Captured 2026-10-26.
    /// </summary>
    public static (bool? Linked, AulaBattery? Battery) ParseStatusPush(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, CmdStatusPush) || HasCrcError(report)) return (null, null);
        return report[4] switch
        {
            >= 2 when report[5] == 0x02 => (report[6] != 0, null),
            >= 3 when report[5] == 0x05 => (null, ToBattery(report[6], report[7])),
            _ => (null, null),
        };
    }
}

/// <param name="Percent">Null while the cable is in (level pinned to 100) or when no reading is available.</param>
public readonly record struct AulaBattery(int? Percent, bool CableIn, byte RawState);
