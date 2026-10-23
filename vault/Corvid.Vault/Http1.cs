using System.Globalization;
using System.Text;
using Corvid.Vault.Contracts;

namespace Corvid.Vault;

/// <summary>An HTTP/1.1 message Vault refuses: the status and <c>X-Corvid-Vault-Error</c> code to answer with.</summary>
internal sealed class Http1Exception : Exception
{
    public Http1Exception(int status, string code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    public Http1Exception()
        : this(400, VaultErrorCodes.BadRequest, "Bad request.")
    {
    }

    public Http1Exception(string message)
        : this(400, VaultErrorCodes.BadRequest, message)
    {
    }

    public Http1Exception(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = 400;
        Code = VaultErrorCodes.BadRequest;
    }

    public int Status { get; }

    public string Code { get; }
}

/// <summary>A request line and header block, as sent.</summary>
internal sealed record Http1Head(string Method, string Target, IReadOnlyList<WireHeader> Headers)
{
    public IEnumerable<string> Values(string name) =>
        Headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
}

/// <summary>What the sandbox proxy needs from an inner request once its head is accepted.</summary>
internal readonly record struct Http1Framing(long ContentLength, bool ExpectContinue, bool Close);

/// <summary>
/// A minimal, strict HTTP/1.1 reader and writer for the sandbox proxy (V-003a). It is the proxy's attack surface, so it
/// accepts one shape only: CRLF line endings (a bare CR or LF is refused), a header block of at most
/// <see cref="MaxHeaderBytes"/> read within <see cref="HeaderTimeout"/>, no obs-fold, bodies framed by a single
/// Content-Length and never by Transfer-Encoding (refused, and refused harder alongside Content-Length, so no request
/// can be framed two ways), and no Upgrade. Requests are read one at a time; nothing is pipelined past a refusal.
/// </summary>
internal sealed class Http1Connection(Stream stream)
{
    public const int MaxHeaderBytes = 64 * 1024;

    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(100);

    private readonly byte[] _buffer = new byte[MaxHeaderBytes + 4096];
    private int _start;
    private int _end;

    /// <summary>Bytes read from the stream but not consumed yet.</summary>
    public bool HasBufferedBytes => _end > _start;

    /// <summary>
    /// Reads one request head. Null when the peer closes cleanly before sending a byte (the end of a keep-alive
    /// connection). Throws <see cref="Http1Exception"/> for anything malformed or too large, and
    /// <see cref="OperationCanceledException"/> when <see cref="HeaderTimeout"/> or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<Http1Head?> ReadHeadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HeaderTimeout);
        int scanned = 0;
        while (true)
        {
            // Tolerate CRLFs between messages (RFC 9112 §2.2), nothing else.
            while (_end - _start >= 2 && _buffer[_start] == '\r' && _buffer[_start + 1] == '\n')
            {
                _start += 2;
                scanned = 0;
            }

            int end = FindHeaderEnd(_buffer.AsSpan(_start, _end - _start), ref scanned);
            if (end >= 0)
            {
                Http1Head head = ParseHead(_buffer.AsSpan(_start, end));
                _start += end + 4;
                return head;
            }

            if (_end - _start >= MaxHeaderBytes)
            {
                throw new Http1Exception(431, VaultErrorCodes.BadRequest, $"The header block exceeds {MaxHeaderBytes} bytes.");
            }

            Compact();
            int read = await stream.ReadAsync(_buffer.AsMemory(_end, Math.Min(_buffer.Length - _end, MaxHeaderBytes + 4 - (_end - _start))), timeout.Token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (_end == _start)
                {
                    return null;
                }

                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "The connection closed inside a request head.");
            }

