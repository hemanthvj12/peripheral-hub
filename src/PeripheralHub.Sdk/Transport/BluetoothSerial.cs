using System.Net;
using System.Net.Sockets;

namespace PeripheralHub.Transport;

/// <summary>
/// A short-lived Bluetooth serial (RFCOMM) connection to one service on a paired classic Bluetooth device, e.g. a maker's
/// own control channel on earbuds. Windows finds the channel from the service ID. Usually connect → ask → close (a maker's
/// app may want the same channel); held open only while the user is watching the device (see IWatchableDriver).
/// </summary>
public sealed class BluetoothSerial : IDisposable
{
    private const AddressFamily Bluetooth = (AddressFamily)32;   // AF_BTH
    private const ProtocolType Rfcomm = (ProtocolType)3;          // BTHPROTO_RFCOMM

    private readonly Socket _socket;

    private BluetoothSerial(Socket socket) => _socket = socket;

    /// <param name="mac">12 hex digits, e.g. "a1b2c3d4e5f6".</param>
    /// <exception cref="SocketException">The device is off, out of range, or doesn't offer the service.</exception>
    /// <exception cref="OperationCanceledException">Not connected within <paramref name="timeout"/>.</exception>
    public static async Task<BluetoothSerial> ConnectAsync(string mac, Guid service, TimeSpan timeout, CancellationToken ct = default)
    {
        var socket = new Socket(Bluetooth, SocketType.Stream, Rfcomm);
        // .NET's async connect (ConnectEx) needs a bound socket, which it can't set up for Bluetooth ("invalid
        // argument"), so connect synchronously on a worker thread; closing the socket cancels it on timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var reg = cts.Token.Register(socket.Dispose);
        try
        {
            var endPoint = new BluetoothEndPoint(Convert.ToUInt64(mac, 16), service);
            await Task.Run(() => socket.Connect(endPoint), CancellationToken.None).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            return new BluetoothSerial(socket);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException && cts.IsCancellationRequested)
        {
            socket.Dispose();
            throw new OperationCanceledException("Bluetooth connect timed out", ex, cts.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default) => _socket.SendAsync(data, SocketFlags.None, ct).AsTask();

    /// <summary>Reads what has arrived (waiting up to <paramref name="timeout"/>); 0 bytes on timeout or when closed.</summary>
    public async Task<int> ReceiveAsync(byte[] buffer, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return await _socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return 0; }
    }

    /// <summary>
    /// Waits for data with no timeout, for a connection held open to hear what the device announces by itself.
    /// 0 bytes means the other side closed the connection.
    /// </summary>
    public Task<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => _socket.ReceiveAsync(buffer, SocketFlags.None, ct).AsTask();

    public void Dispose() => _socket.Dispose();

    /// <summary>SOCKADDR_BTH: address family, 8-byte device address, service class ID, port 0 (look the channel up by service).</summary>
    private sealed class BluetoothEndPoint(ulong address, Guid service) : EndPoint
    {
        public override AddressFamily AddressFamily => Bluetooth;

        public override SocketAddress Serialize()
        {
            var sa = new SocketAddress(Bluetooth, 30);
            var addr = BitConverter.GetBytes(address);
            for (int i = 0; i < 8; i++) sa[2 + i] = addr[i];
            var guid = service.ToByteArray();
            for (int i = 0; i < 16; i++) sa[10 + i] = guid[i];
            return sa;
        }

        public override EndPoint Create(SocketAddress socketAddress) => this;
    }
}
