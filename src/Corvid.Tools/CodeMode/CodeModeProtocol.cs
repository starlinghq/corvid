using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Corvid.Tools.CodeMode;

/// <summary>A7: the host-side limits of one code-mode run, enforced by <see cref="CodeModeSession"/> whatever the script does.</summary>
public sealed record CodeModeLimits
{
    public static CodeModeLimits Default { get; } = new();

    /// <summary>Tool calls the run may make (Hermes keeps 50); later requests get <see cref="CodeModeErrors.CallLimit"/>.</summary>
    public int MaxCalls { get; init; } = 50;

    /// <summary>Bytes in one line from the script (the hello or a request), newline excluded; a longer line is refused unread.</summary>
    public int MaxRequestBytes { get; init; } = 256 * 1024;

    /// <summary>Bytes in one response line; a longer result is replaced by <see cref="CodeModeErrors.TooLarge"/> (the call ran).</summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>Response bytes over the whole run; once reached, requests get <see cref="CodeModeErrors.OutputLimit"/>.</summary>
    public long MaxTotalResponseBytes { get; init; } = 8L * 1024 * 1024;
}

/// <summary>A7: the <c>error.code</c> values of the wire format.</summary>
public static class CodeModeErrors
{
    /// <summary>The hello's token was missing or wrong; the connection is closed.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The line was not a request: not JSON, no id or tool, arguments not an object, the hello's version unknown.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>The tool is not one the run may call (the descriptor lists them all). Nothing ran.</summary>
    public const string UnknownTool = "unknown_tool";

    /// <summary><see cref="CodeModeLimits.MaxCalls"/> reached. Nothing ran.</summary>
    public const string CallLimit = "call_limit";

    /// <summary>A request over <see cref="CodeModeLimits.MaxRequestBytes"/> (nothing ran), or a result over <see cref="CodeModeLimits.MaxResponseBytes"/> (the call ran).</summary>
    public const string TooLarge = "too_large";

    /// <summary><see cref="CodeModeLimits.MaxTotalResponseBytes"/> reached. Nothing ran.</summary>
    public const string OutputLimit = "output_limit";

    /// <summary>The call was refused, denied, not run or failed in the turn; <c>error.detail</c> is the result the log holds.</summary>
    public const string ToolError = "tool_error";

    /// <summary>The call was cancelled (the run is ending).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The run's parent call has finished; no more calls are taken and the connection is closed.</summary>
    public const string Closed = "closed";
}

/// <summary>A7: constants of the code-mode wire format (docs/architecture.md, "Code mode wire format (A7)").</summary>
public static class CodeModeProtocol
{
    /// <summary>The protocol version a hello names as <c>"v"</c>.</summary>
    public const int Version = 1;

    /// <summary>The longest request id accepted (a string id; a number id is an integer).</summary>
    public const int MaxIdChars = 128;

    // Compact, and escaping only what JSON needs: tool output reaches a script, not a web page.
    internal static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>A new per-run token: 32 random bytes, base64url (43 characters). Valid for <c>SandboxTools</c>.</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// A7: the host side of one code-mode run's tools socket. For each connection the script opens, <see cref="ServeAsync"/>
/// reads newline-delimited JSON: first the hello with the run's token, then requests, one at a time, each answered before
/// the next is read. Every tool call goes to the <see cref="ToolInvoker"/> given (the parent call's
/// <see cref="ToolCallContext.InvokeToolAsync"/>), never to a registry, so it takes the turn's own path. The limits hold
/// across all of the run's connections. Anything the script sends is untrusted; nothing it sends is logged here.
/// </summary>
public sealed class CodeModeSession
{
    private readonly byte[] _token;
    private readonly HashSet<string> _tools;
    private readonly ToolInvoker _invoke;
    private int _calls;
    private long _responseBytes;

    /// <param name="token">The run's token, as written to the sandbox's token file.</param>
    /// <param name="tools">What the script may call: <see cref="CodeModeCatalog.Describe"/>'s result.</param>
    /// <param name="invoke">The parent call's invoker (<see cref="ToolCallContext.InvokeToolAsync"/>).</param>
    public CodeModeSession(string token, IEnumerable<CodeModeTool> tools, ToolInvoker invoke, CodeModeLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(invoke);
        _token = Encoding.UTF8.GetBytes(token);
        _tools = new HashSet<string>(tools.Select(tool => tool.Name), StringComparer.Ordinal);
        _invoke = invoke;
        Limits = limits ?? CodeModeLimits.Default;
    }

