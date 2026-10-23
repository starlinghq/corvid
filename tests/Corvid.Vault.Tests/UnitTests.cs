using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Corvid.Vault.Contracts;

namespace Corvid.Vault.Tests;

public sealed class ScrubberTests
{
    private const string Value = "ghp_Q9xv2LmT7rWc4ZpK8sNd3JfYh6BuE1aGo0Ri";

    private static Scrubber Build(string value = Value) => Scrubber.Build(new Dictionary<string, StoredSecret>
    {
        ["github.token"] = new(Encoding.UTF8.GetBytes(value), "", DateTimeOffset.UnixEpoch),
    });

    public static TheoryData<string> Encoded()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(Value);
        string base64 = Convert.ToBase64String(utf8);
        return
        [
            Value,
            base64,
            base64.TrimEnd('='),
            base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Convert.ToHexStringLower(utf8),
            Convert.ToHexString(utf8),
            Uri.EscapeDataString(Value),
            JsonSerializer.Serialize(Value).Trim('"'),
            Value[..12],
        ];
    }

    [Theory]
    [MemberData(nameof(Encoded))]
    public void Every_encoding_is_replaced(string encoded)
    {
        var (text, hits) = Build().Scrub($"before {encoded} after");

        Assert.Equal("before [secret:github.token] after", text);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void The_longest_match_wins_over_the_prefix()
    {
        var (text, _) = Build().Scrub(Value + "!");

        Assert.Equal("[secret:github.token]!", text);
    }

    [Fact]
    public void Values_with_json_specials_are_matched_as_json_escapes()
    {
        const string value = "pw\"with<quotes>&slashes\\/and+more";
        string json = JsonSerializer.Serialize(new { token = value });

        var (text, hits) = Build(value).Scrub(json);

        Assert.DoesNotContain("with<quotes>", text, StringComparison.Ordinal);
        Assert.True(hits >= 1);
    }

    [Fact]
    public void Text_without_values_is_returned_unchanged()
    {
        const string input = "nothing secret here, just ghp_ and a different token ghp_AAAAAAAAAAAAAAAA";

        var (text, hits) = Build().Scrub(input);

        Assert.Same(input, text);
        Assert.Equal(0, hits);
    }

    [Fact]
    public void Binary_input_is_scrubbed_bytewise()
    {
        byte[] input = [0, 255, .. Encoding.UTF8.GetBytes(Value), 1, 2];

        var (bytes, hits) = Build().Scrub(input);

        Assert.Equal(1, hits);
        Assert.Equal([0, 255, .. Encoding.UTF8.GetBytes("[secret:github.token]"), 1, 2], bytes);
    }
}

public sealed class PlaceholderTests
{
    [Theory]
    [InlineData("github.token", true)]
    [InlineData("a", true)]
    [InlineData("aws-prod.access-key", true)]
    [InlineData("GitHub", false)]
    [InlineData("has_underscore", false)]
    [InlineData(".leading-dot", false)]
    [InlineData("", false)]
    public void Secret_names(string name, bool valid) => Assert.Equal(valid, Placeholders.IsValidSecretName(name));

    [Fact]
    public void Placeholders_are_found_in_text()
    {
        string p = Placeholders.Format("github.token", new string('a', Placeholders.TokenLength));

        PlaceholderMatch match = Assert.Single(Placeholders.Find($"Bearer {p}"));

        Assert.Equal(p, match.Value);
        Assert.Equal("github.token", match.SecretName);
    }

    [Fact]
    public void A_placeholder_resolves_only_for_its_session_and_uid_until_it_expires()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var registry = new PlaceholderRegistry(time);
        var (placeholder, _) = registry.Mint("github.token", "s1", 1000, TimeSpan.FromMinutes(10));
        PlaceholderMatch match = Assert.Single(Placeholders.Find(placeholder));

        Assert.Equal("github.token", registry.Resolve(match, "s1", 1000));
        Assert.Null(registry.Resolve(match, "s2", 1000));
        Assert.Null(registry.Resolve(match, "s1", 1001));

