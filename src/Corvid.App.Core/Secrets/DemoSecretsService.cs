using System.Collections.Immutable;

namespace Corvid.App.Core.Secrets;

/// <summary>
/// The Secrets area in demo mode, consistent with the rest of the demo: Vault running, the claude slot's
/// <c>anthropic.key</c> bound to api.anthropic.com (used at 14:03:52 for egress request 7), <c>github.token</c> (whose
/// value the log scrubbed from <c>printenv</c> in cli-9f2e), the ntfy token cli-77b0 wrote a policy for, the Telegram and
/// Discord bot tokens behind the gateway's two running adapters and the notes MCP server's token. Two features wait: the
/// configured gemini model on <c>gemini.key</c> (Models lists it "configured, no key") and the gateway's Slack adapter
/// (off) on <c>slack.bot-token</c>. An unused <c>google.key</c> bound to Google's API host is there to bind gemini to.
/// <para>
/// Demo mode starts nothing: <see cref="StartSetAsync"/> opens no terminal and says so, and the next load reports the
/// secret as set, as though the person had run the command. Bindings change this object only.
/// </para>
/// </summary>
public sealed class DemoSecretsService : ISecretsService
{
    public const string GeminiConsumerId = "model:gemini";
    public const string SlackConsumerId = "gateway:slack";

    private readonly TimeProvider _time;
    private readonly List<SecretInfo> _secrets;
    private readonly List<SecretConsumer> _consumers;
    private readonly List<string> _pending = [];

    /// <param name="extra">More secrets in the catalogue, as Vault would list them (tests add names holding hidden characters).</param>
    public DemoSecretsService(TimeProvider time, IEnumerable<SecretInfo>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        MethodRule Auto(string method) => new(method, MethodPolicy.Auto);
        MethodRule Ask(string method) => new(method, MethodPolicy.Ask);
        _secrets =
        [
            new SecretInfo("anthropic.key")
            {
                Description = "Anthropic API key for the claude model",
                Hosts = ["api.anthropic.com"],
                Methods = [Auto("POST")],
                Toolsets = ["model-provider"],
                Role = "model-provider",
                BudgetUsed = 7, BudgetLimit = 300,
                Tainted = MethodPolicy.Auto,
                LastUsed = At(14, 3, 52),
            },
            new SecretInfo("github.token")
            {
                Description = "GitHub API for git and gh",
                Hosts = ["api.github.com", "uploads.github.com", "github.com"],
                Methods = [Auto("GET"), Ask("POST"), Ask("PATCH"), Ask("PUT"), new("DELETE", MethodPolicy.Confirm)],
                Toolsets = ["git", "github"],
                BudgetUsed = 18, BudgetLimit = 300,
                LastUsed = At(14, 2, 52),
            },
            new SecretInfo("google.key")
            {
                Description = "Google AI Studio key, imported from a .env file",
                Hosts = ["generativelanguage.googleapis.com"],
                Methods = [Auto("POST")],
                Toolsets = ["model-provider"],
                Role = "model-provider",
                BudgetLimit = 300,
            },
            new SecretInfo("ntfy.token")
            {
                Description = "ntfy topic token for job notifications",
                Hosts = ["ntfy.example.org"],
                Methods = [Auto("POST"), new("GET", MethodPolicy.Deny)],
                Toolsets = ["notify"],
                BudgetUsed = 1, BudgetLimit = 60,
                LastUsed = At(13, 5, 2),
            },
            new SecretInfo("telegram.bot-token")
            {
                Description = "Telegram bot for the gateway",
                Hosts = ["api.telegram.org"],
                Methods = [Auto("GET"), Auto("POST")],
                Toolsets = ["gateway"],
                BudgetUsed = 412, BudgetLimit = 2000, BudgetWindow = "day",
                LastUsed = At(14, 4, 40),
            },
            new SecretInfo("discord.bot-token")
            {
                Description = "Discord bot for the gateway",
                Hosts = ["discord.com", "gateway.discord.gg"],
                Methods = [Auto("GET"), Auto("POST")],
                Toolsets = ["gateway"],
                BudgetUsed = 96, BudgetLimit = 2000, BudgetWindow = "day",
                LastUsed = At(14, 1, 10),
            },
            new SecretInfo("notes.token")
            {
                Description = "the notes MCP server",
                Hosts = ["notes.example.org"],
                Methods = [Auto("GET"), Ask("POST")],
                Toolsets = ["mcp:notes"],
                BudgetUsed = 3, BudgetLimit = 120,
                LastUsed = At(8, 11, 58),
            },
            .. extra ?? [],
        ];
        _consumers =
        [
            new SecretConsumer("model:claude", ConsumerKind.Model, "claude", "generator escalation", "anthropic.key", "api.anthropic.com"),
            new SecretConsumer(GeminiConsumerId, ConsumerKind.Model, "gemini", "generator, summarizer", "gemini.key", "generativelanguage.googleapis.com"),
            new SecretConsumer("gateway:telegram", ConsumerKind.Gateway, "telegram", "gateway adapter", "telegram.bot-token", "api.telegram.org"),
            new SecretConsumer("gateway:discord", ConsumerKind.Gateway, "discord", "gateway adapter", "discord.bot-token", "discord.com"),
            new SecretConsumer(SlackConsumerId, ConsumerKind.Gateway, "slack", "gateway adapter, off", "slack.bot-token", "slack.com"),
            new SecretConsumer("mcp:notes", ConsumerKind.Mcp, "notes", "MCP server", "notes.token", "notes.example.org"),
            new SecretConsumer("tool:github", ConsumerKind.Tool, "git and gh", "shell toolset github", "github.token", "api.github.com"),
            new SecretConsumer("tool:notify", ConsumerKind.Tool, "notify", "job result delivery", "ntfy.token", "ntfy.example.org"),
        ];
    }

