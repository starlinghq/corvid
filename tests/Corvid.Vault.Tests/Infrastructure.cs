using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Vault.Tests;

/// <summary>A Vault serving on a temporary socket, with one secret bound to 127.0.0.1 over plain HTTP.</summary>
internal sealed class TestVault : IDisposable
{
    public const string SecretName = "test.token";

    private readonly CancellationTokenSource _stop = new();
    private readonly VaultServer _server;
    private int _policyWrites;

    /// <param name="configure">Changes to the default policy before Vault starts.</param>
    /// <param name="upstream">Vault's upstream handler (by default its production one).</param>
    /// <param name="time">Vault's clock.</param>
    public TestVault(Action<PolicyDocument>? configure = null, HttpMessageHandler? upstream = null, TimeProvider? time = null)
    {
        // Short, because the socket path under it is limited to 108 bytes.
        Root = Corvid.Tests.ShortTemp.Create("cv-");
        Options = new VaultOptions
        {
            DataDirectory = Path.Combine(Root, "data"),
            ConfigPath = Path.Combine(Root, "vault.json"),
            SocketPath = Path.Combine(Root, "run", "vault.sock"),
            KeyProvider = "file",
        };
        SecretValue = "tkn_" + RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 40);
        Policy = new PolicyDocument
        {
            AllowPlainHttpLoopback = true,
            Secrets = { [SecretName] = new SecretPolicy { Hosts = ["127.0.0.1"] } },
        };
        // A failure from here on (configure, Vault's start) leaves no directory behind.
        (Core, _server) = Corvid.Tests.ShortTemp.SetUp(Root, () =>
        {
            configure?.Invoke(Policy);
            WritePolicy();
            var core = new VaultCore(Options, time, upstream);
            VaultServer? server = null;
            try
            {
                core.Store.Set(SecretName, Encoding.UTF8.GetBytes(SecretValue), "Test API token");
                server = new VaultServer(core, Options.SocketPath);
                server.Start(_stop.Token);
                return (core, server);
            }
            catch
            {
                server?.Dispose();
                core.Dispose();
                throw;
            }
        });
    }

    public string Root { get; }

    public VaultOptions Options { get; }

    public string SecretValue { get; }

    public PolicyDocument Policy { get; }

    public VaultCore Core { get; }

    public VaultClient Client(string session = "session-1") => new(session, Options.SocketPath);

    /// <summary>Rewrites vault.json after a change to <see cref="Policy"/>, with a distinct timestamp so Vault reloads it.</summary>
    public void WritePolicy()
    {
        File.WriteAllBytes(Options.ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Policy, VaultInternalJsonContext.Default.PolicyDocument));
        File.SetLastWriteTimeUtc(Options.ConfigPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_policyWrites));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Dispose();
        Core.Dispose();
        _stop.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// A loopback HTTP server that echoes the Authorization header it received: in the body and an X-Echo header (any
/// path), gzip-compressed (<c>/gzip</c>), or in a redirect's Location (<c>/redirect</c>).
/// </summary>
internal sealed class EchoServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public EchoServer()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<string?> ReceivedAuthorization { get; } = new();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            string? auth = context.Request.Headers["Authorization"];
            ReceivedAuthorization.Enqueue(auth);
            HttpListenerResponse response = context.Response;
            byte[] body = Encoding.UTF8.GetBytes($"you sent: {auth}");
            switch (context.Request.Url!.AbsolutePath)
            {
                case "/redirect":
                    response.StatusCode = 302;
                    response.RedirectLocation = $"http://elsewhere.invalid/?leak={Uri.EscapeDataString(auth ?? "")}";
                    body = [];
                    break;
                case "/gzip":
                    using (var compressed = new MemoryStream())
                    {
                        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            gzip.Write(body);
                        }

                        body = compressed.ToArray();
                    }

                    response.AddHeader("Content-Encoding", "gzip");
                    break;
                default:
                    response.AddHeader("X-Echo", auth ?? "");
                    break;
            }

            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            response.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Reads a child process's memory through /proc. Needs ptrace access: Yama scope 0 or 1 with the reader as an ancestor.</summary>
internal static class ProcessMemory
{
    public static bool CanRun => OperatingSystem.IsLinux() && File.Exists("/proc/self/maps");

