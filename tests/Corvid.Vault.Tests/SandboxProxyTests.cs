using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Vault.Tests;

/// <summary>
/// V-003a: the sandbox proxy, end to end. A sandboxed client (an HttpClient whose HTTPS proxy is the sandbox socket and
/// which trusts only the sandbox CA, the shape curl has through V-003b's bridge) talks through Vault to a loopback TLS
/// server that every upstream host maps to. The secret is bound to <c>bound.test</c>.
/// </summary>
public sealed class SandboxProxyTests : IDisposable
{
    private const string Bound = "bound.test";
    private const string Other = "other.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TlsEchoServer _echo = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);
    private readonly TestVault _vault;

    public SandboxProxyTests()
    {
        _vault = new TestVault(p => p.Secrets[TestVault.SecretName].Hosts = [Bound], _echo.UpstreamHandler(), _time);
    }

    public void Dispose()
    {
        _vault.Dispose();
        _echo.Dispose();
    }

    private Task<SandboxOpenResponse> OpenAsync(bool network = false, bool tainted = false, string session = "session-1", int? minutes = null) =>
        _vault.Client(session).OpenSandboxAsync("shell", tainted, network, minutes, Ct);

    private Task<string> PlaceholderAsync(string session = "session-1") =>
        _vault.Client(session).MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, string? placeholder)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (placeholder is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + placeholder);
        }

        return await http.SendAsync(request, Ct);
    }

    private static string? VaultError(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Corvid-Vault-Error", out var values) ? values.Single() : null;

    private int Audited(string key) => _vault.Core.Audit.Counts.GetValueOrDefault(key);

    // (1)
    [Fact]
    public async Task The_placeholder_is_substituted_for_the_bound_host_and_scrubbed_back()
    {
        SandboxOpenResponse open = await OpenAsync();
        string placeholder = await PlaceholderAsync();
        using HttpClient http = SandboxClients.Create(open);

        using HttpResponseMessage response = await GetAsync(http, $"https://{Bound}/echo", placeholder);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer " + _vault.SecretValue, Assert.Single(_echo.ReceivedAuthorization));
        Assert.Equal(Bound, Assert.Single(_echo.Received).Values("Host").Single());
        Assert.Equal("you sent: Bearer [secret:test.token]", body);
        Assert.Equal("Bearer [secret:test.token]", Assert.Single(response.Headers.GetValues("X-Echo")));
        Assert.Equal(1, Audited("sandbox_connect.allowed"));
        Assert.Equal(1, Audited("sandbox_forward.allowed"));
        Assert.False(_vault.Core.Audit.Counts.ContainsKey("forward.allowed"));
    }

    [Fact]
    public async Task Keep_alive_carries_several_requests_on_one_tunnel()
    {
        SandboxOpenResponse open = await OpenAsync();
        string placeholder = await PlaceholderAsync();
        using HttpClient http = SandboxClients.Create(open);

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await GetAsync(http, $"https://{Bound}/echo/{i}", placeholder);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, _echo.Received.Count);
        Assert.Equal(1, Audited("sandbox_connect.allowed"));
        Assert.Equal(3, Audited("sandbox_forward.allowed"));
    }

    // (2)
    [Fact]
    public async Task The_placeholder_sent_to_another_host_is_refused_and_audited()
    {
        SandboxOpenResponse open = await OpenAsync(network: true);
        string placeholder = await PlaceholderAsync();
        using HttpClient http = SandboxClients.Create(open);

        using HttpResponseMessage response = await GetAsync(http, $"https://{Other}/steal", placeholder);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(VaultErrorCodes.HostNotBound, VaultError(response));
        Assert.Empty(_echo.Received);
        Assert.Equal(1, Audited("sandbox_forward.refused"));
        string log = await File.ReadAllTextAsync(_vault.Options.AuditPath, Ct);
        Assert.Contains("\"code\":\"host_not_bound\"", log, StringComparison.Ordinal);
        Assert.Contains($"\"sandbox\":\"{open.SandboxId}\"", log, StringComparison.Ordinal);
        Assert.DoesNotContain(_vault.SecretValue, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_placeholder_from_another_session_is_refused()
    {
        SandboxOpenResponse open = await OpenAsync(session: "session-A");
        string placeholder = await PlaceholderAsync(session: "session-B");
        using HttpClient http = SandboxClients.Create(open);

        using HttpResponseMessage response = await GetAsync(http, $"https://{Bound}/echo", placeholder);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(VaultErrorCodes.BadPlaceholder, VaultError(response));
        Assert.Empty(_echo.Received);
    }

    // (3)
    [Fact]
    public async Task Without_network_an_unbound_host_is_refused_at_CONNECT()
    {
        SandboxOpenResponse open = await OpenAsync(network: false);

        await using Stream raw = await SandboxClients.RawAsync(open.ProxySocketPath, $"CONNECT {Other}:443 HTTP/1.1\r\nHost: {Other}:443\r\n\r\n", Ct);
        RawResponse? response = await SandboxClients.ReadResponseAsync(raw, Ct);

        Assert.Equal(403, response?.Status);
        Assert.Equal(VaultErrorCodes.NetworkNotGranted, response?.Error);
        Assert.Equal(1, Audited("sandbox_connect.refused"));
        Assert.Empty(_echo.Received);
    }

    [Fact]
    public async Task Without_network_a_request_carrying_no_placeholder_is_refused_even_to_a_bound_host()
    {
        SandboxOpenResponse open = await OpenAsync(network: false);
        using HttpClient http = SandboxClients.Create(open);

        using HttpResponseMessage response = await GetAsync(http, $"https://{Bound}/plain", placeholder: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(VaultErrorCodes.NetworkNotGranted, VaultError(response));
        Assert.Empty(_echo.Received);
    }

    [Fact]
    public async Task With_network_a_request_carrying_no_placeholder_passes_through_and_is_still_scrubbed()
    {
        SandboxOpenResponse open = await OpenAsync(network: true);
        _echo.Extra = " and an API echoing " + _vault.SecretValue;
        using HttpClient http = SandboxClients.Create(open);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Other}/public?q=1");
        request.Headers.TryAddWithoutValidation("X-Custom", "kept");
        using HttpResponseMessage response = await http.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Http1Head received = Assert.Single(_echo.Received);
        Assert.Equal("/public?q=1", received.Target);
        Assert.Equal("kept", received.Values("X-Custom").Single());
        Assert.Empty(received.Values("Authorization"));
        Assert.Equal("you sent:  and an API echoing [secret:test.token]", body);
        Assert.Equal(1, Audited("sandbox_forward.allowed"));
    }

    // (4)
    [Fact]
    public async Task Leaves_are_scoped_to_the_SNI_host_and_to_their_sandbox()
    {
        SandboxOpenResponse a = await OpenAsync(network: true);
        SandboxOpenResponse b = await OpenAsync(network: true);
        var seen = new List<(X509Certificate2 Leaf, X509Chain Chain)>();
        using HttpClient http = SandboxClients.Create(a, (leaf, chain) => seen.Add((leaf, chain)));

        using (await GetAsync(http, $"https://{Bound}/", null))
        {
        }

        using (await GetAsync(http, $"https://{Other}/", null))
        {
        }

        Assert.Equal(2, seen.Count);
        X509Certificate2 boundLeaf = seen[0].Leaf, otherLeaf = seen[1].Leaf;
        Assert.Equal([Bound], DnsNames(boundLeaf));
        Assert.Equal([Other], DnsNames(otherLeaf));
        Assert.NotEqual(boundLeaf.Thumbprint, otherLeaf.Thumbprint);
        Assert.NotEqual(boundLeaf.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo(), otherLeaf.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
        Assert.All(seen, s => Assert.All(s.Chain.ChainElements, e => Assert.False(e.Certificate.HasPrivateKey)));

        using X509Certificate2 caA = X509Certificate2.CreateFromPem(a.CaCertificatePem);
        using X509Certificate2 caB = X509Certificate2.CreateFromPem(b.CaCertificatePem);
        Assert.True(Trust.Chains(boundLeaf, caA));
        Assert.False(Trust.Chains(boundLeaf, caB));

        foreach (var (leaf, chain) in seen)
        {
            chain.Dispose();
            leaf.Dispose();
        }
    }

    [Fact]
    public async Task An_SNI_that_differs_from_the_CONNECT_host_fails_the_handshake()
    {
        SandboxOpenResponse open = await OpenAsync(network: true);

        await Assert.ThrowsAnyAsync<Exception>(() => SandboxClients.TunnelAsync(open, Bound, sni: Other, Ct));

        // Audited after the handshake fails on Vault's side; give the serving task a moment.
        await WaitForAsync(() => Audited("sandbox_connect.refused") == 1);
        Assert.Contains("tls_handshake_failed", await File.ReadAllTextAsync(_vault.Options.AuditPath, Ct), StringComparison.Ordinal);
    }

    // (5)
    [Fact]
    public async Task The_CA_private_key_never_leaves_Vault()
    {
        // The open response, as bytes on the wire.
        byte[] frame;
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_vault.Options.SocketPath), Ct);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await VaultWire.WriteRequestAsync(stream, new SandboxOpenRequest { SessionId = "session-1", Network = false }, Ct);
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, Ct);
            frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(frame, Ct);
        }

        string wire = Encoding.UTF8.GetString(frame);
        Assert.Contains("BEGIN CERTIFICATE", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", wire, StringComparison.Ordinal);
        var open = (SandboxOpenResponse)JsonSerializer.Deserialize(frame, VaultJsonContext.Default.VaultResponse)!;
        using X509Certificate2 ca = X509Certificate2.CreateFromPem(open.CaCertificatePem);
        Assert.False(ca.HasPrivateKey);

        // A leaf is minted and used, and still nothing but the socket appears on disk.
        string placeholder = await PlaceholderAsync();
        var seen = new List<X509Chain>();
        using (HttpClient http = SandboxClients.Create(open, (leaf, chain) => seen.Add(chain)))
        using (HttpResponseMessage response = await GetAsync(http, $"https://{Bound}/", placeholder))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        string sandboxDirectory = Path.GetDirectoryName(open.ProxySocketPath)!;
        Assert.Equal([open.SandboxId + ".sock"], Directory.EnumerateFileSystemEntries(sandboxDirectory).Select(Path.GetFileName));
        string[] files = Directory.EnumerateFiles(_vault.Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_vault.Root, f)).Order().ToArray();
        Assert.Equal(
            ["data/audit.jsonl", "data/master.key", "data/store.bin", "run/sandbox/" + open.SandboxId + ".sock", "run/vault.sock", "vault.json"],
            files);
        foreach (string file in files.Where(f => !f.EndsWith(".sock", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("PRIVATE KEY", await File.ReadAllTextAsync(Path.Combine(_vault.Root, file), Ct), StringComparison.Ordinal);
        }

        X509Chain presented = Assert.Single(seen);
        Assert.Equal(2, presented.ChainElements.Count);
        Assert.All(presented.ChainElements, e => Assert.False(e.Certificate.HasPrivateKey));
        Assert.Equal(ca.Thumbprint, presented.ChainElements[^1].Certificate.Thumbprint);
        presented.Dispose();
    }

    // (6)
    [Theory]
    [InlineData("GET http://bound.test/ HTTP/1.1\r\nHost: bound.test\r\n\r\n", 405, VaultErrorCodes.NotProxyable)]
    [InlineData("POST / HTTP/1.1\r\nHost: bound.test\r\nContent-Length: 0\r\n\r\n", 405, VaultErrorCodes.NotProxyable)]
    [InlineData("CONNECT bound.test:80 HTTP/1.1\r\nHost: bound.test:80\r\n\r\n", 403, VaultErrorCodes.NotProxyable)]
    [InlineData("CONNECT 127.0.0.1:443 HTTP/1.1\r\nHost: 127.0.0.1:443\r\n\r\n", 403, VaultErrorCodes.NotProxyable)]
    [InlineData("CONNECT [::1]:443 HTTP/1.1\r\nHost: [::1]:443\r\n\r\n", 403, VaultErrorCodes.NotProxyable)]
    [InlineData("CONNECT bad_host!:443 HTTP/1.1\r\nHost: x\r\n\r\n", 400, VaultErrorCodes.BadRequest)]
    [InlineData("CONNECT bound.test:443 HTTP/1.1\nHost: bound.test\n\n", 400, VaultErrorCodes.BadRequest)]
    public async Task Anything_but_CONNECT_to_a_DNS_name_on_443_is_refused(string request, int status, string code)
    {
        SandboxOpenResponse open = await OpenAsync(network: true);

        await using Stream raw = await SandboxClients.RawAsync(open.ProxySocketPath, request, Ct);
        RawResponse? response = await SandboxClients.ReadResponseAsync(raw, Ct);

        Assert.Equal(status, response?.Status);
        Assert.Equal(code, response?.Error);
        Assert.Null(await SandboxClients.ReadResponseAsync(raw, Ct)); // closed after the refusal
        Assert.Equal(1, Audited("sandbox_connect.refused"));
        Assert.Empty(_echo.Received);
    }

    [Fact]
    public async Task Plain_bytes_after_CONNECT_fail_the_handshake_so_there_are_no_raw_tunnels()
    {
        SandboxOpenResponse open = await OpenAsync(network: true);
        await using Stream raw = await SandboxClients.RawAsync(open.ProxySocketPath, $"CONNECT {Bound}:443 HTTP/1.1\r\nHost: {Bound}:443\r\n\r\n", Ct);
        Assert.Equal(200, (await SandboxClients.ReadResponseAsync(raw, Ct))?.Status);

        await raw.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {Bound}\r\n\r\n"), Ct);

        Assert.Null(await SandboxClients.ReadResponseAsync(raw, Ct));
        await WaitForAsync(() => Audited("sandbox_connect.refused") == 1);
        Assert.Empty(_echo.Received);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: bound.test\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n", 400, VaultErrorCodes.NotProxyable)]
    [InlineData("POST / HTTP/1.1\r\nHost: bound.test\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 411, VaultErrorCodes.BadRequest)]
    [InlineData("POST / HTTP/1.1\r\nHost: bound.test\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 400, VaultErrorCodes.BadRequest)]
    [InlineData("GET / HTTP/1.1\r\nHost: other.test\r\n\r\n", 400, VaultErrorCodes.BadRequest)]
    [InlineData("GET https://bound.test/ HTTP/1.1\r\nHost: bound.test\r\n\r\n", 400, VaultErrorCodes.BadRequest)]
    [InlineData("CONNECT other.test:443 HTTP/1.1\r\nHost: other.test:443\r\n\r\n", 405, VaultErrorCodes.NotProxyable)]
    public async Task Inside_the_tunnel_upgrades_chunked_bodies_and_host_changes_are_refused(string request, int status, string code)
    {
        SandboxOpenResponse open = await OpenAsync(network: true);
        await using SslStream tls = await SandboxClients.TunnelAsync(open, Bound, sni: null, Ct);

        await tls.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        RawResponse? response = await SandboxClients.ReadResponseAsync(tls, Ct);

        Assert.Equal(status, response?.Status);
        Assert.Equal(code, response?.Error);
        Assert.Equal(1, Audited("sandbox_forward.refused"));
        Assert.Empty(_echo.Received);
    }

    [Fact]
    public async Task A_Host_header_with_the_port_is_accepted_and_Expect_continue_is_answered()
    {
        SandboxOpenResponse open = await OpenAsync(network: true);
        await using SslStream tls = await SandboxClients.TunnelAsync(open, Bound, sni: null, Ct);

        await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET /x HTTP/1.1\r\nHost: {Bound}:443\r\n\r\n"), Ct);
        Assert.Equal(200, (await SandboxClients.ReadResponseAsync(tls, Ct))?.Status);

        // No placeholder and network granted: passed through once the interim 100 let the body come.
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"PUT /y HTTP/1.1\r\nHost: {Bound}\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n"), Ct);
        Assert.Equal(100, (await SandboxClients.ReadResponseAsync(tls, Ct))?.Status);
        await tls.WriteAsync("hello"u8.ToArray(), Ct);
        Assert.Equal(200, (await SandboxClients.ReadResponseAsync(tls, Ct))?.Status);
        Assert.Equal(2, _echo.Received.Count);
    }

    // (7)
    [Fact]
    public async Task Closing_removes_the_socket_and_ends_live_connections()
    {
        SandboxOpenResponse open = await OpenAsync(network: true);
        await using SslStream live = await SandboxClients.TunnelAsync(open, Bound, sni: null, Ct);
        await live.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {Bound}\r\n\r\n"), Ct);
        Assert.Equal(200, (await SandboxClients.ReadResponseAsync(live, Ct))?.Status);

        var wrongSession = await Assert.ThrowsAsync<VaultException>(() => _vault.Client("session-2").CloseSandboxAsync(open.SandboxId, Ct));
        Assert.Equal(VaultErrorCodes.UnknownSandbox, wrongSession.Code);
        Assert.True(File.Exists(open.ProxySocketPath));

        await _vault.Client().CloseSandboxAsync(open.SandboxId, Ct);

        Assert.False(File.Exists(open.ProxySocketPath));
        await Assert.ThrowsAsync<SocketException>(async () => await SandboxClients.DialAsync(open.ProxySocketPath, Ct));
        Assert.Null(await SandboxClients.ReadResponseAsync(live, Ct));
        Assert.Equal(VaultErrorCodes.UnknownSandbox, (await Assert.ThrowsAsync<VaultException>(() => _vault.Client().CloseSandboxAsync(open.SandboxId, Ct))).Code);
        Assert.Equal(1, Audited("sandbox_close.allowed"));
        Assert.Equal(0, _vault.Core.Sandboxes.Count);
    }

    [Fact]
    public async Task Expiry_closes_the_sandbox_like_close_does()
    {
        SandboxOpenResponse open = await OpenAsync(network: true, minutes: 5);
        Assert.Equal(_time.Now.AddMinutes(5), open.ExpiresAt);
        await using SslStream live = await SandboxClients.TunnelAsync(open, Bound, sni: null, Ct);

        _time.Now += TimeSpan.FromMinutes(6);
        await _vault.Client().GetCatalogAsync(Ct); // any Vault call sweeps

        Assert.False(File.Exists(open.ProxySocketPath));
        Assert.Null(await SandboxClients.ReadResponseAsync(live, Ct));
        Assert.Contains("\"code\":\"expired\"", await File.ReadAllTextAsync(_vault.Options.AuditPath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_sandbox_refuses_a_new_connection_without_any_other_Vault_call()
    {
        SandboxOpenResponse open = await OpenAsync(network: true, minutes: 5);
        _time.Now += TimeSpan.FromMinutes(6);

        // Vault accepts, sees the expiry and closes at once, so the write may already find the pipe broken.
        await using Stream raw = await SandboxClients.DialAsync(open.ProxySocketPath, Ct);
        try
        {
            await raw.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {Bound}:443 HTTP/1.1\r\nHost: {Bound}:443\r\n\r\n"), Ct);
        }
        catch (IOException)
        {
        }

        Assert.Null(await SandboxClients.ReadResponseAsync(raw, Ct));
        await WaitForAsync(() => !File.Exists(open.ProxySocketPath));
        Assert.Equal(0, Audited("sandbox_connect.allowed"));
    }

    [Fact]
    public async Task The_lifetime_is_capped_by_policy_and_the_number_of_sandboxes_is_limited()
    {
        _vault.Policy.SandboxLifetimeMinutes = 10;
        _vault.Policy.MaxSandboxes = 2;
        _vault.WritePolicy();

        SandboxOpenResponse open = await OpenAsync(minutes: 60);
        await OpenAsync();
        var full = await Assert.ThrowsAsync<VaultException>(() => OpenAsync());

        Assert.Equal(_time.Now.AddMinutes(10), open.ExpiresAt);
        Assert.Equal(VaultErrorCodes.SandboxLimit, full.Code);
        Assert.Equal(12, open.SandboxId.Length);
    }

    [Fact]
    public async Task Disposing_Vault_closes_every_sandbox()
    {
        var vault = new TestVault();
        SandboxOpenResponse open = await vault.Client().OpenSandboxAsync(null, false, false, null, Ct);
        Assert.True(File.Exists(open.ProxySocketPath));

        vault.Core.Dispose();

        Assert.False(File.Exists(open.ProxySocketPath));
        vault.Dispose();
    }

    // (8)
    [Fact]
    public async Task Ask_and_tainted_rules_still_refuse_because_a_sandbox_is_never_pre_approved()
    {
        SandboxOpenResponse tainted = await OpenAsync(tainted: true);
        SandboxOpenResponse clean = await OpenAsync();
        string placeholder = await PlaceholderAsync();
        using HttpClient taintedHttp = SandboxClients.Create(tainted);
        using HttpClient cleanHttp = SandboxClients.Create(clean);

        using HttpResponseMessage get = await GetAsync(taintedHttp, $"https://{Bound}/", placeholder);
        using var post = new HttpRequestMessage(HttpMethod.Post, $"https://{Bound}/") { Content = new StringContent("{}") };
        post.Headers.TryAddWithoutValidation("Authorization", "Bearer " + placeholder);
        using HttpResponseMessage posted = await cleanHttp.SendAsync(post, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(VaultErrorCodes.ApprovalRequired, VaultError(get));
        Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);
        Assert.Equal(VaultErrorCodes.ApprovalRequired, VaultError(posted));
        Assert.Empty(_echo.Received);
    }

    [Fact]
    public async Task The_sandbox_toolset_is_checked_against_the_secret()
    {
        _vault.Policy.Secrets[TestVault.SecretName].Toolsets = ["github"];
        _vault.WritePolicy();
        SandboxOpenResponse open = await OpenAsync();
        using HttpClient http = SandboxClients.Create(open);

        using HttpResponseMessage response = await GetAsync(http, $"https://{Bound}/", await PlaceholderAsync());

        Assert.Equal(VaultErrorCodes.ToolsetNotAllowed, VaultError(response));
        Assert.Empty(_echo.Received);
    }

    private static string[] DnsNames(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames().ToArray();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition());
    }
}