    public event EventHandler? Changed;

    /// <summary>The bindings written so far: which feature, to which name.</summary>
    public List<(string Consumer, string Secret)> Bound { get; } = [];

    /// <summary>Names a set was started for; the next load lists them as present.</summary>
    public IReadOnlyList<string> Pending => _pending;

    public Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        // A set started before this load has, in the demo, been finished in its terminal.
        foreach (var name in _pending)
        {
            if (_secrets.Any(secret => secret.Name == name))
                continue;
            var hosts = _consumers.Where(c => c.Secret == name).Select(c => c.Host).Distinct(StringComparer.Ordinal).ToImmutableArray();
            var model = _consumers.Any(c => c.Secret == name && c.Kind == ConsumerKind.Model);
            _secrets.Add(new SecretInfo(name)
            {
                Description = "set just now with corvid-vault set",
                Hosts = hosts,
                Methods = model ? [new("POST", MethodPolicy.Auto)] : [new("GET", MethodPolicy.Auto), new("POST", MethodPolicy.Ask)],
                Toolsets = model ? ["model-provider"] : [],
                Role = model ? "model-provider" : null,
                BudgetLimit = 300,
            });
        }

        _pending.Clear();
        var status = new VaultStatus(VaultState.Running)
        {
            Version = "corvid-vault 0.4.2",
            Socket = "$XDG_RUNTIME_DIR/corvid-vault/vault.sock",
            UsesToday = 63,
            AskedToday = 2,
            ConfirmedToday = 0,
            RefusedToday = 1,
        };
        return Task.FromResult(new SecretsSnapshot(status, [.. _secrets], [.. _consumers]));
    }

    public Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!SecretNames.IsValid(name))
            return Task.FromException<SetterLaunch>(new ArgumentException(SecretNames.Rule, nameof(name)));
        if (!_pending.Contains(name, StringComparer.Ordinal))
            _pending.Add(name);
        var command = SecretNames.SetCommand(name);
        return Task.FromResult(new SetterLaunch(false, command,
            "Demo mode opens no terminal. Run the command in one: Vault asks for the value there, and Corvid never sees it."));
    }

    public Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default)
    {
        var index = _consumers.FindIndex(c => c.Id == consumerId);
        if (index < 0)
            return Task.FromException(new KeyNotFoundException($"No feature {consumerId} is configured."));
        var consumer = _consumers[index];
        if (SafeText.HasHidden(secretName))
            return Task.FromException(new InvalidOperationException("That name holds hidden characters; it is not bound."));
        if (_secrets.FirstOrDefault(s => s.Name == secretName) is not { } secret)
            return Task.FromException(new InvalidOperationException($"Vault has no secret named {secretName}."));
        if (SecretBinding.Refusal(secret, consumer) is { } refusal)
            return Task.FromException(new InvalidOperationException(refusal));
        _consumers[index] = consumer with { Secret = secretName };
        Bound.Add((consumerId, secretName));
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private DateTimeOffset At(int hour, int minute, int second)
    {
        var zone = _time.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).Date;
        var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}

/// <summary>What a secret name may be, and the command that sets one.</summary>
public static class SecretNames
{
    public const string Rule = "A secret name is lower-case letters, digits, dots, dashes and underscores, starting with a letter, at most 64 characters.";

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name[0] is < 'a' or > 'z')
            return false;
        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'))
                return false;
        }

        return true;
    }

    /// <summary>"corvid-vault set gemini.key"; the name revealed, so a hidden character in it is seen before it is run.</summary>
    public static string SetCommand(string name) => $"corvid-vault set {SafeText.Reveal(name)}";
}

/// <summary>Whether a secret can serve a feature: its hosts and its role.</summary>
public static class SecretBinding
{
    /// <summary>Why the secret cannot serve the feature, or null when it can.</summary>
    public static string? Refusal(SecretInfo secret, SecretConsumer consumer)
    {
        if (!secret.Hosts.Contains(consumer.Host, StringComparer.OrdinalIgnoreCase))
            return $"{SafeText.Reveal(secret.Name)} is not bound to {SafeText.Reveal(consumer.Host)}; Vault would refuse to send it there.";
        if (secret.Role == "model-provider" && consumer.Kind != ConsumerKind.Model)
            return $"{SafeText.Reveal(secret.Name)} has the model-provider role: only a model slot may use it.";
        return null;
    }
}
