using PeripheralHub.Native;

namespace PeripheralHub.Transport;

/// <summary>An input report, valid only during the call.</summary>
public delegate void HidReportHandler(ReadOnlySpan<byte> report);

/// <summary>
/// An open HID collection with one continuous reader. All input reports go to <see cref="ReportReceived"/> and
/// <see cref="ReportSpanReceived"/>;
/// <see cref="RequestAsync"/> writes a request and waits for the first report that matches it.
/// A single reader matters: a second pending read would silently swallow replies.
/// </summary>
public sealed class HidConnection : IDisposable
{
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _pendingLock = new();
    private (Func<byte[], bool> Match, TaskCompletionSource<byte[]> Tcs)? _pending;
    private Task? _reader;

    public HidDeviceInfo Info { get; }
    /// <summary>Each input report as its own array (a copy per report).</summary>
    public event Action<byte[]>? ReportReceived;
    /// <summary>
    /// Each input report without a copy: the span points into the read buffer, which the next read reuses, so it is only
    /// valid during the call. Meant for devices that stream (a game controller sends up to ~250 reports a second);
    /// copy anything you keep.
    /// </summary>
    public event HidReportHandler? ReportSpanReceived;
    /// <summary>Raised once when reading fails (typically the device was unplugged).</summary>
    public event Action<Exception>? Closed;
    public bool IsClosed { get; private set; }

    private HidConnection(HidDeviceInfo info, FileStream stream) { Info = info; _stream = stream; }

    public static HidConnection? TryOpen(HidDeviceInfo info)
    {
        var handle = Win32.CreateFileW(info.Path, Win32.GenericRead | Win32.GenericWrite, Win32.FileShareReadWrite,
            IntPtr.Zero, Win32.OpenExisting, Win32.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return new HidConnection(info, new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true));
    }

    /// <summary>
    /// Whether this device could be opened again right now (a fresh open, closed at once). An open connection keeps
    /// working after another app hides the device (e.g. DSX switching to an emulation mode via HidHide, which only
    /// blocks new opens), so drivers call this to notice a device they should no longer show.
    /// </summary>
    public bool CanReopen()
    {
        using var probe = Win32.CreateFileW(Info.Path, Win32.GenericRead | Win32.GenericWrite, Win32.FileShareReadWrite,
            IntPtr.Zero, Win32.OpenExisting, 0, IntPtr.Zero);
        return !probe.IsInvalid;
    }

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[Math.Max(Info.InputReportLength, 1)];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n <= 0) throw new IOException("HID read returned no data");
                Dispatch(buffer, n);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
            if (!IsClosed) { IsClosed = true; Closed?.Invoke(ex); }
        }
    }

    /// <summary>
    /// Hands one report to a waiting request and the listeners. A copy is made only when something needs its own array
    /// (a pending request or a <see cref="ReportReceived"/> listener), so a streaming controller read through
    /// <see cref="ReportSpanReceived"/> allocates nothing per report.
    /// </summary>
    private void Dispatch(byte[] buffer, int length)
    {
        var report = new ReadOnlySpan<byte>(buffer, 0, length);
        byte[]? copy = null;

        TaskCompletionSource<byte[]>? reply = null;
        lock (_pendingLock)
        {
            if (_pending is { } p)
            {
                copy = report.ToArray();
                if (p.Match(copy)) { reply = p.Tcs; _pending = null; }
            }
        }
        if (reply is not null) reply.TrySetResult(copy!);

        if (ReportReceived is { } listeners) listeners(copy ??= report.ToArray());
        ReportSpanReceived?.Invoke(report);
    }

    /// <summary>Sends <paramref name="request"/> (padded to the output report length) and waits for a matching reply, or null on timeout/failure.</summary>
    public async Task<byte[]?> RequestAsync(byte[] request, Func<byte[], bool> isReply, TimeSpan timeout, CancellationToken ct = default)
    {
        if (IsClosed) return null;
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_pendingLock) _pending = (isReply, tcs);
            var frame = request;
            if (Info.OutputReportLength > 0 && frame.Length != Info.OutputReportLength)
            {
                frame = new byte[Info.OutputReportLength];
                request.AsSpan(0, Math.Min(request.Length, frame.Length)).CopyTo(frame);
            }
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_pendingLock) { if (_pending?.Tcs == tcs) _pending = null; }
            _requestGate.Release();
        }
    }

    /// <summary>Reads a feature report; returns null on failure.</summary>
    public byte[]? GetFeature(byte reportId, int length)
    {
        var buf = new byte[length];
        buf[0] = reportId;
        try { return Win32.HidD_GetFeature(_stream.SafeFileHandle, buf, buf.Length) ? buf : null; }
        catch (ObjectDisposedException) { return null; }
    }

    public void Dispose()
    {
        IsClosed = true;
        _cts.Cancel();
        _stream.Dispose();
        _cts.Dispose();
    }
}
