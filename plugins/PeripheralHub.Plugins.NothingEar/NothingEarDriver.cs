using PeripheralHub.Devices;
using PeripheralHub.Transport;

namespace PeripheralHub.Plugins.NothingEar;

/// <summary>
/// Nothing earbuds (vendor 02B0 on Bluetooth, e.g. "Ear (2)"): left, right and case battery with charging, read over
/// their "NT LINK" serial service (<see cref="NothingProtocol"/>).
///
/// While the buds are connected it connects about once a minute, sends the one battery request, reads the reply and
/// hangs up, so a maker's app (Abyss X) can use the channel the rest of the time; while that app is open it doesn't
/// connect at all. Until the first reading (or if the channel can't be reached) it shows Windows' own hands-free level.
/// Like other Bluetooth devices, the buds appear once connected and then stay (Disconnected, last levels) until restart.
/// The tile id is "bt:&lt;mac&gt;", the same as the generic Bluetooth driver's, so pins and remembered levels carry over.
/// </summary>
/// Noise control (owner's request 2026-10-28): for Ear (2) the same short connection also reads the ANC mode, and the
/// tile's ANC row changes it (<see cref="SetNoiseControlAsync"/>: connect, set, read back, hang up). Other Nothing models
/// aren't asked: their mode codes weren't checked. One connection at a time (<see cref="_linkGate"/>).
///
/// Live while watched (owner, 2026-10-28: "ANC modes update instantly in Abyss X when I change them in my phone app"):
/// while the tile's controls are open (<see cref="IWatchableDriver"/>) and Abyss X isn't running, the connection stays
/// open and a reader applies what the buds announce (ANC push E003, battery push E001) straight away; the minute poll and
/// mode changes then go over that connection. Closing the controls (or Abyss X starting) hangs up again.
internal sealed class NothingEarDriver(PeripheralHubOptions options, VendorApp? vendorApp)
    : IDeviceDriver, INoiseControlDriver, IWatchableDriver
{
    private readonly SemaphoreSlim _linkGate = new(1, 1), _sendLock = new(1, 1);
    private HashSet<string> _watchedMacs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LiveRetryAfterFailure = TimeSpan.FromSeconds(30);
    public const string Model = "nothing-ear", Ear2Model = "nothing-ear-2";

    /// <summary>
    /// The protocol's battery reply doesn't say which model it is, so the Windows name picks the artwork: Ear (2) has
    /// its own picture (the default name is "Ear (2)"; a renamed pair falls back to the generic icon).
    /// </summary>
    internal static string ModelFor(string name) =>
        name.Contains("Ear (2)", StringComparison.OrdinalIgnoreCase) ? Ear2Model : Model;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60), RetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8), ReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly Dictionary<string, Earbuds> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private bool _paused;
    private byte _operationId;

    public event Action? Changed;

    private sealed class Earbuds(string mac, int order)
    {
        public string Mac { get; } = mac;
        public int Order { get; } = order;
        public string Name { get; set; } = "Nothing earbuds";
        public bool Connected { get; set; }
        public bool HasService { get; set; }
        public int? WindowsLevel { get; set; }
        public DateTimeOffset? WindowsLevelAt { get; set; }
        public PartLevel? Left, Right, Case;          // last known
        public bool LeftLive, RightLive, CaseLive;   // in the latest reading
        public DateTimeOffset? ReadAt { get; set; }
        public DateTimeOffset? SeenAt { get; set; }
        public DateTimeOffset NextQuery { get; set; } = DateTimeOffset.MinValue;
        public bool Querying { get; set; }
        public bool LastQueryFailed { get; set; }
        public NoiseMode? Anc { get; set; }
        public NoiseStrength? AncStrength { get; set; }   // last cancellation strength seen
        public (NoiseMode Mode, NoiseStrength? Strength)? AncPending { get; set; }
        public bool SupportsAnc => ModelFor(Name) == Ear2Model;
        public BluetoothSerial? Live { get; set; }          // held open while watched
        public bool LiveOpening { get; set; }
        public DateTimeOffset LiveRetryAt { get; set; } = DateTimeOffset.MinValue;
    }

    public void SetWatched(IReadOnlySet<string> deviceIds)
    {
        var macs = new HashSet<string>(deviceIds.Where(id => id.StartsWith("bt:", StringComparison.OrdinalIgnoreCase)).Select(id => id[3..]),
            StringComparer.OrdinalIgnoreCase);
        List<BluetoothSerial> close;
        lock (_lock)
        {
            if (macs.SetEquals(_watchedMacs)) return;
            _watchedMacs = macs;
            close = TakeUnwantedLinks();
        }
        foreach (var l in close) l.Dispose();
        // Opening happens in the next Update (within a scan interval), where connection state is known.
    }

    private bool WantsLive(Earbuds e) => _watchedMacs.Contains(e.Mac) && e.Connected && e.HasService && !_paused;

    /// <summary>Detaches live links that are no longer wanted (call under the lock; dispose them outside it).</summary>
    private List<BluetoothSerial> TakeUnwantedLinks()
    {
        var close = new List<BluetoothSerial>();
        foreach (var e in _known.Values)
            if (e.Live is { } l && !WantsLive(e))
            {
                e.Live = null;
                close.Add(l);
                string why = !_watchedMacs.Contains(e.Mac) ? "controls closed" : !e.Connected ? "Windows reports it disconnected"
                    : !e.HasService ? "NT LINK service not listed" : "the Nothing app is open";
                options.Log?.Invoke($"{e.Name}: closing the live connection ({why})");
            }
        return close;
    }

    public void Update(ScanContext context)
    {
        bool changed = false;
        var start = new List<Earbuds>();
        var openLive = new List<Earbuds>();
        var pollLive = new List<(Earbuds, BluetoothSerial)>();
        List<BluetoothSerial> close;
        lock (_lock)
        {
            bool paused = vendorApp?.IsRunning() ?? false;
            if (paused != _paused) { _paused = paused; changed = true; }

            foreach (var d in context.BluetoothClassic)
            {
                if (d.VendorId != NothingProtocol.NothingVendorId) continue;
                bool hasService = d.HasService(NothingProtocol.Service);
                if (!_known.TryGetValue(d.Mac, out var e))
                {
                    // Show it once it's connected and we can read it (or Windows has a level for it).
                    if (!d.Connected || (!hasService && d.BatteryPercent is null)) continue;
                    e = _known[d.Mac] = new Earbuds(d.Mac, _known.Count);
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(d.Name) && d.Name != e.Name) { e.Name = d.Name; changed = true; }
                e.HasService = hasService;
                if (d.Connected != e.Connected)
                {
                    e.Connected = d.Connected;
                    changed = true;
                    if (!d.Connected) e.LeftLive = e.RightLive = e.CaseLive = false;
                    else e.NextQuery = DateTimeOffset.MinValue; // just connected: read right away
                }
                if (d.Connected)
                {
                    e.SeenAt = context.Now;
                    if (d.BatteryPercent != e.WindowsLevel) { e.WindowsLevel = d.BatteryPercent; e.WindowsLevelAt = d.BatteryUpdatedAt; changed = true; }
                    if (e.Live is { } live)
                    {
                        // Live: the reader applies pushes; still ask every minute in case one was missed.
                        if (context.Now >= e.NextQuery) { e.NextQuery = context.Now + PollInterval; pollLive.Add((e, live)); }
                    }
                    else if (WantsLive(e) && !e.LiveOpening && !e.Querying && context.Now >= e.LiveRetryAt)
                    {
                        e.LiveOpening = true;
                        openLive.Add(e);
                    }
                    else if (e.HasService && !_paused && !e.Querying && !e.LiveOpening && context.Now >= e.NextQuery)
                    {
                        e.Querying = true;
                        start.Add(e);
                    }
                }
            }
            close = TakeUnwantedLinks();
        }
        foreach (var l in close) l.Dispose();
        foreach (var e in start) _ = Task.Run(() => QueryAsync(e, _cts.Token));
        foreach (var e in openLive) _ = Task.Run(() => OpenLiveAsync(e, _cts.Token));
        foreach (var (e, live) in pollLive) _ = Task.Run(() => AskAsync(e, live, _cts.Token));
        if (changed || close.Count > 0) Changed?.Invoke();
    }

    /// <summary>Sends one frame on a link, one sender at a time (the poll and a mode change can overlap).</summary>
    private async Task SendAsync(BluetoothSerial link, byte[] frame, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await link.SendAsync(frame, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Battery (and ANC, for Ear (2)) requests over the live link; the reader applies the replies.</summary>
    private async Task AskAsync(Earbuds e, BluetoothSerial link, CancellationToken ct)
    {
        try
        {
            await SendAsync(link, NothingProtocol.BuildRequest(NothingProtocol.CmdBatteryRequest, NextOperationId()), ct).ConfigureAwait(false);
            if (e.SupportsAnc) await SendAsync(link, NothingProtocol.BuildRequest(NothingProtocol.CmdAncRequest, NextOperationId()), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or OperationCanceledException or IOException) { }
    }

    private async Task OpenLiveAsync(Earbuds e, CancellationToken ct)
    {
        BluetoothSerial? link = null;
        bool gated = false;
        try
        {
            await _linkGate.WaitAsync(ct).ConfigureAwait(false);
            gated = true;
            link = await BluetoothSerial.ConnectAsync(e.Mac, NothingProtocol.Service, ConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            options.Log?.Invoke($"{e.Name}: couldn't open a live connection ({(ex is OperationCanceledException ? "connect timed out" : ex.Message)})");
        }
        finally { if (gated) _linkGate.Release(); }

        bool keep;
        lock (_lock)
        {
            e.LiveOpening = false;
            keep = link is not null && WantsLive(e);
            if (keep) e.Live = link;
            else if (link is null) e.LiveRetryAt = DateTimeOffset.Now + LiveRetryAfterFailure;
        }
        if (!keep) { link?.Dispose(); return; }
        options.Log?.Invoke($"{e.Name}: following live (controls open)");
        await AskAsync(e, link!, ct).ConfigureAwait(false);
        await ReadLiveAsync(e, link!, ct).ConfigureAwait(false);
    }

    /// <summary>Applies everything the buds send on the live link until it closes.</summary>
    private async Task ReadLiveAsync(Earbuds e, BluetoothSerial link, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var chunk = new byte[256];
        string why = "closed";
        try
        {
            while (true)
            {
                int n = await link.ReceiveAsync(chunk, ct).ConfigureAwait(false);
                if (n <= 0) break;
                buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                bool changed = false;
                while (NothingProtocol.TakeFrame(buffer) is { } frame)
                {
                    if (NothingProtocol.IsAnc(frame.Command)) { HandleAnc(e, frame.Payload); changed = true; }
                    else if (NothingProtocol.IsBattery(frame.Command) && NothingProtocol.ParseBattery(frame.Payload) is { } b)
                    {
                        lock (_lock)
                        {
                            Apply(e, b, DateTimeOffset.Now);
                            e.LastQueryFailed = false;
                        }
                        changed = true;
                    }
                }
                if (changed) Changed?.Invoke();
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or OperationCanceledException or IOException)
        {
            why = ex is ObjectDisposedException or OperationCanceledException ? "closed" : ex.Message;
        }

        bool dropped;
        lock (_lock)
        {
            dropped = e.Live == link; // still ours: it dropped by itself, not because we closed it
            if (dropped) { e.Live = null; e.LiveRetryAt = DateTimeOffset.Now + LiveRetryAfterFailure; }
        }
        link.Dispose();
        options.Log?.Invoke($"{e.Name}: live connection {(dropped ? $"dropped ({why})" : "closed")}");
        Changed?.Invoke();
    }

    private byte NextOperationId()
    {
        lock (_lock) return _operationId = (byte)(_operationId % 250 + 1);
    }

    /// <summary>Reads frames until <paramref name="done"/> says so or the reply timeout passes; ANC replies/pushes are applied.</summary>
    private async Task ReadAsync(BluetoothSerial link, Earbuds e, Func<ushort, byte[], bool> done, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var chunk = new byte[256];
        var until = DateTime.UtcNow + ReplyTimeout;
        while (DateTime.UtcNow < until)
        {
            int n = await link.ReceiveAsync(chunk, until - DateTime.UtcNow, ct).ConfigureAwait(false);
            if (n <= 0) return;
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
            while (NothingProtocol.TakeFrame(buffer) is { } frame)
            {
                if (NothingProtocol.IsAnc(frame.Command)) HandleAnc(e, frame.Payload);
                if (done(frame.Command, frame.Payload)) return;
            }
        }
    }

    /// <summary>Applies an ANC reply/push; logs it when it changes (or can't be read).</summary>
    private void HandleAnc(Earbuds e, byte[] payload)
    {
        var anc = NothingProtocol.ParseAnc(payload);
        bool changedAnc;
        lock (_lock)
        {
            changedAnc = anc is { } a && (a.Mode != e.Anc || (a.Strength is { } s && s != e.AncStrength));
            if (anc is { } a2) ApplyAnc(e, a2);
        }
        if (anc is null || changedAnc)
            options.Log?.Invoke($"{e.Name}: noise control reads {(anc is { } r ? $"{r.Mode}{(r.Strength is { } st ? $" ({st})" : "")}" : "unknown")} " +
                                $"[payload {Convert.ToHexString(payload)}]");
    }

    private static void ApplyAnc(Earbuds e, (NoiseMode Mode, NoiseStrength? Strength) anc)
    {
        e.Anc = anc.Mode;
        if (anc.Strength is { } s) e.AncStrength = s;
    }

    private async Task QueryAsync(Earbuds e, CancellationToken ct)
    {
        NothingBattery? battery = null;
        string? error = null;
        bool gated = false;
        try
        {
            await _linkGate.WaitAsync(ct).ConfigureAwait(false);
            gated = true;
            using var link = await BluetoothSerial.ConnectAsync(e.Mac, NothingProtocol.Service, ConnectTimeout, ct).ConfigureAwait(false);
            await link.SendAsync(NothingProtocol.BuildRequest(NothingProtocol.CmdBatteryRequest, NextOperationId()), ct).ConfigureAwait(false);
            bool askAnc = e.SupportsAnc, ancSeen = false;
            if (askAnc) await link.SendAsync(NothingProtocol.BuildRequest(NothingProtocol.CmdAncRequest, NextOperationId()), ct).ConfigureAwait(false);

            await ReadAsync(link, e, (cmd, payload) =>
            {
                if (NothingProtocol.IsBattery(cmd)) battery ??= NothingProtocol.ParseBattery(payload);
                if (cmd == NothingProtocol.CmdAncReply) ancSeen = true;
                return battery is not null && (!askAnc || ancSeen);
            }, ct).ConfigureAwait(false);
            if (battery is null) error = "no battery reply";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            error = ex is OperationCanceledException ? "connect timed out" : ex.Message;
        }
        finally { if (gated) _linkGate.Release(); }

        lock (_lock)
        {
            e.Querying = false;
            var now = DateTimeOffset.Now;
            if (battery is not null)
            {
                Apply(e, battery, now);
                e.NextQuery = now + PollInterval;
                if (e.LastQueryFailed) options.Log?.Invoke($"{e.Name}: battery readable again");
                e.LastQueryFailed = false;
            }
            else
            {
                e.NextQuery = now + RetryAfterFailure;
                if (!e.LastQueryFailed) options.Log?.Invoke($"{e.Name}: couldn't read battery over NT LINK ({error}); showing Windows' level");
                e.LastQueryFailed = true;
            }
        }
        Changed?.Invoke();
    }

    public async Task<string?> SetNoiseControlAsync(string deviceId, NoiseMode mode, NoiseStrength? strength, CancellationToken ct = default)
    {
        Earbuds? e;
        lock (_lock)
        {
            e = _known.Values.FirstOrDefault(x => $"bt:{x.Mac}" == deviceId);
            if (e is null || !e.Connected) return "The earbuds aren't connected.";
            if (!e.SupportsAnc) return "These earbuds' noise control isn't supported.";
            if (_paused) return $"Close {vendorApp?.DisplayName ?? "the Nothing app"} first: it's using the earbuds.";
            strength ??= e.AncStrength;
            e.AncPending = (mode, mode == NoiseMode.Cancellation ? strength ?? NoiseStrength.Adaptive : null);
        }
        Changed?.Invoke(); // show the new mode right away (busy) while it's sent

        string? error = null;
        // A live connection being opened right now: wait for it (a second connection to the service is refused).
        var waitUntil = DateTime.UtcNow + ConnectTimeout;
        while (DateTime.UtcNow < waitUntil)
        {
            lock (_lock) if (!e.LiveOpening) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        BluetoothSerial? live;
        lock (_lock) live = e.Live;
        if (live is not null)
        {
            error = await SetOverLiveAsync(e, live, mode, strength, ct).ConfigureAwait(false);
            lock (_lock) e.AncPending = null;
            Changed?.Invoke();
            return error;
        }

        bool gated = false;
        try
        {
            await _linkGate.WaitAsync(ct).ConfigureAwait(false);
            gated = true;
            using var link = await BluetoothSerial.ConnectAsync(e.Mac, NothingProtocol.Service, ConnectTimeout, ct).ConfigureAwait(false);
            await link.SendAsync(NothingProtocol.BuildAncSet(mode, strength, NextOperationId()), ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            await link.SendAsync(NothingProtocol.BuildRequest(NothingProtocol.CmdAncRequest, NextOperationId()), ct).ConfigureAwait(false);
            bool confirmed = false;
            await ReadAsync(link, e, (cmd, _) => confirmed |= cmd == NothingProtocol.CmdAncReply, ct).ConfigureAwait(false);
            lock (_lock)
                if (!confirmed) error = "The earbuds didn't answer.";
                else if (e.Anc != mode) error = "The earbuds didn't switch.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { error = "Cancelled."; }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            error = "Couldn't reach the earbuds.";
            options.Log?.Invoke($"{e.Name}: noise control change failed ({(ex is OperationCanceledException ? "connect timed out" : ex.Message)})");
        }
        finally { if (gated) _linkGate.Release(); }

        lock (_lock) e.AncPending = null;
        Changed?.Invoke();
        return error;
    }

    /// <summary>Mode change over the live link: send, ask again, and wait for the reader to see the new mode.</summary>
    private async Task<string?> SetOverLiveAsync(Earbuds e, BluetoothSerial live, NoiseMode mode, NoiseStrength? strength, CancellationToken ct)
    {
        var expectedStrength = mode == NoiseMode.Cancellation ? strength ?? NoiseStrength.Adaptive : (NoiseStrength?)null;
        bool Reached()
        {
            lock (_lock) return e.Anc == mode && (expectedStrength is null || e.AncStrength == expectedStrength);
        }
        try
        {
            await SendAsync(live, NothingProtocol.BuildAncSet(mode, strength, NextOperationId()), ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            await SendAsync(live, NothingProtocol.BuildRequest(NothingProtocol.CmdAncRequest, NextOperationId()), ct).ConfigureAwait(false);
            var until = DateTime.UtcNow + ReplyTimeout;
            while (!Reached() && DateTime.UtcNow < until) await Task.Delay(100, ct).ConfigureAwait(false);
            return Reached() ? null : "The earbuds didn't switch.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "Cancelled."; }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or IOException)
        {
            options.Log?.Invoke($"{e.Name}: noise control change over the live connection failed ({ex.Message})");
            return "Couldn't reach the earbuds.";
        }
    }

    private static void Apply(Earbuds e, NothingBattery b, DateTimeOffset now)
    {
        // Parts missing from this reading keep their last level, shown as not live.
        e.LeftLive = b.Left is not null; if (b.Left is { } l) e.Left = l;
        e.RightLive = b.Right is not null; if (b.Right is { } r) e.Right = r;
        e.CaseLive = b.Case is not null; if (b.Case is { } c) e.Case = c;
        e.ReadAt = now;
    }

    public IReadOnlyList<DeviceSnapshot> GetSnapshots(DateTimeOffset now)
    {
        lock (_lock) return _known.Values.OrderBy(e => e.Order).Select(Snapshot).ToList();
    }

    private DeviceSnapshot Snapshot(Earbuds e)
    {
        bool online = e.Connected;
        var buds = new[] { (e.Left, e.LeftLive), (e.Right, e.RightLive) };
        IReadOnlyList<BatteryPart>? parts = e.ReadAt is null ? null :
        [
            Part("L", e.Left, e.LeftLive && online),
            Part("R", e.Right, e.RightLive && online),
            Part("Case", e.Case, e.CaseLive && online),
        ];

        // One summary number (tray tooltip, remembered level): the lower bud, preferring live readings.
        var live = buds.Where(b => b.Item2 && b.Item1 is not null).Select(b => b.Item1!.Value.Percent).ToList();
        var known = buds.Where(b => b.Item1 is not null).Select(b => b.Item1!.Value.Percent).ToList();
        int? summary = live.Count > 0 ? live.Min() : known.Count > 0 ? known.Min() : e.WindowsLevel;

        var charging = e.ReadAt is null ? ChargeState.Unknown
            : buds.Any(b => b.Item2 && b.Item1 is { Charging: true }) ? ChargeState.Charging
            : ChargeState.Discharging;

        string? note = _paused && online ? $"Paused while {vendorApp?.DisplayName ?? "the Nothing app"} is open"
            : e.LastQueryFailed && online ? "Showing Windows' battery level (couldn't reach the earbuds' NT LINK channel)"
            : null;

        return new DeviceSnapshot($"bt:{e.Mac}", e.Name, DeviceKind.Audio,
            online ? ConnectionType.Bluetooth : ConnectionType.None,
            online ? LinkState.Online : LinkState.Disconnected,
            summary, online ? charging : ChargeState.Unknown, e.ReadAt ?? e.WindowsLevelAt, e.SeenAt,
            Note: note, Model: ModelFor(e.Name), Paused: _paused && online, Parts: parts, BluetoothMac: e.Mac,
            NoiseControl: online ? Noise(e) : null);
    }

    /// <summary>The ANC row: shown once the mode has been read; a change being sent shows its target mode, busy.</summary>
    private NoiseControl? Noise(Earbuds e)
    {
        if (e.AncPending is { } p) return new NoiseControl(p.Mode, p.Strength ?? e.AncStrength, CanChange: false, Busy: true);
        if (e.Anc is not { } mode) return null;
        return _paused
            ? new NoiseControl(mode, e.AncStrength, CanChange: false, Reason: $"Close {vendorApp?.DisplayName ?? "the Nothing app"} to change it here")
            : new NoiseControl(mode, e.AncStrength);
    }

    private static BatteryPart Part(string label, PartLevel? level, bool live) =>
        new(label, level?.Percent, live && level is { Charging: true }, live);

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
            foreach (var e in _known.Values)
            {
                e.Live?.Dispose();
                e.Live = null;
            }
    }
}
