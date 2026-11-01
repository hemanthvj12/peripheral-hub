using System.IO.Pipes;
using System.Text;

namespace PeripheralHub.Integration;

/// <summary>What HapticLink's service says about itself (its STATUS reply).</summary>
/// <param name="State">"off", "waiting" (no DualSense on Bluetooth), "starting", "on" or "error".</param>
/// <param name="Enabled">The "Full DualSense features" switch.</param>
/// <param name="Haptics">Haptics are relayed too (needs Steam's streaming speaker driver).</param>
/// <param name="ControllerMac">The bridged controller's Bluetooth MAC (12 lower-case hex digits), or null.</param>
/// <param name="Message">HapticLink's own one-line status text.</param>
public sealed record HapticLinkStatus(string State, bool Enabled, bool Haptics, string? ControllerMac, string Message)
{
    /// <summary>Games see the controller with this MAC as a wired DualSense right now.</summary>
    public bool IsBridging(string? mac) =>
        State == "on" && ControllerMac is not null && string.Equals(ControllerMac, mac, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the part of a STATUS reply after "OK ": <c>state|enabled|haptics|mac|message</c>.</summary>
    public static HapticLinkStatus? Parse(string reply)
    {
        var f = reply.Split('|', 5);
        if (f.Length < 5 || f[0].Length == 0) return null;
        string mac = f[3].Trim().Replace(":", "").ToLowerInvariant();
        return new HapticLinkStatus(f[0], f[1] == "1", f[2] == "1", mac.Length == 12 && mac.All(Uri.IsHexDigit) ? mac : null, f[4]);
    }
}

/// <summary>
/// HapticLink for DualSense (the owner's separate app, github.com/hemanthvj12/haptic-link) gives games a virtual wired
/// DualSense fed by the real one on Bluetooth, so haptics and adaptive triggers work. Owner's rule (2026-11-02): only
/// HapticLink talks to the controller; PeripheralHub reads its battery, shows the waves on its tile and offers the switch,
/// all through HapticLink's control pipe "HapticLink" (one line per command, "OK …" or "ERR …" back).
/// </summary>
public static class HapticLink
{
    private const string PipeName = "HapticLink";
    /// <summary>The pipe protocol this client speaks; HapticLink answers HELLO with its own.</summary>
    public const int Protocol = 1;

    /// <summary>
    /// Says hello as PeripheralHub, which makes HapticLink keep this exe on HidHide's allowed list while it hides the real
    /// controller from games (so its battery still shows). Returns HapticLink's version, or null when it isn't running.
    /// </summary>
    public static Version? Hello() =>
        Ask("HELLO PeripheralHub") is { } reply && Version.TryParse(reply.Split(' ')[0], out var v) ? v : null;

    /// <summary>Null when HapticLink isn't installed or its service isn't running.</summary>
    public static HapticLinkStatus? Status() => Ask("STATUS") is { } reply ? HapticLinkStatus.Parse(reply) : null;

    /// <summary>Turns the switch on or off (HapticLink remembers it). False when HapticLink couldn't be reached.</summary>
    public static bool SetEnabled(bool enabled) => Ask(enabled ? "ENABLE" : "DISABLE") is not null;

    private static string? Ask(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(300);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine(command);
            var reply = reader.ReadLine();
            return reply is not null && reply.StartsWith("OK", StringComparison.Ordinal) ? (reply.Length > 3 ? reply[3..] : "") : null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return null; }
    }
}
