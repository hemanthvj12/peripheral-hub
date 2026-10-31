namespace PeripheralHub;

public enum NoiseMode { Cancellation, Transparency, Off }

public enum NoiseStrength { High, Mid, Low, Adaptive }

/// <summary>Noise control of earbuds/headphones, as last read from the device.</summary>
/// <param name="Mode">The current mode.</param>
/// <param name="Strength">
/// The noise-cancellation strength: the current one while <see cref="Mode"/> is Cancellation, otherwise the last one
/// seen (used when cancellation is turned on again). Null if never seen.
/// </param>
/// <param name="CanChange">False while it can't be changed right now (e.g. the maker's app is using the channel).</param>
/// <param name="Busy">A change is being sent.</param>
/// <param name="Reason">Why it can't be changed, for a tooltip.</param>
public sealed record NoiseControl(NoiseMode Mode, NoiseStrength? Strength, bool CanChange = true, bool Busy = false,
    string? Reason = null);
