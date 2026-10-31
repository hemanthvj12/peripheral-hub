namespace PeripheralHub.Tests;

/// <summary>Builds byte arrays from hex strings like "08 04 00 ... 49", zero-padded to a report length.</summary>
internal static class Frames
{
    public static byte[] Hex(string hex, int padTo = 0)
    {
        var bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToList();
        while (bytes.Count < padTo) bytes.Add(0);
        return bytes.ToArray();
    }

    /// <summary>A Sony input report of <paramref name="length"/> bytes with only the report ID and one status byte set.</summary>
    public static byte[] Sony(byte reportId, int length, int statusOffset, byte status)
    {
        var r = new byte[length];
        r[0] = reportId;
        r[statusOffset] = status;
        return r;
    }
}