        time.Now += TimeSpan.FromMinutes(11);
        Assert.Null(registry.Resolve(match, "s1", 1000));
    }

    [Fact]
    public void A_placeholder_relabelled_with_another_secret_name_does_not_resolve()
    {
        var registry = new PlaceholderRegistry(TimeProvider.System);
        var (placeholder, _) = registry.Mint("github.token", "s1", 1000, TimeSpan.FromMinutes(10));
        string forged = placeholder.Replace("github.token", "aws.key", StringComparison.Ordinal);

        Assert.Null(registry.Resolve(Assert.Single(Placeholders.Find(forged)), "s1", 1000));
    }
}

public sealed class PolicyTests
{
    private static readonly Uri GitHub = new("https://api.github.com/repos/o/r/issues");

    private static PolicyDocument Document(SecretPolicy policy) => new() { Secrets = { ["github.token"] = policy } };

    private static ForwardRequest Request(bool approved = false, bool tainted = false, string? toolset = null) =>
        new() { SessionId = "s", Method = "GET", Url = "", Approved = approved, Tainted = tainted, Toolset = toolset };

    private static PolicyResult Evaluate(SecretPolicy policy, string method = "GET", Uri? uri = null, ForwardRequest? request = null) =>
        PolicyEvaluator.Evaluate(Document(policy), "github.token", method, uri ?? GitHub, request ?? Request());

    [Fact]
    public void Unbound_secrets_are_refused() =>
        Assert.Equal(VaultErrorCodes.HostNotBound, Evaluate(new SecretPolicy()).Code);

    [Theory]
    [InlineData("api.github.com", "https://api.github.com/x", true)]
    [InlineData("api.github.com", "https://API.GitHub.com/x", true)]
    [InlineData("api.github.com", "https://evil.example/x", false)]
    [InlineData("api.github.com", "https://api.github.com.evil.example/x", false)]
    [InlineData("*.github.com", "https://uploads.github.com/x", true)]
    [InlineData("*.github.com", "https://github.com/x", false)]
    public void Hosts(string pattern, string url, bool allowed) =>
        Assert.Equal(allowed, Evaluate(new SecretPolicy { Hosts = [pattern] }, uri: new Uri(url)).Allowed);

    [Fact]
    public void Plain_http_is_refused_even_to_a_bound_host() =>
        Assert.Equal(VaultErrorCodes.InsecureTransport, Evaluate(new SecretPolicy { Hosts = ["api.github.com"] }, uri: new Uri("http://api.github.com/x")).Code);

    [Fact]
    public void Without_a_method_table_reads_are_automatic_and_writes_need_approval()
    {
        var policy = new SecretPolicy { Hosts = ["api.github.com"] };

        Assert.True(Evaluate(policy, "GET").Allowed);
        Assert.Equal(VaultErrorCodes.ApprovalRequired, Evaluate(policy, "POST").Code);
        Assert.True(Evaluate(policy, "POST", request: Request(approved: true)).Allowed);
    }

    [Fact]
    public void A_method_table_denies_unlisted_methods_and_confirm_is_not_yet_available()
    {
        var policy = new SecretPolicy
        {
            Hosts = ["api.github.com"],
            Methods = new() { ["GET"] = Rule.Auto, ["DELETE"] = Rule.Confirm },
        };

        Assert.True(Evaluate(policy, "GET").Allowed);
        Assert.Equal(VaultErrorCodes.Denied, Evaluate(policy, "PUT", request: Request(approved: true)).Code);
        Assert.Equal(VaultErrorCodes.ConfirmUnavailable, Evaluate(policy, "DELETE", request: Request(approved: true)).Code);
    }

    [Fact]
    public void Tainted_turns_raise_auto_to_ask()
    {
        var policy = new SecretPolicy { Hosts = ["api.github.com"] };

        Assert.Equal(VaultErrorCodes.ApprovalRequired, Evaluate(policy, request: Request(tainted: true)).Code);
        Assert.True(Evaluate(policy, request: Request(tainted: true, approved: true)).Allowed);
    }

