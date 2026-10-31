namespace PeripheralHub;

/// <summary>A device that is one of Windows' sound devices (a USB headset: its speakers and mic).</summary>
/// <param name="HasOutput">It has speakers/headphones in Windows.</param>
/// <param name="HasInput">It has a mic in Windows.</param>
/// <param name="IsDefault">Windows uses it as the default for everything it has (output and/or mic).</param>
public sealed record SoundDevice(bool HasOutput, bool HasInput, bool IsDefault);
