using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Corvid.App.Core.Remote;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Closing attached mode's JSON-RPC (A6 integration): a refusal's answer and the close with its reason leave before the
/// reading is cancelled, since cancelling a WebSocket's read aborts the socket and, on a busy machine, dropped both; and an
/// end the other closed closes back.
/// </summary>
public sealed class JsonRpcPeerCloseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A channel that records what happens to it, in order; its read waits until the test closes it or it is cancelled.</summary>
    private sealed class RecordingChannel : IMessageChannel
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

        public ConcurrentQueue<string> Events { get; } = new();

        public TaskCompletionSource ClosedOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Deliver(string? message) => _incoming.Writer.TryWrite(message);

        public ValueTask SendAsync(string message, CancellationToken cancellationToken)
        {
            Events.Enqueue("sent " + (JsonNode.Parse(message)?["error"] is null ? "result" : "error"));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Events.Enqueue("read cancelled");
                throw;
            }
        }

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
        {
            Events.Enqueue("close " + reason);
            ClosedOnce.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_fatal_refusal_and_its_close_leave_before_the_read_is_cancelled()
    {
        var channel = new RecordingChannel();
        var peer = new JsonRpcPeer(channel, (_, _, _) => throw new JsonRpcException(AttachedProtocol.Errors.Unauthorized, "that token is refused") { Fatal = true },
            onNotification: null);
        peer.Start();
        channel.Deliver("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal("that token is refused", await peer.Closed.WaitAsync(Ct));
        // The other end reads the answer and the close, then closes back: the reading ends with it.
        await channel.ClosedOnce.Task.WaitAsync(Ct);
        Assert.DoesNotContain("read cancelled", channel.Events);
        channel.Deliver(null);
        await peer.DisposeAsync();

        Assert.Equal(["sent error", "close that token is refused"], channel.Events.Take(2));
        Assert.DoesNotContain("read cancelled", channel.Events);
    }

    [Fact]
    public async Task An_end_the_other_closed_closes_back()
    {
        var channel = new RecordingChannel();
        var peer = new JsonRpcPeer(channel, onRequest: null, onNotification: null);
        peer.Start();
        channel.Deliver(null);

        Assert.Null(await peer.Closed.WaitAsync(Ct));
        await peer.DisposeAsync();
        Assert.Equal(["close closed"], channel.Events);
    }
}