    [Fact]
    public void Paths_and_toolsets_are_enforced()
    {
        var policy = new SecretPolicy
        {
            Hosts = ["api.github.com"],
            DenyPaths = ["/user/keys*"],
            Paths = ["/repos/*"],
            Toolsets = ["github"],
        };

        Assert.True(Evaluate(policy, request: Request(toolset: "github")).Allowed);
        Assert.Equal(VaultErrorCodes.ToolsetNotAllowed, Evaluate(policy, request: Request(toolset: "web")).Code);
        Assert.Equal(VaultErrorCodes.Denied, Evaluate(policy, uri: new Uri("https://api.github.com/user/keys/1"), request: Request(toolset: "github")).Code);
        Assert.Equal(VaultErrorCodes.Denied, Evaluate(policy, uri: new Uri("https://api.github.com/gists"), request: Request(toolset: "github")).Code);
    }

    [Theory]
    [InlineData("/repos/*", "/repos/a/b", true)]
    [InlineData("/repos/*/*/hooks*", "/repos/a/b/hooks/1", true)]
    [InlineData("/repos/*/*/hooks*", "/repos/a/b/issues", false)]
    [InlineData("*", "/anything", true)]
    [InlineData("/exact", "/exact/more", false)]
    public void Globs(string pattern, string path, bool matches) => Assert.Equal(matches, Glob.Matches(pattern, path));

    [Fact]
    public void Budgets_cap_requests_per_hour()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var budgets = new Budgets(time);

        Assert.True(budgets.TryConsume("t", 2));
        Assert.True(budgets.TryConsume("t", 2));
        Assert.False(budgets.TryConsume("t", 2));
        time.Now += TimeSpan.FromMinutes(61);
        Assert.True(budgets.TryConsume("t", 2));
    }
}

public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cv-store-" + Guid.NewGuid().ToString("N")[..8]);

    public StoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Values_round_trip_and_are_not_on_disk_in_the_clear()
    {
        byte[] key = new byte[32];
        Random.Shared.NextBytes(key);
        string path = Path.Combine(_dir, "store.bin");
        const string value = "sk-live-0123456789abcdefghijklmnop";

        new SecretStore(path, key).Set("api.key", Encoding.UTF8.GetBytes(value), "An API key");

        Assert.True(new SecretStore(path, key).TryGet("api.key", out StoredSecret stored));
        Assert.Equal(value, Encoding.UTF8.GetString(stored.Value));
        Assert.Equal("An API key", stored.Description);
        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(-1, file.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)));
        Assert.Equal(-1, file.AsSpan().IndexOf("api.key"u8));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void A_wrong_key_cannot_open_the_store()
    {
        string path = Path.Combine(_dir, "store.bin");
        new SecretStore(path, new byte[32]).Set("a", "0123456789abcdef"u8.ToArray(), "");
        byte[] other = new byte[32];
        other[0] = 1;

        Assert.Throws<InvalidDataException>(() => new SecretStore(path, other));
    }

    [Fact]
    public void Changes_by_another_process_are_picked_up()
    {
        byte[] key = new byte[32];
        string path = Path.Combine(_dir, "store.bin");
        var server = new SecretStore(path, key);
        var cli = new SecretStore(path, key);

        cli.Set("late.secret", "0123456789abcdef"u8.ToArray(), "");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.True(server.TryGet("late.secret", out _));
    }
}

public sealed class EntropyTests
{
    [Theory]
    [InlineData("hunter2")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa")]
    [InlineData("password")]
    public void Guessable_values_are_refused(string value) => Assert.NotNull(Entropy.Problem(Encoding.UTF8.GetBytes(value)));

    [Theory]
    [InlineData("ghp_Q9xv2LmT7rWc4ZpK8sNd3JfYh6BuE1aGo0Ri")]
    [InlineData("xoxb-1234-5678-AbCdEfGhIjKlMnOp")]
    public void Real_tokens_are_accepted(string value) => Assert.Null(Entropy.Problem(Encoding.UTF8.GetBytes(value)));
}

public sealed class SandboxCertificateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void The_CA_is_a_public_only_pathlen_0_root_with_a_key_identifier()
    {
        using var certificates = new SandboxCertificates("abcdef123456", Now, Now.AddHours(2));
        X509Certificate2 ca = certificates.Ca;