    /// <summary>The labels of the patterns found anywhere in the readable memory of <paramref name="pid"/>.</summary>
    public static HashSet<string> Find(int pid, IReadOnlyDictionary<string, byte[]> patterns, out long bytesScanned)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        int overlap = patterns.Values.Max(p => p.Length) - 1;
        bytesScanned = 0;
        using var mem = new FileStream($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
        byte[] buffer = new byte[(1 << 20) + overlap];
        foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
        {
            string[] parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            string[] range = parts[0].Split('-');
            ulong start = Convert.ToUInt64(range[0], 16);
            ulong end = Convert.ToUInt64(range[1], 16);
            string path = parts.Length > 5 ? parts[5].Trim() : "";
            if (parts[1][0] != 'r' || end > long.MaxValue || path.StartsWith("[vvar", StringComparison.Ordinal) || path == "[vsyscall]")
            {
                continue;
            }

            try
            {
                long position = (long)start;
                int carried = 0;
                while (position < (long)end)
                {
                    int want = (int)Math.Min(1 << 20, (long)end - position);
                    mem.Seek(position, SeekOrigin.Begin);
                    int read = mem.Read(buffer, carried, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    var window = buffer.AsSpan(0, carried + read);
                    foreach (var (label, pattern) in patterns)
                    {
                        if (!found.Contains(label) && window.IndexOf(pattern) >= 0)
                        {
                            found.Add(label);
                        }
                    }

                    bytesScanned += read;
                    position += read;
                    carried = Math.Min(overlap, window.Length);
                    window[^carried..].CopyTo(buffer);
                }
            }
            catch (IOException)
            {
                // Some mappings (device memory, guard pages) refuse reads; nothing of ours lives there.
            }
        }

        return found;
    }

    /// <summary>The value as it could appear in a .NET process: UTF-8, UTF-16, and the encodings the scrubber covers.</summary>
    public static Dictionary<string, byte[]> Encodings(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["utf8"] = utf8,
            ["utf16"] = Encoding.Unicode.GetBytes(value),
            ["utf8-prefix"] = utf8[..12],
            ["utf16-prefix"] = Encoding.Unicode.GetBytes(value[..12]),
            ["base64"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(utf8).TrimEnd('=')),
            ["base64-utf16"] = Encoding.Unicode.GetBytes(Convert.ToBase64String(utf8).TrimEnd('=')),
            ["hex"] = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(utf8)),
            ["HEX"] = Encoding.ASCII.GetBytes(Convert.ToHexString(utf8)),
        };
    }
}

/// <summary>
/// A loopback HTTPS server standing in for real upstreams (V-003a): its own test CA, one leaf for <c>bound.test</c> and
/// <c>other.test</c>. It echoes the Authorization header it received in the body (followed by <see cref="Extra"/>) and in
/// an X-Echo header, records every request, and closes each connection after one response.
/// </summary>
internal sealed class TlsEchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ECDsa _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _leaf;
    private readonly SslStreamCertificateContext _context;

    public TlsEchoServer()
    {
        var caName = new X500DistinguishedName("CN=Test upstream CA");
        var caRequest = new CertificateRequest(caName, _caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Ca = caRequest.Create(caName, X509SignatureGenerator.CreateForECDsa(_caKey), now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(8));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=bound.test", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("bound.test");
        san.AddDnsName("other.test");
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using X509Certificate2 unkeyed = leafRequest.Create(
            caName, X509SignatureGenerator.CreateForECDsa(_caKey), now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(8));
        _leaf = unkeyed.CopyWithPrivateKey(leafKey);
        _context = SslStreamCertificateContext.Create(_leaf, [Ca], offline: true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(LoopAsync);
    }

    /// <summary>The test upstream CA (public only); the only root Vault's upstream handler trusts in these tests.</summary>
    public X509Certificate2 Ca { get; }

    public int Port { get; }

    /// <summary>Appended to every response body, e.g. a secret value an API might echo.</summary>
    public string Extra { get; set; } = "";

    public ConcurrentQueue<Http1Head> Received { get; } = new();

    public IEnumerable<string?> ReceivedAuthorization => Received.Select(h => h.Values("Authorization").FirstOrDefault());

    /// <summary>
    /// Vault's production upstream handler, changed only so that every host dials this server and only
    /// <see cref="Ca"/> is trusted. Host-name checks and HTTPS stay as in production.
    /// </summary>
    public HttpMessageHandler UpstreamHandler()
    {
        SocketsHttpHandler handler = VaultCore.CreateUpstreamHandler();
        handler.ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = Trust.Only(Ca);
        return handler;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var tls = new SslStream(client.GetStream()))
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _context }, _stop.Token);
                var http = new Http1Connection(tls);
                Http1Head? head = await http.ReadHeadAsync(_stop.Token);
                if (head is null)
                {
                    return;
                }

                long length = long.Parse(head.Values("Content-Length").FirstOrDefault() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                if (length > 0)
                {
                    await http.ReadBodyAsync(length, _stop.Token);
                }

                Received.Enqueue(head);
                string? auth = head.Values("Authorization").FirstOrDefault();
                byte[] body = Encoding.UTF8.GetBytes($"you sent: {auth}{Extra}");
                await http.WriteResponseAsync(200, "OK", [new WireHeader("X-Echo", auth ?? "")], body, close: true, toHead: head.Method == "HEAD", _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException
            or ObjectDisposedException or Http1Exception)
        {
            // A refused or abandoned connection; the tests assert on what was received.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        _leaf.Dispose();
        Ca.Dispose();
        _caKey.Dispose();
    }
}