    public CodeModeLimits Limits { get; }

    /// <summary>Tool calls made so far (requests that reached the invoker).</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Response bytes written so far, over every connection.</summary>
    public long ResponseBytes => Interlocked.Read(ref _responseBytes);

    /// <summary>A connection's whole exchange over one duplex stream (a connected Unix socket's).</summary>
    public Task ServeAsync(Stream stream, CancellationToken cancellationToken = default) => ServeAsync(stream, stream, cancellationToken);

    /// <summary>
    /// A connection's whole exchange. Returns when the script closes its side, after a refused hello, or when the parent
    /// call has finished; throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task ServeAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var reader = new LineReader(input, Limits.MaxRequestBytes);
        var (hello, helloTooLong) = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (hello is null && !helloTooLong)
            return;
        if (Hello(hello, helloTooLong) is { } refusal)
        {
            await WriteAsync(output, refusal, count: false, cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteAsync(output, Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("v", CodeModeProtocol.Version);
            writer.WriteStartObject("limits");
            writer.WriteNumber("calls", Limits.MaxCalls);
            writer.WriteNumber("request_bytes", Limits.MaxRequestBytes);
            writer.WriteNumber("response_bytes", Limits.MaxResponseBytes);
            writer.WriteNumber("total_response_bytes", Limits.MaxTotalResponseBytes);
            writer.WriteEndObject();
        }), count: false, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var (line, tooLong) = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (line is null && !tooLong)
                return;
            var (response, close) = await AnswerAsync(line, tooLong, cancellationToken).ConfigureAwait(false);
            await WriteAsync(output, response, count: true, cancellationToken).ConfigureAwait(false);
            if (close)
                return;
        }
    }

