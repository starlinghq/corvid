using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Corvid.App.Core.Remote;

/// <summary>
/// One end of a connection that carries whole text messages in order: a WebSocket (<see cref="WebSocketMessageChannel"/>)
/// or, in tests, an in-memory pair (<see cref="InMemoryMessageChannel"/>). Sends may come from any thread; the channel
/// orders them. One reader only.
/// </summary>
public interface IMessageChannel : IAsyncDisposable
{
    /// <summary>Sends one message.</summary>
    /// <exception cref="IOException">The connection is gone.</exception>
    ValueTask SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>The next message, or null when the other end closed the connection.</summary>
    /// <exception cref="InvalidDataException">The message was larger than this end reads, or not text.</exception>
    ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Closes the connection, telling the other end why when it can.</summary>
    ValueTask CloseAsync(string reason, CancellationToken cancellationToken);

    /// <summary>The reason the other end gave when it closed the connection, once it has; untrusted text.</summary>
    string? ClosedBecause => null;
}

/// <summary>A WebSocket as an <see cref="IMessageChannel"/>: text messages, each read whole up to a size limit.</summary>
public sealed class WebSocketMessageChannel(WebSocket socket, int maxMessageBytes) : IMessageChannel
{
    private readonly SemaphoreSlim _sending = new(1, 1);

    public WebSocket Socket { get; } = socket;

    public string? ClosedBecause { get; private set; }

    public async ValueTask SendAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            throw new IOException(exception.Message, exception);
        }
        finally
        {
            _sending.Release();
        }
    }

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await Socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return null;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ClosedBecause = Socket.CloseStatusDescription;
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Attached mode speaks text messages only.");
                if (message.Length + result.Count > maxMessageBytes)
                    throw new InvalidDataException($"A message over {maxMessageBytes} bytes was refused.");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (Socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        // A close reason is at most 123 bytes.
        var text = reason.Length > 100 ? reason[..100] : reason;
        try
        {
            // A browser closes only with 1000 or 3000-4999 (1008 throws there); the reason says why either way.
            var status = OperatingSystem.IsBrowser() ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation;
            await Socket.CloseOutputAsync(status, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException
                                              or ArgumentException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        Socket.Dispose();
        _sending.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Two connected in-memory ends (tests, and an engine attached to in the same process): what one sends the other reads.</summary>
public sealed class InMemoryMessageChannel : IMessageChannel
{
    private readonly Channel<string> _incoming;
    private readonly Channel<string> _outgoing;

    private InMemoryMessageChannel(Channel<string> incoming, Channel<string> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>The reason the other end gave when it closed, if it gave one.</summary>
    public string? ClosedBecause { get; private set; }

    private InMemoryMessageChannel? Other { get; set; }

    public static (InMemoryMessageChannel Client, InMemoryMessageChannel Server) CreatePair()
    {
        var toServer = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var toClient = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var client = new InMemoryMessageChannel(toClient, toServer);
        var server = new InMemoryMessageChannel(toServer, toClient);
        client.Other = server;
        server.Other = client;
        return (client, server);
    }

    public ValueTask SendAsync(string message, CancellationToken cancellationToken) =>
        _outgoing.Writer.TryWrite(message) ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("The connection is closed."));

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (Other is { } other)
            other.ClosedBecause ??= reason;
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync("closed", CancellationToken.None);
}
