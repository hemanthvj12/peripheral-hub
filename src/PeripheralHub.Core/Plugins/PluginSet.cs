namespace PeripheralHub.Plugins;

/// <summary>
/// The plugins the hub knows about. <see cref="Available"/> ones are on disk but dormant (no code loaded); a plugin
/// becomes <see cref="Active"/> once one of its devices is detected, and stays active after that (remembered across
/// restarts). Lookups (artwork, makers' apps, Bluetooth claims) only consider active plugins. Safe to read from any thread.
/// </summary>
public sealed class PluginSet
{
    private volatile IReadOnlyList<LoadedPlugin> _active = [];
    private volatile IReadOnlyList<PluginError> _errors;

    public PluginSet(IReadOnlyList<LoadedPlugin> available, IReadOnlyList<PluginError> errors)
    {
        Available = available;
        _errors = errors;
    }

    public static PluginSet Empty => new([], []);

    /// <summary>Every plugin found on disk (one per id), active or not.</summary>
    public IReadOnlyList<LoadedPlugin> Available { get; }
    /// <summary>Plugins whose code is loaded and whose drivers are running, in the order they were turned on.</summary>
    public IReadOnlyList<LoadedPlugin> Active => _active;
    /// <summary>Plugin folders that were skipped or failed to start (bad manifest, missing dll, needs a newer app…).</summary>
    public IReadOnlyList<PluginError> Errors => _errors;

    public bool IsActive(string id) => Active.Any(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    internal void MarkActive(LoadedPlugin plugin) => _active = [.. _active, plugin];
    internal void AddError(PluginError error) => _errors = [.. _errors, error];

    /// <summary>The maker's app for a <see cref="DeviceSnapshot.Model"/>, declared in any installed plugin's manifest.</summary>
    public VendorApp? VendorAppFor(string? model) =>
        model is null ? null : Active.Concat(Available).Select(p => p.Manifest.VendorAppFor(model)).FirstOrDefault(a => a is not null);

    /// <summary>
    /// True if an active plugin handles this Bluetooth device itself, so the generic Bluetooth driver should leave it alone.
    /// Without the plugin the device still shows, as a plain Bluetooth device with Windows' battery level.
    /// </summary>
    public bool ClaimsBluetooth(ushort vendorId, ushort? productId) =>
        Active.Any(p => p.Manifest.Devices.Any(d => d.IsBluetooth && d.Matches(vendorId, productId)));
}