            _end += read;
        }
    }

    /// <summary>Reads exactly <paramref name="length"/> body bytes, buffered ones first.</summary>
    public async Task<byte[]> ReadBodyAsync(long length, CancellationToken cancellationToken)
    {
        byte[] body = new byte[length];
        int fromBuffer = (int)Math.Min(length, _end - _start);
        _buffer.AsSpan(_start, fromBuffer).CopyTo(body);
        _start += fromBuffer;
        if (fromBuffer < length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BodyTimeout);
            try
            {
                await stream.ReadExactlyAsync(body.AsMemory(fromBuffer), timeout.Token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "The connection closed inside a request body.");
            }
        }

        return body;
    }

    /// <summary>
    /// Checks an inner request's head (inside a tunnel to <paramref name="connectHost"/>) and returns its framing.
    /// The method must not be CONNECT, the target must be origin-form, Host must name the tunnel's host, and the body must
    /// be framed by at most one Content-Length no larger than <paramref name="maxBody"/>.
    /// </summary>
    public static Http1Framing Check(Http1Head head, string connectHost, long maxBody)
    {
        if (head.Method == "CONNECT")
        {
            throw new Http1Exception(405, VaultErrorCodes.NotProxyable, "CONNECT inside a tunnel is not proxied.");
        }

        if (!head.Target.StartsWith('/'))
        {
            throw new Http1Exception(400, VaultErrorCodes.BadRequest, "The request target must be origin-form (a path starting with '/').");
        }

        if (head.Values("Upgrade").Any())
        {
            throw new Http1Exception(400, VaultErrorCodes.NotProxyable, "Protocol upgrades (websockets) are not proxied.");
        }

        List<string> te = head.Values("Transfer-Encoding").ToList();
        List<string> cl = head.Values("Content-Length").ToList();
        if (te.Count > 0 && cl.Count > 0)
        {
            throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Content-Length and Transfer-Encoding together are refused.");
        }

        if (te.Count > 0)
        {
            throw new Http1Exception(411, VaultErrorCodes.BadRequest, "Chunked or encoded request bodies are not supported; send Content-Length.");
        }

        if (cl.Count > 1)
        {
            throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Content-Length appears more than once.");
        }

        long length = 0;
        if (cl.Count == 1)
        {
            string value = cl[0];
            if (value.Length is 0 or > 18 || !value.All(char.IsAsciiDigit))
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Content-Length is not a decimal number.");
            }

            length = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (length > maxBody)
            {
                throw new Http1Exception(413, VaultErrorCodes.BadRequest, $"The request body exceeds {maxBody} bytes.");
            }
        }

        List<string> hosts = head.Values("Host").ToList();
        if (hosts.Count != 1 || !HostMatches(hosts[0], connectHost))
        {
            throw new Http1Exception(400, VaultErrorCodes.BadRequest, "The Host header must name the tunnel's host, once.");
        }

        bool expectContinue = false;
        foreach (string expect in head.Values("Expect"))
        {
            if (!string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                throw new Http1Exception(417, VaultErrorCodes.BadRequest, "Only 'Expect: 100-continue' is supported.");
            }

            expectContinue = true;
        }

        bool close = head.Values("Connection")
            .SelectMany(v => v.Split(','))
            .Any(t => string.Equals(t.Trim(), "close", StringComparison.OrdinalIgnoreCase));
        return new Http1Framing(length, expectContinue && length > 0, close);
    }

    private static bool HostMatches(string host, string connectHost) =>
        string.Equals(host, connectHost, StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, connectHost + ":443", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a header block (without its final CRLF CRLF): <c>METHOD SP target SP HTTP/1.1</c>, then <c>name: value</c>
    /// lines. Values are read as Latin-1 so every byte survives; names must be tokens.
    /// </summary>
    public static Http1Head ParseHead(ReadOnlySpan<byte> block)
    {
        string text = Encoding.Latin1.GetString(block);
        string[] lines = text.Split("\r\n");
        foreach (string line in lines)
        {
            if (line.Contains('\r', StringComparison.Ordinal) || line.Contains('\n', StringComparison.Ordinal))
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "A bare CR or LF is refused.");
            }
        }

        string[] parts = lines[0].Split(' ');
        if (parts.Length != 3 || !IsToken(parts[0]) || parts[1].Length == 0 || !parts[1].All(c => c is > ' ' and < '\x7f'))
        {
            throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Malformed request line.");
        }

        if (parts[2] != "HTTP/1.1")
        {
            throw new Http1Exception(505, VaultErrorCodes.BadRequest, "Only HTTP/1.1 is supported.");
        }

        var headers = new List<WireHeader>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Folded header lines (obs-fold) are refused.");
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !IsToken(line[..colon]))
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Malformed header line.");
            }

            string value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(c => c is < ' ' and not '\t' or '\x7f'))
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Control characters in a header value are refused.");
            }

            headers.Add(new WireHeader(line[..colon], value));
        }

        return new Http1Head(parts[0], parts[1], headers);
    }

    /// <summary>Writes a response with a fresh Content-Length (omitted for 1xx, 204 and 304).</summary>
    public async Task WriteResponseAsync(
        int status, string? reason, IEnumerable<WireHeader> headers, ReadOnlyMemory<byte> body, bool close, CancellationToken cancellationToken)
    {
        bool bodyless = status is < 200 or 204 or 304;
        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(reason is { Length: > 0 } r && IsSafeValue(r) ? r : DefaultReason(status)).Append("\r\n");
        foreach (WireHeader header in headers)
        {
            // Response headers come from upstream (scrubbed); never let one split the response.
            if (IsToken(header.Name) && IsSafeValue(header.Value))
            {
                head.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        if (!bodyless)
        {
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        if (close)
        {
            head.Append("Connection: close\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (!bodyless && body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A refusal: the status, <c>X-Corvid-Vault-Error: code</c> and a one-line text body.</summary>
    public Task WriteRefusalAsync(int status, string code, string message, bool close, CancellationToken cancellationToken) =>
        WriteResponseAsync(
            status,
            null,
            [new WireHeader("X-Corvid-Vault-Error", code), new WireHeader("Content-Type", "text/plain; charset=utf-8")],
            Encoding.UTF8.GetBytes(OneLine(message) + "\n"),
            close,
            cancellationToken);

    /// <summary>The interim response to <c>Expect: 100-continue</c>.</summary>
    public async Task WriteContinueAsync(CancellationToken cancellationToken)
    {
        await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string OneLine(string message) => message.ReplaceLineEndings(" ");

    private static int FindHeaderEnd(ReadOnlySpan<byte> data, ref int scanned)
    {
        int end = data.IndexOf("\r\n\r\n"u8);
        // A bare LF can never be part of a valid head; refuse it as soon as it arrives rather than waiting for the cap.
        ReadOnlySpan<byte> check = end >= 0 ? data[..end] : data;
        for (int i = Math.Max(scanned, 0); i < check.Length; i++)
        {
            if (check[i] == '\n' && (i == 0 || check[i - 1] != '\r'))
            {
                throw new Http1Exception(400, VaultErrorCodes.BadRequest, "Lines must end in CRLF.");
            }
        }

        scanned = check.Length;
        return end;
    }

    private void Compact()
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }
    }

    private static bool IsToken(string s) => s.Length > 0 && s.All(c => c < 0x7f && (char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)));

    private static bool IsSafeValue(string s) => !s.Any(c => c is '\r' or '\n' or '\0');

    private static string DefaultReason(int status) => status switch
    {
        100 => "Continue",
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        405 => "Method Not Allowed",
        411 => "Length Required",
        413 => "Content Too Large",
        417 => "Expectation Failed",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        502 => "Bad Gateway",
        505 => "HTTP Version Not Supported",
        _ => "Status",
    };
}
