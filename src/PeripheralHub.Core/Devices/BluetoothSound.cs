using PeripheralHub.Transport;

namespace PeripheralHub.Devices;

/// <summary>
/// "Set as default" for Bluetooth audio devices (owner, 2026-11-01: every audio device, not only USB headsets), for any
/// driver's tile (generic Bluetooth, Nothing Ear…): the hub adds <see cref="DeviceSnapshot.Sound"/> to connected audio
/// snapshots that have a <see cref="DeviceSnapshot.BluetoothMac"/> and no sound info of their own, and switches the
/// default here. Windows files a Bluetooth headset's sound endpoints under the same container as its Bluetooth device
/// node (BTHENUM\DEV_&lt;mac&gt;\… / BTHLE\DEV_&lt;mac&gt;\…, read while enumerating; verified with Ear (2) 2026-11-01:
/// "Headphones", "Headset" and the hands-free mic all in the node's container), which is how they're matched.
/// <para>
/// Speakers only (owner's choice): the mic is left alone, since while an app uses a Bluetooth mic the earbuds drop to
/// call-quality sound. A classic headset can list a stereo output and a hands-free one; the stereo one is used.
/// </para>
/// </summary>
internal sealed class BluetoothSound
{
    /// <summary>The outputs "Set as default" switches to for one device, and whether Windows already uses them.</summary>
    internal sealed record Outputs(IReadOnlyList<string> Ids, bool IsDefault);

    private volatile IReadOnlyDictionary<string, Outputs> _byMac = new Dictionary<string, Outputs>();

    /// <summary>
    /// Re-reads Windows' sound devices for connected Bluetooth audio devices (MAC → the device node's container id,
    /// from the scan). Does nothing (and asks Windows nothing) when there are none.
    /// </summary>
    public void Update(IReadOnlyDictionary<string, Guid> containers)
    {
        if (containers.Count == 0)
        {
            if (_byMac.Count > 0) _byMac = new Dictionary<string, Outputs>();
            return;
        }
        var endpoints = CoreAudio.ActiveEndpoints();
        var (defaultOutput, _) = CoreAudio.Defaults();
        var result = new Dictionary<string, Outputs>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, container) in containers)
        {
            var ids = ChooseOutputs(endpoints, container);
            if (ids.Count > 0) result[mac] = new Outputs(ids, ids.Contains(defaultOutput ?? ""));
        }
        _byMac = result;
    }

    /// <summary>Adds the sound info to a connected Bluetooth audio snapshot that has outputs in Windows.</summary>
    public DeviceSnapshot Decorate(DeviceSnapshot s) =>
        s is { Kind: DeviceKind.Audio, State: LinkState.Online, Sound: null, BluetoothMac: { } mac } && _byMac.TryGetValue(mac, out var o)
            ? s with { Sound = new SoundDevice(HasOutput: true, HasInput: false, o.IsDefault) }
            : s;

    /// <summary>For tests: what <see cref="Update"/> would have found.</summary>
    internal void Use(IReadOnlyDictionary<string, Outputs> byMac) => _byMac = new Dictionary<string, Outputs>(byMac, StringComparer.OrdinalIgnoreCase);

    public bool Handles(string? mac) => mac is not null && _byMac.ContainsKey(mac);

    /// <summary>Makes the device's speakers Windows' default output (every role). Null on success, else a message for the user.</summary>
    public async Task<string?> MakeDefaultAsync(string mac, CancellationToken ct)
    {
        if (!_byMac.TryGetValue(mac, out var o)) return "Windows has no speakers for this device right now.";
        bool ok = await Task.Run(() => o.Ids.Aggregate(true, (all, id) => CoreAudio.SetDefault(id) & all), ct).ConfigureAwait(false);
        if (!ok) return "Windows didn't accept the change.";
        // Show it at once; the next scan re-reads it.
        var updated = new Dictionary<string, Outputs>(_byMac, StringComparer.OrdinalIgnoreCase) { [mac] = o with { IsDefault = true } };
        _byMac = updated;
        return null;
    }

    /// <summary>
    /// The device's outputs: everything in its container except hands-free outputs, unless those are all it has
    /// (newer Windows lists one combined endpoint).
    /// </summary>
    internal static List<string> ChooseOutputs(IEnumerable<AudioEndpoint> endpoints, Guid container)
    {
        var outputs = endpoints.Where(e => e.IsOutput && e.Container == container && container != Guid.Empty).ToList();
        var stereo = outputs.Where(e => !e.IsHandsFree).ToList();
        return (stereo.Count > 0 ? stereo : outputs).Select(e => e.Id).ToList();
    }
}
