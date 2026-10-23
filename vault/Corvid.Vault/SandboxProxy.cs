using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Corvid.Vault.Contracts;

namespace Corvid.Vault;

/// <summary>
/// One open sandbox proxy: the opener's identity and flags, its socket, its CA and its live connections. Every request
/// through it is made as the opener's session and uid, with its toolset and taint, and never pre-approved.
/// </summary>
internal sealed class SandboxSession
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public required uint Uid { get; init; }

    public string? Toolset { get; init; }

    public bool Tainted { get; init; }

    public bool Network { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string SocketPath { get; init; }

    public required SandboxCertificates Certificates { get; init; }

    public required Socket Listener { get; init; }

    public CancellationTokenSource Stop { get; } = new();

    public ConcurrentDictionary<Socket, byte> Connections { get; } = new();

    /// <summary>Connections counted against <see cref="PolicyDocument.MaxSandboxConnections"/>; changed with Interlocked only.</summary>
    internal int Live;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>
/// Sandbox proxies (V-003a, vault.md "Implementation status"). Each is a Unix socket of its own, at
/// <c>&lt;dir of vault.sock&gt;/sandbox/&lt;id&gt;.sock</c>, speaking HTTP CONNECT to port 443 of a DNS name. Vault
/// terminates TLS with a leaf minted by the sandbox's CA for exactly that name, reads strict HTTP/1.1 requests out of
/// the tunnel and sends each through the same forward pipeline as Corvid's own requests.
/// </summary>
internal sealed class SandboxProxy : IDisposable
{
    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int IdLength = 12;
    private const int MaxSocketPathBytes = 107;

    private readonly VaultCore _core;
    private readonly string _directory;
    private readonly uint _ownUid = Native.CurrentUid();
    private readonly ConcurrentDictionary<string, SandboxSession> _sandboxes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _live;

    public SandboxProxy(VaultCore core, string directory)
    {
        _core = core;
        _directory = directory;
    }

    public int Count => _sandboxes.Count;

    /// <summary>Live connections across every sandbox, counted against <see cref="PolicyDocument.MaxSandboxConnectionsTotal"/>.</summary>
    public int LiveConnections => Volatile.Read(ref _live);

    public SandboxSession? Find(string id) => _sandboxes.GetValueOrDefault(id);

    public VaultResponse Open(SandboxOpenRequest request, PeerInfo peer)
    {
        PolicyDocument policy = _core.Policy.Current;
        if (request.LifetimeMinutes is <= 0)
        {
            return Refuse("sandbox_open", VaultErrorCodes.BadRequest, "lifetime_minutes must be positive.", request.SessionId, peer, null);
        }

        int minutes = Math.Min(request.LifetimeMinutes ?? int.MaxValue, policy.SandboxLifetimeMinutes);
        lock (_gate)
        {
            Sweep();
            if (_sandboxes.Count >= policy.MaxSandboxes)
            {
                return Refuse("sandbox_open", VaultErrorCodes.SandboxLimit, $"{policy.MaxSandboxes} sandboxes are already open.", request.SessionId, peer, null);
            }

            string id = RandomNumberGenerator.GetString(IdAlphabet, IdLength);
            string path = Path.Combine(_directory, id + ".sock");
            int pathBytes = Encoding.UTF8.GetByteCount(path);
            if (pathBytes > MaxSocketPathBytes)
            {
                return Refuse(
                    "sandbox_open", VaultErrorCodes.BadRequest,
                    $"The sandbox socket path {path} is {pathBytes} bytes; a Unix socket path holds at most {MaxSocketPathBytes}. Use a shorter --socket directory.",
                    request.SessionId, peer, null);
            }

            DateTimeOffset now = _core.Time.GetUtcNow();
            DateTimeOffset expires = now.AddMinutes(minutes);
            var certificates = new SandboxCertificates(id, now, expires);
            Socket listener;
            try
            {
                listener = VaultServer.Listen(path, 16);
            }
            catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
            {
                certificates.Dispose();
                return Refuse("sandbox_open", VaultErrorCodes.BadRequest, $"Could not bind the sandbox socket {path}: {e.Message}", request.SessionId, peer, null);
            }

            var sandbox = new SandboxSession
            {
                Id = id, SessionId = request.SessionId, Uid = peer.Uid, Toolset = request.Toolset, Tainted = request.Tainted,
                Network = request.Network, ExpiresAt = expires, SocketPath = path, Certificates = certificates, Listener = listener,
            };
            _sandboxes[id] = sandbox;
            _ = Task.Run(() => AcceptLoopAsync(sandbox));
            _core.Audit.Write(new AuditEvent
            {
                Time = now, Op = "sandbox_open", Decision = "allowed", Session = request.SessionId, Pid = peer.Pid, Uid = peer.Uid, Sandbox = id,
            });
            return new SandboxOpenResponse { SandboxId = id, ProxySocketPath = path, CaCertificatePem = certificates.CaCertificatePem, ExpiresAt = expires };
        }
    }

    public VaultResponse Close(SandboxCloseRequest request, PeerInfo peer)
    {
        if (!_sandboxes.TryGetValue(request.SandboxId, out SandboxSession? sandbox) || sandbox.SessionId != request.SessionId || sandbox.Uid != peer.Uid)
        {
            return Refuse("sandbox_close", VaultErrorCodes.UnknownSandbox, "No such sandbox for this session.", request.SessionId, peer, request.SandboxId);
        }

        Shutdown(sandbox, "closed");
        return new SandboxClosedResponse { SandboxId = sandbox.Id };
    }

    /// <summary>Closes every sandbox whose lifetime has run out.</summary>
    public void Sweep()
    {
        DateTimeOffset now = _core.Time.GetUtcNow();
        foreach (SandboxSession sandbox in _sandboxes.Values)
        {
            if (sandbox.IsExpired(now))
            {
                Shutdown(sandbox, "expired");
            }
        }
    }

    /// <summary>
    /// Ends a sandbox: no new connections, every live one closed, the socket file removed, the CA key and leaves
    /// disposed. Idempotent; only the first call audits.
    /// </summary>
    private void Shutdown(SandboxSession sandbox, string reason)
    {
        if (!_sandboxes.TryRemove(new KeyValuePair<string, SandboxSession>(sandbox.Id, sandbox)))
        {
            return;
        }

        sandbox.Stop.Cancel();
        sandbox.Listener.Dispose();
        foreach (Socket connection in sandbox.Connections.Keys)
        {
            try
            {
                connection.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Already gone.
            }

            connection.Dispose();
        }

        try
        {
            File.Delete(sandbox.SocketPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The directory went away (tests) or is not ours to clean any more.
        }

        sandbox.Certificates.Dispose();
        _core.Audit.Write(new AuditEvent
        {
            Time = _core.Time.GetUtcNow(), Op = "sandbox_close", Decision = "allowed", Code = reason, Session = sandbox.SessionId, Uid = sandbox.Uid,
            Sandbox = sandbox.Id,
        });
    }

    private async Task AcceptLoopAsync(SandboxSession sandbox)
    {
        CancellationToken stop = sandbox.Stop.Token;
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await sandbox.Listener.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (sandbox.IsExpired(_core.Time.GetUtcNow()))
            {
                client.Dispose();
                Shutdown(sandbox, "expired");
                return;
            }

            // Counted before anything is read, so a flood of idle connections is capped like a flood of large bodies.
            PolicyDocument policy = _core.Policy.Current;
            int perSandbox = Interlocked.Increment(ref sandbox.Live);
            int total = Interlocked.Increment(ref _live);
            if (perSandbox > policy.MaxSandboxConnections || total > policy.MaxSandboxConnectionsTotal)
            {
                Release(sandbox);
                string message = perSandbox > policy.MaxSandboxConnections
                    ? $"This sandbox already has {policy.MaxSandboxConnections} connections open."
                    : $"Sandboxes already have {policy.MaxSandboxConnectionsTotal} connections open.";
                _ = Task.Run(() => RefuseBusyAsync(sandbox, client, message, stop));
                continue;
            }

            sandbox.Connections[client] = 0;
            if (stop.IsCancellationRequested)
            {
                // Closed between accept and registration: Shutdown may have missed this one.
                sandbox.Connections.TryRemove(client, out _);
                Release(sandbox);
                client.Dispose();
                return;
            }

            _ = Task.Run(() => ServeAsync(sandbox, client));
        }
    }

    private async Task ServeAsync(SandboxSession sandbox, Socket client)
    {
        CancellationToken stop = sandbox.Stop.Token;
        try
        {
            using (client)
            {
                if (VaultServer.CheckPeer(_core, client, _ownUid, sandbox.Id) is not { } connecting)
                {
                    return;
                }

                // The connection acts for the opener: its session and uid are what placeholders were minted for.
                var peer = new PeerInfo(connecting.Pid, sandbox.Uid);
                await using var raw = new NetworkStream(client, ownsSocket: false);
                var outer = new Http1Connection(raw);
                string? host = await ConnectAsync(sandbox, peer, outer, stop).ConfigureAwait(false);
                if (host is null)
                {
                    return;
                }

                await using var tls = new SslStream(raw, leaveInnerStreamOpen: true);
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    handshake.CancelAfter(Http1Connection.HeaderTimeout);
                    await tls.AuthenticateAsServerAsync(SelectCertificate, (sandbox, host), handshake.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (!stop.IsCancellationRequested
                    && e is AuthenticationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    // Not TLS (a raw tunnel attempt), an SNI for another host, or a client that does not trust the CA.
                    AuditConnect(sandbox, peer, "refused", "tls_handshake_failed", host);
                    return;
                }

                await ServeTunnelAsync(sandbox, peer, host, new Http1Connection(tls), stop).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // Client gone, timed out, or the sandbox closed under it.
        }
        finally
        {
            sandbox.Connections.TryRemove(client, out _);
            Release(sandbox);
        }
    }

    private void Release(SandboxSession sandbox)
    {
        Interlocked.Decrement(ref sandbox.Live);
        Interlocked.Decrement(ref _live);
    }

    /// <summary>
    /// Answers a connection over a cap with 429 <c>sandbox_limit</c> and closes it, reading nothing from it. The peer is
    /// checked first, as for any connection; the write is one small response, bounded by <see cref="Http1Connection.HeaderTimeout"/>.
    /// </summary>
    private async Task RefuseBusyAsync(SandboxSession sandbox, Socket client, string message, CancellationToken stop)
    {
        try
        {
            using (client)
            {
                if (VaultServer.CheckPeer(_core, client, _ownUid, sandbox.Id) is not { } connecting)
                {
                    return;
                }

                AuditConnect(sandbox, new PeerInfo(connecting.Pid, sandbox.Uid), "refused", VaultErrorCodes.SandboxLimit, null);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(Http1Connection.HeaderTimeout);
                await using var raw = new NetworkStream(client, ownsSocket: false);
                await new Http1Connection(raw).WriteRefusalAsync(429, VaultErrorCodes.SandboxLimit, message, close: true, toHead: false, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The client went away first.
        }
    }

    /// <summary>Reads and answers the CONNECT. Returns the tunnel's host after a 200, or null after a refusal.</summary>
    private async Task<string?> ConnectAsync(SandboxSession sandbox, PeerInfo peer, Http1Connection outer, CancellationToken stop)
    {
        Http1Head? head;
        try
        {
            head = await outer.ReadHeadAsync(stop).ConfigureAwait(false);
        }
        catch (Http1Exception e)
        {
            AuditConnect(sandbox, peer, "refused", e.Code, null);
            await outer.WriteRefusalAsync(e.Status, e.Code, e.Message, close: true, toHead: false, stop).ConfigureAwait(false);
            return null;
        }

        if (head is null)
        {
            return null;
        }

        string destination = head.Target.Length > 256 ? head.Target[..256] : head.Target;
        if (head.Method != "CONNECT")
        {
            return await RefuseConnectAsync(
                sandbox, peer, outer, 405, VaultErrorCodes.NotProxyable,
                "Only CONNECT to port 443 is proxied; plain-http and other methods are not.", $"{head.Method} {destination}", stop).ConfigureAwait(false);
        }

        if (ParseAuthority(head.Target) is not { } authority)
        {
            return await RefuseConnectAsync(sandbox, peer, outer, 400, VaultErrorCodes.BadRequest, "CONNECT needs host:443.", destination, stop).ConfigureAwait(false);
        }

        if (authority.Port != 443)
        {
            return await RefuseConnectAsync(sandbox, peer, outer, 403, VaultErrorCodes.NotProxyable, "Only port 443 is proxied.", destination, stop).ConfigureAwait(false);
        }

        if (authority.IsIpLiteral)
        {
            return await RefuseConnectAsync(sandbox, peer, outer, 403, VaultErrorCodes.NotProxyable, "IP-literal destinations are not proxied; use a DNS name.", destination, stop)
                .ConfigureAwait(false);
        }

        if (authority.Host is not { } host)
        {
            return await RefuseConnectAsync(sandbox, peer, outer, 400, VaultErrorCodes.BadRequest, "The CONNECT host is not a valid DNS name.", destination, stop)
                .ConfigureAwait(false);
        }

        if (outer.HasBufferedBytes)
        {
            return await RefuseConnectAsync(sandbox, peer, outer, 400, VaultErrorCodes.BadRequest, "Data arrived before the tunnel was established.", host, stop)
                .ConfigureAwait(false);
        }

        if (sandbox.IsExpired(_core.Time.GetUtcNow()))
        {
            Shutdown(sandbox, "expired");
            return null;
        }

        PolicyDocument policy = _core.Policy.Current;
        if (!sandbox.Network && !IsBound(policy, host))
        {
            return await RefuseConnectAsync(
                sandbox, peer, outer, 403, VaultErrorCodes.NetworkNotGranted, $"This sandbox has no network and no secret is bound to {host}.", host, stop).ConfigureAwait(false);
        }

        if (sandbox.Certificates.For(host) is null)
        {
            return await RefuseConnectAsync(
                sandbox, peer, outer, 429, VaultErrorCodes.SandboxLimit, $"This sandbox has reached {SandboxCertificates.MaxHosts} hosts.", host, stop).ConfigureAwait(false);
        }

        AuditConnect(sandbox, peer, "allowed", null, host);
        await outer.WriteResponseAsync(200, "Connection established", [], ReadOnlyMemory<byte>.Empty, close: false, toHead: false, stop).ConfigureAwait(false);
        return host;
    }

    private async Task<string?> RefuseConnectAsync(
        SandboxSession sandbox, PeerInfo peer, Http1Connection outer, int status, string code, string message, string destination, CancellationToken stop)
    {
        AuditConnect(sandbox, peer, "refused", code, destination);
        await outer.WriteRefusalAsync(status, code, message, close: true, toHead: false, stop).ConfigureAwait(false);
        return null;
    }

    private static ValueTask<SslServerAuthenticationOptions> SelectCertificate(
        SslStream stream, SslClientHelloInfo hello, object? state, CancellationToken cancellationToken)
    {
        var (sandbox, host) = ((SandboxSession, string))state!;

        // The leaf is for the CONNECT host only; an SNI naming anything else (or nothing) gets no certificate at all.
        if (!string.Equals(hello.ServerName, host, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationException("The TLS server name does not match the CONNECT host.");
        }

        SslStreamCertificateContext context = sandbox.Certificates.Existing(host)
            ?? throw new AuthenticationException("The sandbox has closed.");
        return ValueTask.FromResult(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = context,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        });
    }

    /// <summary>Sequential HTTP/1.1 requests on one TLS connection until the client closes, asks to, or the sandbox ends.</summary>
    private async Task ServeTunnelAsync(SandboxSession sandbox, PeerInfo peer, string host, Http1Connection inner, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (sandbox.IsExpired(_core.Time.GetUtcNow()))
            {
                Shutdown(sandbox, "expired");
                return;
            }

            PolicyDocument policy = _core.Policy.Current;
            Http1Head? head = null;
            Http1Framing framing;
            byte[]? body = null;
            try
            {
                head = await inner.ReadHeadAsync(stop).ConfigureAwait(false);
                if (head is null)
                {
                    return;
                }

                framing = Http1Connection.Check(head, host, policy.MaxResponseBytes);
                if (framing.ExpectContinue)
                {
                    await inner.WriteContinueAsync(stop).ConfigureAwait(false);
                }

                if (framing.ContentLength > 0)
                {
                    body = await inner.ReadBodyAsync(framing.ContentLength, stop).ConfigureAwait(false);
                }
            }
            catch (Http1Exception e)
            {
                // A malformed request leaves the stream's framing unknown, so the connection ends with the refusal.
                _core.Audit.Write(new AuditEvent
                {
                    Time = _core.Time.GetUtcNow(), Op = "sandbox_forward", Decision = "refused", Code = e.Code, Session = sandbox.SessionId,
                    Pid = peer.Pid, Uid = peer.Uid, Destination = host, Sandbox = sandbox.Id,
                });
                // A refused HEAD still gets no body, or the client would read the text as the next response.
                await inner.WriteRefusalAsync(e.Status, e.Code, e.Message, close: true, toHead: head?.Method == "HEAD", stop).ConfigureAwait(false);
                return;
            }

            if (sandbox.IsExpired(_core.Time.GetUtcNow()))
            {
                Shutdown(sandbox, "expired");
                return;
            }

            var request = new ForwardRequest
            {
                SessionId = sandbox.SessionId,
                Method = head.Method,
                // Vault builds the destination from the CONNECT host; the inner Host header was only checked against it.
                Url = "https://" + host + head.Target,
                Headers = head.Headers.Where(h => !string.Equals(h.Name, "Expect", StringComparison.OrdinalIgnoreCase)).ToList(),
                Body = body,
                Approved = false,
                Tainted = sandbox.Tainted,
                Toolset = sandbox.Toolset,
            };

            VaultResponse response;
            try
            {
                response = await _core.ForwardAsync(request, peer, sandbox, stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                response = new ErrorResponse { Code = VaultErrorCodes.BadRequest, Message = _core.CurrentScrubber.Scrub(e.Message).Text };
            }

            bool toHead = head.Method == "HEAD";
            switch (response)
            {
                case ForwardResponse forwarded:
                    await inner.WriteResponseAsync(forwarded.Status, forwarded.Reason, forwarded.Headers, forwarded.Body, framing.Close, toHead, stop)
                        .ConfigureAwait(false);
                    break;
                case ErrorResponse error:
                    await inner.WriteRefusalAsync(StatusFor(error.Code), error.Code, error.Message, framing.Close, toHead, stop).ConfigureAwait(false);
                    break;
                default:
                    await inner.WriteRefusalAsync(502, VaultErrorCodes.UpstreamError, "Unexpected response.", close: true, toHead, stop).ConfigureAwait(false);
                    return;
            }

            if (framing.Close)
            {
                return;
            }
        }
    }

    /// <summary>Policy codes are 403; upstream failures 502; malformed requests 400.</summary>
    internal static int StatusFor(string code) => code switch
    {
        VaultErrorCodes.BadRequest => 400,
        VaultErrorCodes.UpstreamError or VaultErrorCodes.ResponseTooLarge => 502,
        _ => 403,
    };

    private static bool IsBound(PolicyDocument policy, string host) =>
        policy.Secrets.Values.Any(secret => secret.Hosts.Exists(pattern => PolicyEvaluator.HostMatches(pattern, host)));

    internal readonly record struct Authority(string? Host, int Port, bool IsIpLiteral);

    /// <summary>
    /// <c>host:port</c> from a CONNECT target. The host comes back lower-cased when it is a DNS name (letters, digits and
    /// hyphens per label, at most 253 characters, a non-numeric last label), null when it is anything else.
    /// </summary>
    internal static Authority? ParseAuthority(string target)
    {
        int colon = target.LastIndexOf(':');
        if (colon <= 0 || colon == target.Length - 1)
        {
            return null;
        }

        string portText = target[(colon + 1)..];
        if (portText.Length > 5 || !portText.All(char.IsAsciiDigit))
        {
            return null;
        }

        int port = int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture);
        string host = target[..colon];
        if (host.StartsWith('[') || IPAddress.TryParse(host, out _))
        {
            return new Authority(null, port, IsIpLiteral: true);
        }

        return new Authority(IsDnsName(host) ? host.ToLowerInvariant() : null, port, IsIpLiteral: false);
    }

    internal static bool IsDnsName(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        string[] labels = host.Split('.');
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        // An all-numeric last label is an address in disguise (127.1, 0x7f.1).
        return !labels[^1].All(char.IsAsciiDigit);
    }

    private void AuditConnect(SandboxSession sandbox, PeerInfo peer, string decision, string? code, string? destination) =>
        _core.Audit.Write(new AuditEvent
        {
            Time = _core.Time.GetUtcNow(), Op = "sandbox_connect", Decision = decision, Code = code, Session = sandbox.SessionId,
            Pid = peer.Pid, Uid = peer.Uid, Destination = destination, Sandbox = sandbox.Id,
        });

    private ErrorResponse Refuse(string op, string code, string message, string sessionId, PeerInfo peer, string? sandbox)
    {
        _core.Audit.Write(new AuditEvent
        {
            Time = _core.Time.GetUtcNow(), Op = op, Decision = "refused", Code = code, Session = sessionId, Pid = peer.Pid, Uid = peer.Uid,
            Sandbox = sandbox,
        });
        return new ErrorResponse { Code = code, Message = message };
    }

    public void Dispose()
    {
        foreach (SandboxSession sandbox in _sandboxes.Values)
        {
            Shutdown(sandbox, "vault_stopped");
        }
    }
}
