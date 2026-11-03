namespace PeripheralHub;

/// <summary>
/// One of the devices a controller is paired with (a DualSense remembers up to four, one per PS + face button).
/// </summary>
/// <param name="Slot">The slot's position in the controller's list (0-based; a DualSense lists them △ ○ ✕ □).</param>
/// <param name="Button">The button that switches to it, e.g. "△".</param>
/// <param name="Mac">The paired device's Bluetooth address (12 lower-case hex digits); null when the slot is empty.</param>
/// <param name="InUse">The controller is using this slot right now.</param>
public sealed record PairedHost(int Slot, string Button, string? Mac, bool InUse);
