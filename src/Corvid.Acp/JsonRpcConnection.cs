using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Corvid.Acp;

/// <summary>A request's result, and what to send once the response is out (a notification the client can only use after it).</summary>
internal readonly record struct JsonRpcResult(JsonElement Result, Action? AfterResponse = null);

/// <summary>What a connection hands its owner: requests to answer and notifications to act on.</summary>
internal interface IJsonRpcHandler
{
    /// <summary>Answers a request. Throw <see cref="AcpException"/> for a JSON-RPC error; anything else is an internal error.</summary>
    Task<JsonRpcResult> HandleRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken);

    void HandleNotification(string method, JsonElement parameters);
}

/// <summary>
/// JSON-RPC 2.0 over ACP's stdio transport: one UTF-8 message per line, never an embedded newline, and nothing else on the
/// output. Requests run concurrently (a prompt runs for minutes while its cancel arrives), every message goes out through
/// one writer in the order it was queued, and the agent's own requests to the client (permission prompts) are matched to
/// their responses by id.
/// </summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    // A message larger than this is refused rather than buffered without bound.
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly List<Task> _handlers = [];
    private readonly Task _writer;
    private long _nextId;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Cancelled when the client closes its end.</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>Reads messages until the input ends, dispatching each to <paramref name="handler"/>.</summary>
    public async Task RunAsync(IJsonRpcHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var reader = new LineReader(_input, MaxMessageBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                Dispatch(handler, line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _closed.CancelAsync().ConfigureAwait(false);
            foreach (var pending in _pending.Values)
                pending.TrySetCanceled(_closed.Token);
            Task[] handlers;
            lock (_handlers)
                handlers = [.. _handlers];
            await Task.WhenAll(handlers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Notify<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        Queue(writer =>
        {
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, type);
        });

    /// <summary>Sends a request to the client and waits for its result.</summary>
    /// <exception cref="AcpException">The client answered with an error.</exception>
    public async Task<JsonElement> RequestAsync<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            Queue(writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, type);
            });
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            return await answer.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        await _writer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _closed.CancelAsync().ConfigureAwait(false);
        _closed.Dispose();
    }

    private void Dispatch(IJsonRpcHandler handler, byte[] line)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            Error(null, AcpErrors.ParseError, "The message is not JSON.");
            return;
        }
        if (message.ValueKind != JsonValueKind.Object)
        {
            Error(null, AcpErrors.InvalidRequest, "A message must be a JSON object.");
            return;
        }

        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        if (!message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            // A response to one of the agent's own requests.
            if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var pending))
            {
                if (message.TryGetProperty("error", out var error))
                    pending.TrySetException(new AcpException(error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : AcpErrors.InternalError,
                        error.TryGetProperty("message", out var text) ? text.GetString() ?? "" : ""));
                else
                    pending.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
            }
            else if (!hasId)
                Error(null, AcpErrors.InvalidRequest, "A message needs a method or an id.");
            return;
        }

        var method = methodElement.GetString()!;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        if (!hasId)
        {
            if (method == AcpMethods.CancelRequest)
            {
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out var requestId)
                    && _running.TryGetValue(requestId.GetRawText(), out var running))
                    running.Cancel();
                return;
            }
            try
            {
                handler.HandleNotification(method, parameters);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A notification has nobody to answer; the client's next request will show what state it left.
            }
            return;
        }

        var key = id.GetRawText();
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _running[key] = cancel;
        var task = Task.Run(async () =>
        {
            try
            {
                var result = await handler.HandleRequestAsync(method, parameters, cancel.Token).ConfigureAwait(false);
                Queue(writer =>
                {
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WritePropertyName("result");
                    result.Result.WriteTo(writer);
                });
                result.AfterResponse?.Invoke();
            }
            catch (AcpException exception)
            {
                Error(id, exception.Code, exception.Message);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Error(id, AcpErrors.RequestCancelled, "The request was cancelled.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Error(id, AcpErrors.InternalError, exception.Message);
            }
            finally
            {
                _running.TryRemove(key, out _);
                cancel.Dispose();
            }
        });
        lock (_handlers)
        {
            _handlers.RemoveAll(item => item.IsCompleted);
            _handlers.Add(task);
        }
    }

    private void Error(JsonElement? id, int code, string text) =>
        Queue(writer =>
        {
            writer.WritePropertyName("id");
            if (id is { } value)
                value.WriteTo(writer);
            else
                writer.WriteNullValue();
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, new JsonRpcError(code, text), AcpJson.Default.JsonRpcError);
        });

    private void Queue(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        // Not indented, and strings escape control characters, so a message never holds a raw newline.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }
        _outgoing.Writer.TryWrite(buffer.WrittenSpan.ToArray());
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _output.WriteAsync(message).ConfigureAwait(false);
            await _output.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads newline-delimited messages as bytes, refusing a line longer than the limit.</summary>
    private sealed class LineReader(Stream stream, int limit)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    if (_end == 0)
                        return line.WrittenCount > 0 ? Trim(line.WrittenSpan) : null;
                }
                var span = _buffer.AsSpan(_start, _end - _start);
                var newline = span.IndexOf((byte)'\n');
                var take = newline < 0 ? span : span[..newline];
                if (line.WrittenCount + take.Length > limit)
                    throw new InvalidDataException($"A message is longer than {limit} bytes.");
                line.Write(take);
                _start += newline < 0 ? take.Length : newline + 1;
                if (newline >= 0)
                    return Trim(line.WrittenSpan);
            }
        }

        // A client on Windows may end lines with CRLF.
        private static byte[] Trim(ReadOnlySpan<byte> line) => (line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line).ToArray();
    }
}