        Assert.False(ca.HasPrivateKey);
        Assert.Equal("CN=Corvid Vault sandbox CA abcdef123456", ca.Subject);
        Assert.Equal(ca.Subject, ca.Issuer);
        Assert.Equal(16, ca.SerialNumberBytes.Length);
        Assert.Equal(Now.AddHours(-1).ToUnixTimeSeconds(), new DateTimeOffset(ca.NotBefore).ToUnixTimeSeconds());
        Assert.Equal(Now.AddHours(3).ToUnixTimeSeconds(), new DateTimeOffset(ca.NotAfter).ToUnixTimeSeconds());
        var basic = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.Critical);
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.HasPathLengthConstraint);
        Assert.Equal(0, basic.PathLengthConstraint);
        var usage = ca.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);
        Assert.NotNull(ca.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier);
        Assert.Contains("BEGIN CERTIFICATE", certificates.CaCertificatePem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", certificates.CaCertificatePem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_leaf_names_exactly_its_host_and_chains_to_its_CA_only()
    {
        using var certificates = new SandboxCertificates("abcdef123456", Now, Now.AddHours(2));
        using var other = new SandboxCertificates("zzzzzz999999", Now, Now.AddHours(2));
        X509Certificate2 leaf = certificates.For("api.example.com")!.TargetCertificate;

        Assert.True(leaf.HasPrivateKey);
        Assert.Equal(["api.example.com"], leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        Assert.Empty(leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses());
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        var aki = leaf.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single();
        Assert.Equal(
            Convert.FromHexString(certificates.Ca.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier!),
            aki.KeyIdentifier!.Value.ToArray());
        Assert.Equal(certificates.Ca.NotBefore, leaf.NotBefore);
        Assert.Equal(certificates.Ca.NotAfter, leaf.NotAfter);
        using X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData);
        Assert.True(Trust.Chains(publicLeaf, certificates.Ca));
        Assert.False(Trust.Chains(publicLeaf, other.Ca));
    }

    [Fact]
    public void Leaves_are_cached_per_host_capped_and_gone_after_dispose()
    {
        var certificates = new SandboxCertificates("abcdef123456", Now, Now.AddHours(2));

        var first = certificates.For("a.example");
        Assert.Same(first, certificates.For("a.example"));
        Assert.NotSame(first, certificates.For("b.example"));
        for (int i = 2; i < SandboxCertificates.MaxHosts; i++)
        {
            Assert.NotNull(certificates.For($"h{i}.example"));
        }

        Assert.Null(certificates.For("one-too-many.example"));
        Assert.NotNull(certificates.For("a.example"));

        certificates.Dispose();
        Assert.Null(certificates.For("a.example"));
        Assert.Null(certificates.Existing("a.example"));
    }
}

