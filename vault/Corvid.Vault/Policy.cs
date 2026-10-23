using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Vault.Contracts;

namespace Corvid.Vault;

/// <summary>What happens when a secret is used: silently, with Corvid's approval, with out-of-band confirmation, or never.</summary>
internal enum Rule
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("ask")] Ask,
    [JsonStringEnumMemberName("confirm")] Confirm,
    [JsonStringEnumMemberName("deny")] Deny,
}

/// <summary><c>vault.json</c>. Owned by Vault's user; Corvid has no way to write it.</summary>
internal sealed class PolicyDocument
{
    /// <summary>Allow <c>http://</c> to loopback addresses. For tests and local services only.</summary>
    public bool AllowPlainHttpLoopback { get; set; }

    /// <summary>Peer uids allowed to connect. Empty means Vault's own uid (the default tier).</summary>
    public List<uint> AllowedUids { get; set; } = [];

    public int PlaceholderLifetimeMinutes { get; set; } = 12 * 60;

    public int MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    public int ScrubRequestsPerMinute { get; set; } = 600;

    /// <summary>Upper bound on a sandbox proxy's lifetime (V-003a); a sandbox may ask for less.</summary>
    public int SandboxLifetimeMinutes { get; set; } = 12 * 60;

    /// <summary>Live sandbox proxies at once; one more is refused with <c>sandbox_limit</c>.</summary>
    public int MaxSandboxes { get; set; } = 64;

    public Dictionary<string, SecretPolicy> Secrets { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class SecretPolicy
{
    /// <summary>Hosts the value may be sent to: exact names, or <c>*.example.com</c> for subdomains.</summary>
    public List<string> Hosts { get; set; } = [];

    /// <summary>Rule per HTTP method. When absent: GET and HEAD are auto, everything else ask. When present, unlisted methods are denied.</summary>
    public Dictionary<string, Rule>? Methods { get; set; }

    /// <summary>If non-empty, the request path must match one of these globs (<c>*</c> matches anything).</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Paths that are always denied.</summary>
    public List<string> DenyPaths { get; set; } = [];

    /// <summary>Toolsets allowed to use the secret. Empty means any.</summary>
    public List<string> Toolsets { get; set; } = [];

    public int? RequestsPerHour { get; set; }

    /// <summary>The least strict rule that applies when Corvid's turn holds untrusted content.</summary>
    public Rule Tainted { get; set; } = Rule.Ask;

    /// <summary>Allow the value in the URL path or query (Telegram-style bot APIs). Off by default: headers only.</summary>
    public bool AllowInUrl { get; set; }
}

internal readonly record struct PolicyResult(bool Allowed, string Code, string Message)
{
    public static PolicyResult Allow(Rule rule) => new(true, rule.ToString().ToLowerInvariant(), "allowed");

    public static PolicyResult Refuse(string code, string message) => new(false, code, message);
}

internal static class PolicyEvaluator
{
    public static PolicyResult Evaluate(PolicyDocument document, string secret, string method, Uri uri, ForwardRequest request)
    {
        if (!document.Secrets.TryGetValue(secret, out SecretPolicy? policy) || policy.Hosts.Count == 0)
        {
            return PolicyResult.Refuse(VaultErrorCodes.HostNotBound, $"'{secret}' is not bound to any host in vault.json.");
        }

        bool secure = uri.Scheme == Uri.UriSchemeHttps || (document.AllowPlainHttpLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secure)
        {
            return PolicyResult.Refuse(VaultErrorCodes.InsecureTransport, $"'{secret}' is only sent over HTTPS.");
        }

        if (!policy.Hosts.Exists(h => HostMatches(h, uri.IdnHost)))
        {
            return PolicyResult.Refuse(VaultErrorCodes.HostNotBound, $"'{secret}' may not be sent to {uri.IdnHost}.");
        }

        if (policy.Toolsets.Count > 0 && (request.Toolset is null || !policy.Toolsets.Contains(request.Toolset, StringComparer.Ordinal)))
        {
            return PolicyResult.Refuse(VaultErrorCodes.ToolsetNotAllowed, $"'{secret}' is not available to toolset '{request.Toolset ?? "(none)"}'.");
        }

        string path = uri.AbsolutePath;
        if (policy.DenyPaths.Exists(p => Glob.Matches(p, path)))
        {
            return PolicyResult.Refuse(VaultErrorCodes.Denied, $"'{secret}' may not be used for {path}.");
        }

        if (policy.Paths.Count > 0 && !policy.Paths.Exists(p => Glob.Matches(p, path)))
        {
            return PolicyResult.Refuse(VaultErrorCodes.Denied, $"'{secret}' may not be used for {path}.");
        }

        Rule rule = RuleFor(policy, method);
        if (request.Tainted && rule < policy.Tainted)
        {
            rule = policy.Tainted;
        }

        return rule switch
        {
            Rule.Auto => PolicyResult.Allow(rule),
            Rule.Ask when request.Approved => PolicyResult.Allow(rule),
            Rule.Ask => PolicyResult.Refuse(VaultErrorCodes.ApprovalRequired, $"{method} with '{secret}' needs the user's approval."),
            Rule.Confirm => PolicyResult.Refuse(
                VaultErrorCodes.ConfirmUnavailable, $"{method} with '{secret}' needs out-of-band confirmation, which this Vault version does not provide yet."),
            _ => PolicyResult.Refuse(VaultErrorCodes.Denied, $"{method} with '{secret}' is denied."),
        };
    }

    private static Rule RuleFor(SecretPolicy policy, string method)
    {
        if (policy.Methods is null)
        {
            return method is "GET" or "HEAD" ? Rule.Auto : Rule.Ask;
        }

        foreach (var (name, rule) in policy.Methods)
        {
            if (string.Equals(name, method, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return Rule.Deny;
    }

    internal static bool HostMatches(string pattern, string host)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            string suffix = pattern[1..];
            return host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class Glob
{
    /// <summary>Matches <paramref name="text"/> against a pattern where <c>*</c> matches any run of characters.</summary>
    public static bool Matches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

/// <summary>Reloads <c>vault.json</c> when it changes. A missing file means no secret is usable.</summary>
internal sealed class PolicySource(string path)
{
    private readonly Lock _gate = new();
    private PolicyDocument _current = new();
    private DateTime _loadedWriteTime = DateTime.MinValue;

    public PolicyDocument Current
    {
        get
        {
            lock (_gate)
            {
                DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                if (writeTime != _loadedWriteTime)
                {
                    _current = writeTime == default
                        ? new PolicyDocument()
                        : JsonSerializer.Deserialize(File.ReadAllBytes(path), VaultInternalJsonContext.Default.PolicyDocument) ?? new PolicyDocument();
                    _loadedWriteTime = writeTime;
                }

                return _current;
            }
        }
    }
}

/// <summary>Per-secret sliding-hour request budgets.</summary>
internal sealed class Budgets(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _uses = new(StringComparer.Ordinal);

    public bool TryConsume(string secret, int? perHour)
    {
        if (perHour is null)
        {
            return true;
        }

        Queue<DateTimeOffset> uses = _uses.GetOrAdd(secret, _ => new Queue<DateTimeOffset>());
        lock (uses)
        {
            DateTimeOffset now = time.GetUtcNow();
            while (uses.Count > 0 && now - uses.Peek() > TimeSpan.FromHours(1))
            {
                uses.Dequeue();
            }

            if (uses.Count >= perHour.Value)
            {
                return false;
            }

            uses.Enqueue(now);
            return true;
        }
    }
}