    private byte[]? Hello(byte[]? line, bool tooLong)
    {
        if (tooLong || line is null)
            return Error(null, CodeModeErrors.Unauthorized, "the first line must be the hello with the run's token");
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token.GetString()!), _token))
                return Error(null, CodeModeErrors.Unauthorized, "the hello's token is missing or wrong");
            if (!root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var v) || v != CodeModeProtocol.Version)
                return Error(null, CodeModeErrors.BadRequest, $"the hello must name protocol version {CodeModeProtocol.Version} as \"v\"");
            return null;
        }
        catch (JsonException)
        {
            return Error(null, CodeModeErrors.Unauthorized, "the first line must be the hello with the run's token");
        }
    }

    private async Task<(byte[] Response, bool Close)> AnswerAsync(byte[]? line, bool tooLong, CancellationToken cancellationToken)
    {
        if (tooLong)
            return (Error(null, CodeModeErrors.TooLarge, $"a request may be at most {Limits.MaxRequestBytes} bytes; nothing ran"), false);
        string tool;
        JsonElement id, arguments;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (Error(null, CodeModeErrors.BadRequest, "a request is a JSON object: {\"id\", \"tool\", \"args\"}"), false);
            if (!root.TryGetProperty("id", out id) || !ValidId(id))
                return (Error(null, CodeModeErrors.BadRequest, $"a request needs an \"id\": a string of at most {CodeModeProtocol.MaxIdChars} characters or an integer"), false);
            id = id.Clone();
            if (!root.TryGetProperty("tool", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 } named)
                return (Error(id, CodeModeErrors.BadRequest, "a request needs a \"tool\" name"), false);
            tool = named;
            if (!root.TryGetProperty("args", out arguments) || arguments.ValueKind == JsonValueKind.Null)
                arguments = EmptyObject();
            else if (arguments.ValueKind != JsonValueKind.Object)
                return (Error(id, CodeModeErrors.BadRequest, "\"args\" must be a JSON object"), false);
            else
                arguments = arguments.Clone();
        }
        catch (JsonException)
        {
            return (Error(null, CodeModeErrors.BadRequest, "the line is not JSON"), false);
        }

        if (!_tools.Contains(tool))
            return (Error(id, CodeModeErrors.UnknownTool, $"'{tool}' is not one of the tools this run may call; nothing ran"), false);
        if (ResponseBytes >= Limits.MaxTotalResponseBytes)
            return (Error(id, CodeModeErrors.OutputLimit, $"the run's results reached {Limits.MaxTotalResponseBytes} bytes; nothing ran"), false);
        if (Interlocked.Increment(ref _calls) > Limits.MaxCalls)
        {
            Interlocked.Decrement(ref _calls);
            return (Error(id, CodeModeErrors.CallLimit, $"the run may make at most {Limits.MaxCalls} tool calls; nothing ran"), false);
        }

        NestedToolResult result;
        try
        {
            result = await _invoke(tool, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Error(id, CodeModeErrors.Cancelled, "the call was cancelled"), false);
        }
        catch (InvalidOperationException)
        {
            return (Error(id, CodeModeErrors.Closed, "the run has finished; no more calls are taken"), true);
        }

        var response = Write(writer =>
        {
            WriteId(writer, id);
            writer.WriteBoolean("ok", !result.IsError);
            if (result.IsError)
            {
                writer.WriteStartObject("error");
                writer.WriteString("code", CodeModeErrors.ToolError);
                writer.WriteString("message", Message(result.Result));
                writer.WritePropertyName("detail");
                result.Result.WriteTo(writer);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                result.Result.WriteTo(writer);
            }
            writer.WriteBoolean("tainted", result.Tainted);
        });
        if (response.Length + 1 > Limits.MaxResponseBytes)
            response = Error(id, CodeModeErrors.TooLarge,
                $"the result was {response.Length} bytes, over the limit of {Limits.MaxResponseBytes}; the call ran", result.Tainted);
        return (response, false);
    }

    private async Task WriteAsync(Stream output, byte[] response, bool count, CancellationToken cancellationToken)
    {
        if (count)
            Interlocked.Add(ref _responseBytes, response.Length + 1);
        await output.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString()!.Length <= CodeModeProtocol.MaxIdChars,
        JsonValueKind.Number => id.TryGetInt64(out _),
        _ => false,
    };

    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        writer.WritePropertyName("id");
        if (id is { } value)
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
    }

    /// <summary>The kernel's error object as one line: <c>kind: detail; detail</c>.</summary>
    private static string Message(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var kind) && kind.ValueKind == JsonValueKind.String)
        {
            var details = result.TryGetProperty("details", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : [];
            return details.Count == 0 ? kind.GetString()! : $"{kind.GetString()}: {string.Join("; ", details)}";
        }
        return "the tool failed";
    }

    private static byte[] Error(JsonElement? id, string code, string message, bool? tainted = null) => Write(writer =>
    {
        WriteId(writer, id);
        writer.WriteBoolean("ok", false);
        writer.WriteStartObject("error");
        writer.WriteString("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        if (tainted is { } outside)
            writer.WriteBoolean("tainted", outside);
    });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CodeModeProtocol.WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}"u8.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Lines of at most <c>max</c> bytes; a longer one is read to its end, discarded and reported as too long.</summary>
    private sealed class LineReader(Stream stream, int max)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _start;
        private int _end;
        private bool _eof;

        /// <returns>The line without its newline (and a trailing CR); null with <c>TooLong</c> false at the end of the stream.</returns>
        public async ValueTask<(byte[]? Line, bool TooLong)> ReadAsync(CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            var tooLong = false;
            var any = false;
            while (true)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                var stop = newline >= 0 ? newline : _end;
                var count = stop - _start;
                any |= count > 0;
                if (!tooLong && line.WrittenCount + count > max)
                    tooLong = true;
                if (!tooLong)
                    line.Write(_buffer.AsSpan(_start, count));
                if (newline >= 0)
                {
                    _start = newline + 1;
                    return tooLong ? (null, true) : (Trim(line.WrittenSpan), false);
                }
                if (_eof)
                {
                    _start = _end;
                    // A last line without its newline still counts.
                    return tooLong ? (null, true) : any ? (Trim(line.WrittenSpan), false) : (null, false);
                }
                _start = 0;
                _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _eof = _end == 0;
            }
        }

        private static byte[] Trim(ReadOnlySpan<byte> line) => (line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line).ToArray();
    }
}