/// <summary>Certificate validation that trusts exactly one root, as a sandboxed client given only the sandbox CA does.</summary>
internal static class Trust
{
    public static RemoteCertificateValidationCallback Only(X509Certificate2 root, Action<X509Certificate2, X509Chain>? seen = null) =>
        (_, certificate, chain, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            {
                return false;
            }

            var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(root);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain is not null)
            {
                foreach (X509ChainElement element in chain.ChainElements)
                {
                    custom.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            bool ok = custom.Build(leaf);
            if (seen is null)
            {
                custom.Dispose();
                leaf.Dispose();
            }
            else
            {
                seen(leaf, custom);
            }

            return ok;
        };

    /// <summary>True if <paramref name="leaf"/> chains to <paramref name="root"/> alone.</summary>
    public static bool Chains(X509Certificate2 leaf, X509Certificate2 root)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }
}

/// <summary>The sandbox side of V-003a, as V-003b's bridge will present it to curl or requests.</summary>
internal static class SandboxClients
{
    /// <summary>
    /// An HttpClient whose HTTPS proxy is the sandbox's socket (the proxy URL's host is never resolved; every proxy
    /// connection dials the socket) and which trusts only the sandbox CA.
    /// </summary>
    public static HttpClient Create(SandboxOpenResponse open, Action<X509Certificate2, X509Chain>? seen = null)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy("http://vault.proxy"),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = (_, cancellationToken) => DialAsync(open.ProxySocketPath, cancellationToken),
        };
        handler.SslOptions.RemoteCertificateValidationCallback = Trust.Only(X509Certificate2.CreateFromPem(open.CaCertificatePem), seen);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static async ValueTask<Stream> DialAsync(string socketPath, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends raw bytes on a fresh proxy connection and returns it, for protocol tests.</summary>
    public static async Task<Stream> RawAsync(string socketPath, string text, CancellationToken cancellationToken)
    {
        Stream stream = await DialAsync(socketPath, cancellationToken);
        await stream.WriteAsync(Encoding.Latin1.GetBytes(text), cancellationToken);
        return stream;
    }

    /// <summary>Reads one response (head and Content-Length body), or null if the peer closed first.</summary>
    public static async Task<RawResponse?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var head = new List<byte>();
        byte[] one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, timeout.Token);
            }
            catch (IOException)
            {
                read = 0;
            }

            if (read == 0)
            {
                return null;
            }

            head.Add(one[0]);
        }

        string text = Encoding.Latin1.GetString(head.ToArray());
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        int status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        byte[] body = [];
        if (headers.TryGetValue("Content-Length", out string? cl) && int.Parse(cl, System.Globalization.CultureInfo.InvariantCulture) is > 0 and var n)
        {
            body = new byte[n];
            await stream.ReadExactlyAsync(body, timeout.Token);
        }

        return new RawResponse(status, headers, Encoding.UTF8.GetString(body));
    }

    /// <summary>CONNECT to <paramref name="host"/>:443, then TLS with <paramref name="sni"/> trusting only the sandbox CA.</summary>
    public static async Task<SslStream> TunnelAsync(SandboxOpenResponse open, string host, string? sni, CancellationToken cancellationToken)
    {
        Stream raw = await RawAsync(open.ProxySocketPath, $"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n", cancellationToken);
        RawResponse? connect = await ReadResponseAsync(raw, cancellationToken);
        if (connect?.Status != 200)
        {
            await raw.DisposeAsync();
            throw new InvalidOperationException($"CONNECT got {connect?.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no answer"}.");
        }

        var tls = new SslStream(raw, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = sni ?? host,
                    RemoteCertificateValidationCallback = Trust.Only(X509Certificate2.CreateFromPem(open.CaCertificatePem)),
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                },
                cancellationToken);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync();
            throw;
        }
    }
}

internal sealed record RawResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Error => Headers.GetValueOrDefault("X-Corvid-Vault-Error");
}