public sealed class Http1Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<Http1Head?> ReadAsync(string text) =>
        new Http1Connection(new MemoryStream(Encoding.Latin1.GetBytes(text))).ReadHeadAsync(Ct);

    private static Http1Head Head(string text) => Http1Connection.ParseHead(Encoding.Latin1.GetBytes(text));

    private static Http1Exception Refused(Action action) => Assert.Throws<Http1Exception>(action);

    [Fact]
    public async Task A_well_formed_request_parses_and_keep_alive_reads_the_next_one()
    {
        var connection = new Http1Connection(new MemoryStream(Encoding.Latin1.GetBytes(
            "\r\nPOST /a?b=1 HTTP/1.1\r\nHost: x.test\r\nContent-Length: 3\r\nX-Folded:  v  \r\n\r\nabcGET /next HTTP/1.1\r\nHost: x.test\r\n\r\n")));

        Http1Head first = (await connection.ReadHeadAsync(Ct))!;
        Http1Framing framing = Http1Connection.Check(first, "x.test", 1024);
        byte[] body = await connection.ReadBodyAsync(framing.ContentLength, Ct);
        Http1Head second = (await connection.ReadHeadAsync(Ct))!;

        Assert.Equal(("POST", "/a?b=1"), (first.Method, first.Target));
        Assert.Equal("v", first.Values("x-folded").Single());
        Assert.Equal("abc"u8.ToArray(), body);
        Assert.Equal("/next", second.Target);
        Assert.Null(await connection.ReadHeadAsync(Ct));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\nHost: x\n\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\nX: y\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\rX: y\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nX: a\r\n folded\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nBad Name: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("GET  / HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.0\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nX: a\u0001b\r\n\r\n")]
    public async Task Malformed_heads_are_refused(string text)
    {
        var e = await Assert.ThrowsAsync<Http1Exception>(() => ReadAsync(text));

        Assert.True(e.Status is 400 or 505);
    }

    [Fact]
    public async Task An_oversized_header_block_is_refused_with_431()
    {
        string text = "GET / HTTP/1.1\r\nX: " + new string('a', Http1Connection.MaxHeaderBytes) + "\r\n\r\n";

        Assert.Equal(431, (await Assert.ThrowsAsync<Http1Exception>(() => ReadAsync(text))).Status);
    }

    [Fact]
    public async Task A_connection_closed_inside_a_head_is_refused_and_a_clean_close_is_not()
    {
        Assert.Null(await ReadAsync(""));
        await Assert.ThrowsAsync<Http1Exception>(() => ReadAsync("GET / HTTP/1.1\r\nHost"));
    }

    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nTransfer-Encoding: chunked", 411, VaultErrorCodes.BadRequest)]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nContent-Length: 3\r\nTransfer-Encoding: chunked", 400, VaultErrorCodes.BadRequest)]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nContent-Length: 3\r\nContent-Length: 3", 400, VaultErrorCodes.BadRequest)]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nContent-Length: +3", 400, VaultErrorCodes.BadRequest)]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nContent-Length: 3, 3", 400, VaultErrorCodes.BadRequest)]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nContent-Length: 2048", 413, VaultErrorCodes.BadRequest)]
    [InlineData("GET / HTTP/1.1\r\nHost: x.test\r\nUpgrade: websocket", 400, VaultErrorCodes.NotProxyable)]
    [InlineData("GET / HTTP/1.1\r\nHost: y.test", 400, VaultErrorCodes.BadRequest)]
    [InlineData("GET / HTTP/1.1\r\nHost: x.test:8443", 400, VaultErrorCodes.BadRequest)]
    [InlineData("GET / HTTP/1.1\r\nHost: x.test\r\nHost: x.test", 400, VaultErrorCodes.BadRequest)]
    [InlineData("GET / HTTP/1.1", 400, VaultErrorCodes.BadRequest)]
    [InlineData("GET http://x.test/ HTTP/1.1\r\nHost: x.test", 400, VaultErrorCodes.BadRequest)]
    [InlineData("OPTIONS * HTTP/1.1\r\nHost: x.test", 400, VaultErrorCodes.BadRequest)]
    [InlineData("CONNECT x.test:443 HTTP/1.1\r\nHost: x.test", 405, VaultErrorCodes.NotProxyable)]
    [InlineData("POST / HTTP/1.1\r\nHost: x.test\r\nContent-Length: 1\r\nExpect: something-else", 417, VaultErrorCodes.BadRequest)]
    public void Ambiguous_or_unsupported_framing_is_refused(string head, int status, string code)
    {
        var e = Refused(() => Http1Connection.Check(Head(head), "x.test", 1024));

        Assert.Equal((status, code), (e.Status, e.Code));
    }

    [Fact]
    public void Expect_continue_close_and_the_port_form_of_Host_are_understood()
    {
        Http1Framing framing = Http1Connection.Check(
            Head("PUT / HTTP/1.1\r\nHost: X.Test:443\r\nContent-Length: 10\r\nExpect: 100-Continue\r\nConnection: keep-alive, close"), "x.test", 1024);

        Assert.Equal(new Http1Framing(10, ExpectContinue: true, Close: true), framing);
        Assert.False(Http1Connection.Check(Head("GET / HTTP/1.1\r\nHost: x.test\r\nExpect: 100-continue"), "x.test", 1024).ExpectContinue);
    }

    [Fact]
    public async Task Responses_get_a_fresh_Content_Length_and_unsafe_headers_are_dropped()
    {
        var output = new MemoryStream();
        var connection = new Http1Connection(output);

        await connection.WriteResponseAsync(
            200, "Fine\r\nInjected: yes", [new WireHeader("X-Ok", "1"), new WireHeader("X-Bad", "a\r\nInjected: yes"), new WireHeader("Bad Name", "x")],
            "body"u8.ToArray(), close: true, toHead: false, Ct);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nX-Ok: 1\r\nContent-Length: 4\r\nConnection: close\r\n\r\nbody",
            Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public async Task A_response_to_HEAD_has_no_body_and_no_Content_Length()
    {
        var output = new MemoryStream();
        var connection = new Http1Connection(output);

        await connection.WriteResponseAsync(200, "OK", [new WireHeader("X-Ok", "1")], "ignored"u8.ToArray(), close: false, toHead: true, Ct);
        await connection.WriteRefusalAsync(403, VaultErrorCodes.NetworkNotGranted, "No.", close: false, toHead: true, Ct);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nX-Ok: 1\r\n\r\n"
            + "HTTP/1.1 403 Forbidden\r\nX-Corvid-Vault-Error: network_not_granted\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n",
            Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public async Task A_body_is_read_as_it_arrives_and_the_announced_length_is_not_allocated_up_front()
    {
        // The body trickles in a few KB per read; the buffer doubles as it fills instead of asking for 300 KB at once.
        byte[] payload = RandomNumberGenerator.GetBytes(300_000);
        var stream = new TrickleStream(payload, maxPerRead: 7_000);
        var connection = new Http1Connection(stream);

        byte[] body = await connection.ReadBodyAsync(payload.Length, Ct);

        Assert.Equal(payload, body);
        Assert.True(stream.LargestRequest <= Http1Connection.InitialBodyBytes * 2, $"asked for {stream.LargestRequest} bytes in one read");

        // A 16 MB claim with one byte behind it: the first read asks for at most the initial buffer, then the close is a 400.
        var claim = new TrickleStream([1], maxPerRead: 1);
        var e = await Assert.ThrowsAsync<Http1Exception>(() => new Http1Connection(claim).ReadBodyAsync(16_000_000, Ct));
        Assert.Equal(400, e.Status);
        Assert.True(claim.LargestRequest <= Http1Connection.InitialBodyBytes, $"asked for {claim.LargestRequest} bytes in one read");
    }

    /// <summary>A read-only stream that hands out at most <c>maxPerRead</c> bytes per read and records the largest read asked for.</summary>
    private sealed class TrickleStream(byte[] data, int maxPerRead) : Stream
    {
        private int _position;

        public int LargestRequest { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            int n = Math.Min(Math.Min(buffer.Length, maxPerRead), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("api.github.com:443", "api.github.com", 443, false)]
    [InlineData("API.GitHub.com:443", "api.github.com", 443, false)]
    [InlineData("localhost:443", "localhost", 443, false)]
    [InlineData("bound.test:80", "bound.test", 80, false)]
    [InlineData("127.0.0.1:443", null, 443, true)]
    [InlineData("[::1]:443", null, 443, true)]
    [InlineData("127.1:443", null, 443, true)]
    [InlineData("0x7f.1:443", null, 443, true)]
    [InlineData("a.0x7f:443", "a.0x7f", 443, false)]
    [InlineData("a.b.123:443", null, 443, false)]
    [InlineData("under_score.test:443", null, 443, false)]
    [InlineData("-lead.test:443", null, 443, false)]
    [InlineData("a..b:443", null, 443, false)]
    [InlineData("bücher.de:443", null, 443, false)]
    public void Connect_targets_parse_to_DNS_names_only(string target, string? host, int port, bool ipLiteral)
    {
        Assert.Equal(new SandboxProxy.Authority(host, port, ipLiteral), SandboxProxy.ParseAuthority(target));
    }

    [Theory]
    [InlineData("bound.test")]
    [InlineData("bound.test:")]
    [InlineData("bound.test:99999x")]
    [InlineData(":443")]
    public void Connect_targets_without_a_port_are_refused(string target) => Assert.Null(SandboxProxy.ParseAuthority(target));

    [Theory]
    [InlineData(VaultErrorCodes.BadRequest, 400)]
    [InlineData(VaultErrorCodes.UpstreamError, 502)]
    [InlineData(VaultErrorCodes.ResponseTooLarge, 502)]
    [InlineData(VaultErrorCodes.HostNotBound, 403)]
    [InlineData(VaultErrorCodes.ApprovalRequired, 403)]
    [InlineData(VaultErrorCodes.NetworkNotGranted, 403)]
    public void Vault_errors_map_to_HTTP_statuses(string code, int status) => Assert.Equal(status, SandboxProxy.StatusFor(code));
}
