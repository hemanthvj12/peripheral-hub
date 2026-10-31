namespace PeripheralHub.Plugins.NothingEar;

/// <summary>
/// Nothing earbuds' control protocol on their own Bluetooth serial service ("NT LINK", <see cref="Service"/>).
/// Protocol facts from the community app abyss-x-desktop (github.com/NehemiahAklil/abyss-x-desktop); this is our own
/// implementation. Verified on the owner's Ear (2) on 2026-10-27.
///
/// Frame: 55 60 01 &lt;command lo&gt; &lt;command hi&gt; &lt;payload length&gt; 00 &lt;operation id&gt; &lt;payload…&gt; &lt;crc lo&gt; &lt;crc hi&gt;,
/// CRC-16/Modbus (init FFFF, poly A001) over everything before it.
/// Battery: request command C007 (no payload); reply 4007, and the buds push E001 when it changes. Payload: a count, then
/// (part, value) pairs: part 02 = left, 03 = right, 04 = case; value bits 0-6 = %, bit 7 = charging.
/// A part missing from the reply isn't connected (a bud out of reach, or the case closed).
///
/// Noise control (ANC), from abyss-x-desktop's Ear (2) page (res/js/bluetooth_socket.js, two.js), added at the owner's
/// request 2026-10-28: read with C01E (reply 401E; the buds push E003 when it changes, e.g. by pinching); set with F00F
/// and payload 01 &lt;mode&gt; 00. The mode byte is payload[1] in replies: 01 = noise cancellation high, 02 = mid,
/// 03 = low, 04 = adaptive, 05 = off, 07 = transparency.
/// Only the battery and ANC commands above are ever sent; the protocol also has EQ, gestures, firmware… which we never send.
/// </summary>
public static class NothingProtocol
{
    public static readonly Guid Service = new("aeac4a03-dff5-498f-843a-34487cf133eb");
    public const ushort NothingVendorId = 0x02B0;

    public const ushort CmdBatteryRequest = 0xC007, CmdBatteryReply = 0x4007, CmdBatteryPush = 0xE001;
    public const ushort CmdAncRequest = 0xC01E, CmdAncReply = 0x401E, CmdAncPush = 0xE003, CmdAncSet = 0xF00F;
    private const int HeaderLength = 8, CrcLength = 2;

    public static byte[] BuildRequest(ushort command, byte operationId) => BuildFrame(command, operationId, []);

    public static byte[] BuildFrame(ushort command, byte operationId, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length + CrcLength];
        frame[0] = 0x55; frame[1] = 0x60; frame[2] = 0x01;
        frame[3] = (byte)command; frame[4] = (byte)(command >> 8);
        frame[5] = (byte)payload.Length;
        frame[6] = 0;
        frame[7] = operationId;
        payload.CopyTo(frame.AsSpan(HeaderLength));
        int end = HeaderLength + payload.Length;
        ushort crc = Crc16(frame.AsSpan(0, end));
        frame[end] = (byte)crc; frame[end + 1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>The F00F frame that sets noise control (strength only matters for cancellation; null = adaptive).</summary>
    public static byte[] BuildAncSet(NoiseMode mode, NoiseStrength? strength, byte operationId) =>
        BuildFrame(CmdAncSet, operationId, [0x01, AncCode(mode, strength), 0x00]);

    public static byte AncCode(NoiseMode mode, NoiseStrength? strength) => mode switch
    {
        NoiseMode.Off => 0x05,
        NoiseMode.Transparency => 0x07,
        _ => (strength ?? NoiseStrength.Adaptive) switch
        {
            NoiseStrength.High => 0x01,
            NoiseStrength.Mid => 0x02,
            NoiseStrength.Low => 0x03,
            _ => 0x04,
        },
    };

    public static bool IsAnc(ushort command) => command is CmdAncReply or CmdAncPush;

    /// <summary>ANC reply/push payload → mode (and strength for cancellation), or null if unknown.</summary>
    public static (NoiseMode Mode, NoiseStrength? Strength)? ParseAnc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return null;
        return payload[1] switch
        {
            0x01 => (NoiseMode.Cancellation, NoiseStrength.High),
            0x02 => (NoiseMode.Cancellation, NoiseStrength.Mid),
            0x03 => (NoiseMode.Cancellation, NoiseStrength.Low),
            0x04 => (NoiseMode.Cancellation, NoiseStrength.Adaptive),
            0x05 => (NoiseMode.Off, null),
            0x07 => (NoiseMode.Transparency, null),
            _ => null,
        };
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>
    /// Takes the first complete, valid frame off the front of <paramref name="buffer"/> (bytes before a 0x55 start, or a
    /// frame with a bad CRC, are dropped). Returns null and leaves the buffer as is when no full frame has arrived yet.
    /// </summary>
    public static (ushort Command, byte[] Payload)? TakeFrame(List<byte> buffer)
    {
        while (buffer.Count > 0)
        {
            int start = buffer.IndexOf(0x55);
            if (start < 0) { buffer.Clear(); return null; }
            if (start > 0) buffer.RemoveRange(0, start);
            if (buffer.Count < HeaderLength) return null;
            // Byte 1 is 0x60 on replies (a CRC follows) and 0x00 on what the buds announce by themselves, e.g. the ANC
            // push "55 00 01 03 E0 03 00 00 01 07 00" (no CRC; captured 2026-10-28 when the mode was changed from the phone).
            bool hasCrc = buffer[1] != 0x00;
            if (!hasCrc && buffer[2] != 0x01) { buffer.RemoveAt(0); continue; } // not a frame start
            int payloadEnd = HeaderLength + buffer[5];
            int length = payloadEnd + (hasCrc ? CrcLength : 0);
            if (buffer.Count < length) return null;

            var frame = buffer.GetRange(0, length).ToArray();
            if (hasCrc)
            {
                ushort crc = Crc16(frame.AsSpan(0, payloadEnd));
                if (frame[^2] != (byte)crc || frame[^1] != (byte)(crc >> 8))
                {
                    buffer.RemoveAt(0); // not a real frame start: look for the next 0x55
                    continue;
                }
            }
            buffer.RemoveRange(0, length);
            return ((ushort)(frame[3] | frame[4] << 8), frame[HeaderLength..payloadEnd]);
        }
        return null;
    }

    public static bool IsBattery(ushort command) => command is CmdBatteryReply or CmdBatteryPush;

    /// <summary>Battery payload → the parts present (left, right, case), or null if malformed.</summary>
    public static NothingBattery? ParseBattery(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        int count = payload[0];
        if (payload.Length < 1 + count * 2) return null;
        NothingBattery result = new(null, null, null);
        for (int i = 0; i < count; i++)
        {
            byte part = payload[1 + i * 2], value = payload[2 + i * 2];
            var level = new PartLevel(Math.Min(value & 0x7F, 100), (value & 0x80) != 0);
            result = part switch
            {
                0x02 => result with { Left = level },
                0x03 => result with { Right = level },
                0x04 => result with { Case = level },
                _ => result,
            };
        }
        return result;
    }
}

public readonly record struct PartLevel(int Percent, bool Charging);

/// <summary>One battery reading; null parts weren't reported (not connected right now).</summary>
public sealed record NothingBattery(PartLevel? Left, PartLevel? Right, PartLevel? Case);
