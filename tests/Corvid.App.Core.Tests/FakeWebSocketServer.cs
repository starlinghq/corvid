using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Corvid.App.Core.Tests;

/// <summary>
/// A WebSocket server that answers the handshake and then only listens (A6 review M3): every text message a client sends is
/// recorded and none is ever answered. It stands for a process that took attached mode's port or socket path to collect
/// tokens, or for a host that accepts and then says nothing.
/// </summary>
internal sealed class FakeWebSocketServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly ConcurrentBag<Socket> _clients = [];
    private int _connections;

    private FakeWebSocketServer(Socket listener)
    {
        _listener = listener;
        _listener.Listen(8);
        _accepting = AcceptAsync();
    }

    /// <summary>Text messages received, in order of arrival.</summary>
    public ConcurrentQueue<string> Received { get; } = new();

    /// <summary>Completes with the first text message a client sends.</summary>
    public TaskCompletionSource<string> FirstMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Connections accepted.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Bytes any client sent, the handshake included.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytes);

    private long _bytes;

    public int Port => _listener.LocalEndPoint is IPEndPoint ip ? ip.Port : 0;

    public static FakeWebSocketServer Loopback()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return new FakeWebSocketServer(socket);
    }

    public static FakeWebSocketServer Unix(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        return new FakeWebSocketServer(socket);
    }

    /// <summary>Whether any message received holds <paramref name="text"/>.</summary>
    public bool Saw(string text) => Received.Any(message => message.Contains(text, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        foreach (var client in _clients)
            client.Dispose();
        await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _clients.Add(client);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(Socket client)
    {
        try
        {
            var stream = new NetworkStream(client, ownsSocket: true);
            var request = new StringBuilder();
            var buffer = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(buffer, _stop.Token) == 0)
                    return;
                Interlocked.Increment(ref _bytes);
                request.Append((char)buffer[0]);
            }
            var key = request.ToString().Split("\r\n").First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                ["Sec-WebSocket-Key:".Length..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            var frame = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(frame, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                Interlocked.Add(ref _bytes, result.Count);
                message.Write(frame, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                Received.Enqueue(text);
                FirstMessage.TrySetResult(text);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or WebSocketException or OperationCanceledException
                                              or ObjectDisposedException)
        {
        }
    }
}
