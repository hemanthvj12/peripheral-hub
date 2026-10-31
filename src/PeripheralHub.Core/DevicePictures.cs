using System.Text.RegularExpressions;

namespace PeripheralHub;

/// <summary>Which generic picture an audio device without its own product picture gets.</summary>
public static partial class DevicePictures
{
    /// <summary>
    /// Earbuds rather than a headset (owner, 2026-10-29). Windows can't tell: Nothing's Ear (2) reports class of device
    /// 0x240404 "wearable headset" and category Communication.Headset.Bluetooth, exactly like an over-ear headset. So:
    /// separate left/right batteries, or a name that says so ("Buds", "AirPods", "Ear (2)", "earphones", "TWS"…).
    /// Everything else is drawn as a headset.
    /// </summary>
    public static bool IsEarbuds(string name, IReadOnlyList<BatteryPart>? parts) =>
        parts?.Any(p => p.Label is "L" or "R") == true || EarbudName().IsMatch(name);

    [GeneratedRegex(@"\b(ear ?buds?|buds\d*|\w*pods|freebuds|earphones?|in-?ear|tws|ear \(\w+\)|ear\d?\s*(pro|open|a|stick)?$)", RegexOptions.IgnoreCase)]
    private static partial Regex EarbudName();
}
