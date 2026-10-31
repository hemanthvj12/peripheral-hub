using PeripheralHub.Transport;

namespace PeripheralHub.Devices;

/// <summary>
/// Base for request/response devices (mouse and keyboard receivers): keeps one connection open,
/// polls on an interval, and can be woken early when the device pushes a status change.
/// </summary>
public abstract class PolledHidSession : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected HidConnection Connection { get; }
    protected PeripheralHubOptions Options { get; }

    public event Action? Changed;
    public bool IsClosed => Connection.IsClosed;
    /// <summary>When this session started talking to the receiver.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    /// <summary>When the device last answered "reachable"; null if it hasn't since the session started.</summary>
    public DateTimeOffset? LastSeenAt { get; protected set; }
    public HidDeviceInfo Info => Connection.Info;

    private volatile bool _paused;

    /// <summary>
    /// While true (the device maker's app is open) the session sends nothing and only listens, so the two apps don't
    /// collide on the shared command channel. Replies the other app triggers still update the state via <see cref="OnReport"/>.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!value) Wake(); // resume (and refresh) right away
        }
    }

    protected PolledHidSession(HidConnection connection, PeripheralHubOptions options)
    {
        Connection = connection;
        Options = options;
        connection.ReportReceived += OnReport;
        connection.Closed += _ => RaiseChanged();
    }

    public void Start()
    {
        Connection.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested && !Connection.IsClosed)
        {
            if (!Paused)
            {
                try { await PollAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Options.Log?.Invoke($"{GetType().Name}: poll failed: {ex.Message}"); }
            }

            var interval = Paused || IsReachable ? Options.OnlinePollInterval : Options.AsleepPollInterval;
            try { await _wake.WaitAsync(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    protected abstract Task PollAsync(CancellationToken ct);

    /// <summary>Whether the device answered on the last poll; while false the session polls at <see cref="PeripheralHubOptions.AsleepPollInterval"/>.</summary>
    protected abstract bool IsReachable { get; }

    protected virtual void OnReport(byte[] report) { }

    protected void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected void RaiseChanged() => Changed?.Invoke();

    /// <summary>Request with one retry (replies are occasionally dropped right after a link comes up).</summary>
    protected async Task<byte[]?> RequestAsync(byte[] request, Func<byte[], bool> isReply, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var reply = await Connection.RequestAsync(request, isReply, Options.RequestTimeout, ct).ConfigureAwait(false);
            if (reply is not null) return reply;
            if (Connection.IsClosed) break;
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        Connection.Dispose();
    }
}
