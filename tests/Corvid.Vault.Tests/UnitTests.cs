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
