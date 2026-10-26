using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Corvid.App.Core.Composition;

namespace Corvid.App.Core.Remote;

/// <summary>A JSON-RPC error: from the other end, or one a handler raises to answer with a code of its choosing.</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>Raised by a handler: the connection closes once this error has been sent.</summary>
    public bool Fatal { get; init; }
}

/// <summary>
/// One end of attached mode's JSON-RPC (<see cref="AttachedProtocol"/>) over an <see cref="IMessageChannel"/>: requests both
/// ways, notifications in order. Everything it sends goes through one queue, so a notification sent after another arrives
/// after it, and a send never blocks the caller (an engine callback on the dispatcher). Requests it receives run one at a
/// time in the order they came (A6 review M1: a mode set before a prompt applies to it, a queue move before a removal comes
/// first), except those <see cref="RunsLong"/> names, which are started in their turn but not waited for, so a request that
/// waits on a person (a worktree's card) never holds up the answer to it. At most
/// <see cref="AttachedProtocol.MaxConcurrentRequests"/> are queued or running at once; either end may give one up with
/// <see cref="AttachedProtocol.CancelRequest"/>. Notifications run in order on the reading loop. Everything read is
/// untrusted: a message that is not JSON-RPC closes the connection.
/// </summary>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    /// <summary>Answers a request; the result (null for none) or a thrown exception goes back to the caller.</summary>
    public delegate Task<JsonNode?> RequestHandler(string method, JsonObject? parameters, CancellationToken cancellationToken);

    private readonly IMessageChannel _channel;
    private readonly RequestHandler? _onRequest;
    private readonly Action<string, JsonObject?>? _onNotification;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new(StringComparer.Ordinal);
    private readonly Channel<Incoming> _ordered = Channel.CreateUnbounded<Incoming>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _answering;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _closeGate = new();
    private Task? _closing;
    private long _nextId;
    private long _queuedBytes;
    private int _running;
    private Task? _reading;
    private Task? _writing;

    public JsonRpcPeer(IMessageChannel channel, RequestHandler? onRequest, Action<string, JsonObject?>? onNotification)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _onRequest = onRequest;
        _onNotification = onNotification;
    }

    /// <summary>The longest a message queued for the other end may be in bytes, in total, before the connection is dropped.</summary>
    public long MaxQueuedBytes { get; init; } = AttachedProtocol.MaxQueuedBytes;

    /// <summary>
    /// Requests that are started in their turn but not waited for before the next (A6 review M1), and that the caller sets
    /// no <see cref="RequestTimeout"/>; null runs every request in turn.
    /// </summary>
    public Func<string, bool>? RunsLong { get; init; }

    /// <summary>
    /// How long <see cref="RequestAsync"/> waits for an answer to a request that does not <see cref="RunsLong"/> before it
    /// gives up, tells the other end to cancel it and fails (A6 review M2). Infinite by default.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>The clock <see cref="RequestTimeout"/> runs on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// A request's handler failed with an exception no error code describes (A6 review L3): the other end is told only that
    /// it failed, and this hears the method and the exception, to log.
    /// </summary>
    public Action<string, Exception>? OnUnexpected { get; init; }

    /// <summary>Completes when the connection has ended, with the reason when this end closed it.</summary>
    public Task<string?> Closed => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>Starts reading and writing.</summary>
    public void Start()
    {
        _writing ??= WriteAsync();
        if (_onRequest is not null)
            _answering ??= AnswerInTurnAsync();
        _reading ??= ReadAsync();
    }

    /// <summary>Calls a method on the other end and waits for its result.</summary>
    /// <exception cref="JsonRpcException">The other end answered with an error.</exception>
    /// <exception cref="IOException">The connection ended first, or (<see cref="RequestTimeout"/>) no answer came in time.</exception>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        var limit = RunsLong?.Invoke(method) == true ? Timeout.InfiniteTimeSpan : RequestTimeout;
        using var late = limit == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(limit, Time);
        try
        {
            if (IsClosed)
                throw new IOException("The connection is closed.");
            Enqueue(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            using (cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken)))
            using (late?.Token.Register(() => answer.TrySetException(new IOException($"No answer to {method} within {limit.TotalSeconds:0} s."))))
                return await answer.Task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException || late is { IsCancellationRequested: true })
        {
            // A6 review L4: the other end stops working on what nobody waits for any more.
            Notify(AttachedProtocol.CancelRequest, new JsonObject { ["id"] = id });
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification, after everything sent before it. Dropped when the connection is closed.</summary>
    public void Notify(string method, JsonObject? parameters)
    {
        if (!IsClosed)
            Enqueue(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
    }

    /// <summary>Closes the connection, telling the other end why; pending requests fail.</summary>
    public ValueTask CloseAsync(string reason) => CloseAsync(reason, fromReading: false);

    // The reading loop's own close does not wait for the reading to end.
    private ValueTask CloseAsync(string reason, bool fromReading)
    {
        // One close runs; a later call (the disposal after it) waits for it, so the reason given first is the one sent.
        lock (_closeGate)
        {
            _closing ??= _closed.TrySetResult(reason) ? CloseCoreAsync(reason, fromReading) : Task.CompletedTask;
            return new ValueTask(_closing);
        }
    }

    private async Task CloseCoreAsync(string reason, bool fromReading)
    {
        _outgoing.Writer.TryComplete();
        // What was queued before the close (an error answer) goes out first, briefly.
        if (_writing is { } writing)
            await writing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _channel.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false);
        // Reading goes on until the other end closes back, briefly. Cancelling a WebSocket's read aborts the socket, and on a
        // busy machine the abort dropped the refusal's answer and the close with its reason before they had left.
        if (_reading is { IsCompleted: false } reading && !fromReading)
            await reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Finish();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("closed").ConfigureAwait(false);
        if (_reading is { } reading)
            await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _channel.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private void Finish()
    {
        _closed.TrySetResult(null);
        _outgoing.Writer.TryComplete();
        _ordered.Writer.TryComplete();
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("The attached Corvid's connection ended."));
    }

    private void Enqueue(JsonObject message)
    {
        var text = message.ToJsonString(AttachedJson.Options);
        var size = Encoding.UTF8.GetByteCount(text);
        if (Interlocked.Add(ref _queuedBytes, size) > MaxQueuedBytes)
        {
            // The other end does not read: rather than hold the engine's updates without bound, drop it.
            _ = CloseAsync("too much unread").AsTask();
            return;
        }
        if (!_outgoing.Writer.TryWrite(text))
            Interlocked.Add(ref _queuedBytes, -size);
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var text in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _channel.SendAsync(text, _stop.Token).ConfigureAwait(false);
                Interlocked.Add(ref _queuedBytes, -Encoding.UTF8.GetByteCount(text));
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException
                                              or InvalidOperationException)
        {
            Finish();
            return;
        }
        // The queue was completed by Finish or by a close; a close sends the channel's close and then finishes itself.
        if (!_closed.Task.IsCompleted)
            Finish();
    }

    private async Task ReadAsync()
    {
        await Task.Yield();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var text = await _channel.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (text is null)
                {
                    // The other end closed: close back (a WebSocket's closing handshake) unless this end is closing already.
                    if (!_closed.Task.IsCompleted)
                        await _channel.CloseAsync("closed", CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                if (!Dispatch(text))
                {
                    await CloseAsync("not JSON-RPC", fromReading: true).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (InvalidDataException exception)
        {
            await CloseAsync(exception.Message, fromReading: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Handles one message; false when it is not JSON-RPC.</summary>
    private bool Dispatch(string text)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
        if (message is null || message["jsonrpc"]?.GetValueKind() != JsonValueKind.String || (string?)message["jsonrpc"] != "2.0")
            return false;
        var id = message["id"];
        var methodNode = message["method"];
        if (methodNode is not null)
        {
            if (methodNode.GetValueKind() != JsonValueKind.String)
                return false;
            var method = methodNode.GetValue<string>();
            var parameters = message["params"] as JsonObject;
            if (message["params"] is { } given && given is not JsonObject)
                return false;
            if (id is null)
            {
                if (method == AttachedProtocol.CancelRequest)
                {
                    // A6 review L4: the request named (one of the other end's, by its id) stops; an unknown id is ignored.
                    if (parameters?["id"] is JsonValue cancelled && cancelled.GetValueKind() is JsonValueKind.Number or JsonValueKind.String
                        && _incoming.TryGetValue(cancelled.ToJsonString(), out var running))
                    {
                        try
                        {
                            running.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // It finished meanwhile.
                        }
                    }
                    return true;
                }
                _onNotification?.Invoke(method, parameters);
                return true;
            }
            if (id.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.String))
                return false;
            Answer(id.DeepClone(), method, parameters);
            return true;
        }
        // A response to one of ours: ids are this end's numbers; anything else is ignored.
        if (id is null || id.GetValueKind() != JsonValueKind.Number || !id.AsValue().TryGetValue<long>(out var number))
            return false;
        if (!_pending.TryGetValue(number, out var pending))
            return true;
        if (message["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var c) ? c : AttachedProtocol.Errors.Internal;
            var words = error["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var m) ? m : "error";
            pending.TrySetException(new JsonRpcException(code, words));
        }
        else
        {
            pending.TrySetResult(message["result"]?.DeepClone());
        }
        return true;
    }

    /// <summary>A request received, waiting for its turn or running.</summary>
    private sealed record Incoming(JsonNode Id, string Key, string Method, JsonObject? Parameters, CancellationTokenSource Cancel);

    private void Answer(JsonNode id, string method, JsonObject? parameters)
    {
        if (_onRequest is null)
        {
            Reply(id, method, null, new JsonRpcException(AttachedProtocol.Errors.MethodNotFound, $"No method {method}."));
            return;
        }
        // Queued and running together: a client cannot pile up work behind a slow request.
        if (Interlocked.Increment(ref _running) > AttachedProtocol.MaxConcurrentRequests)
        {
            Interlocked.Decrement(ref _running);
            Reply(id, method, null, new JsonRpcException(AttachedProtocol.Errors.Busy, "Too many requests at once."));
            return;
        }
        var key = id.ToJsonString();
        CancellationTokenSource cancel;
        try
        {
            cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Decrement(ref _running);
            return;
        }
        if (!_incoming.TryAdd(key, cancel))
        {
            cancel.Dispose();
            Interlocked.Decrement(ref _running);
            Reply(id, method, null, new JsonRpcException(AttachedProtocol.Errors.InvalidRequest, "That request id is in use."));
            return;
        }
        if (!_ordered.Writer.TryWrite(new Incoming(id, key, method, parameters, cancel)))
            Done(key, cancel);
    }

    /// <summary>
    /// The requests in the order they came (A6 review M1): each runs to its answer before the next starts, except one that
    /// <see cref="RunsLong"/>, which is started here (so everything before it has run) and then left to finish on its own.
    /// </summary>
    private async Task AnswerInTurnAsync()
    {
        await Task.Yield();
        await foreach (var request in _ordered.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var running = RunAsync(request);
            if (RunsLong?.Invoke(request.Method) != true)
                await running.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Incoming request)
    {
        try
        {
            request.Cancel.Token.ThrowIfCancellationRequested();
            var result = await _onRequest!(request.Method, request.Parameters, request.Cancel.Token).ConfigureAwait(false);
            Reply(request.Id, request.Method, result, null);
        }
        catch (Exception exception)
        {
            Reply(request.Id, request.Method, null, exception);
        }
        finally
        {
            Done(request.Key, request.Cancel);
        }
    }

    private void Done(string key, CancellationTokenSource cancel)
    {
        _incoming.TryRemove(KeyValuePair.Create(key, cancel));
        cancel.Dispose();
        Interlocked.Decrement(ref _running);
    }

    private void Reply(JsonNode id, string method, JsonNode? result, Exception? exception)
    {
        if (IsClosed)
            return;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (exception is null)
        {
            message["result"] = result;
        }
        else
        {
            var code = CodeOf(exception);
            // A6 review L3: an exception nothing expected may carry paths or internals; the other end hears only that the
            // request failed, and this end's log hears the rest.
            var words = code == AttachedProtocol.Errors.Internal ? "The request failed; the attached Corvid's log says why." : exception.Message;
            if (code == AttachedProtocol.Errors.Internal)
                OnUnexpected?.Invoke(method, exception);
            message["error"] = new JsonObject { ["code"] = code, ["message"] = words };
        }
        Enqueue(message);
        if (exception is JsonRpcException { Fatal: true })
            _ = CloseAsync(exception.Message).AsTask();
    }

    /// <summary>The code a request's exception answers with; the other end raises the matching exception (<see cref="Raise"/>).</summary>
    public static int CodeOf(Exception exception) => exception switch
    {
        JsonRpcException rpc => rpc.Code,
        OperationCanceledException => AttachedProtocol.Errors.RequestCancelled,
        KeyNotFoundException => AttachedProtocol.Errors.NotFound,
        ServiceUnavailableException => AttachedProtocol.Errors.Unavailable,
        ArgumentException or InvalidOperationException or NotSupportedException or FormatException => AttachedProtocol.Errors.Refused,
        _ => AttachedProtocol.Errors.Internal,
    };

    /// <summary>
    /// The exception the seam documents for an error the server answered with, its text cleaned of hidden characters (it
    /// arrived over a socket): a missing session, a refusal with its reason, an unavailable area.
    /// </summary>
    public static Exception Raise(JsonRpcException error)
    {
        var text = SafeText.Clean(error.Message);
        return error.Code switch
        {
            AttachedProtocol.Errors.NotFound => new KeyNotFoundException(text),
            AttachedProtocol.Errors.Unavailable => new ServiceUnavailableException(text),
            AttachedProtocol.Errors.Refused or AttachedProtocol.Errors.Busy or AttachedProtocol.Errors.InvalidParams => new InvalidOperationException(text),
            AttachedProtocol.Errors.Unauthorized => new UnauthorizedAccessException(text),
            _ => new IOException(text),
        };
    }
}
