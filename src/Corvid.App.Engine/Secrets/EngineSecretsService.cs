using System.Collections.Immutable;
using System.Globalization;
using System.Net.Sockets;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Secrets;
using Corvid.App.Engine.Ops;
using Corvid.Gateway.Discord;
using Corvid.Gateway.Slack;
using Corvid.Gateway.Telegram;
using Corvid.Host.Configuration;
using Corvid.Models.Anthropic;
using Corvid.Models.OpenAICompat;
using Corvid.Models.Remote;
using Corvid.Vault.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.App.Engine.Secrets;

/// <summary>
/// The Secrets page over what Vault will say (docs/plan/ui.md §4.11): its catalogue (each secret's name, description, bound
/// hosts and toolsets; never a value) and its audit summary, over its socket, beside the features <c>corvid.json</c>
/// configures to use a secret by name (cloud models, gateway adapters, MCP servers), each with the host it sends the secret
/// to. A binding is written to <c>corvid.json</c> through the config writer and applies from the next start.
/// <para>
/// What Vault does not say yet (V-n): a secret's method policy, role, budget, tainted-turn policy and last use
/// (<see cref="SecretInfo.PolicyReported"/> is false, and the page says "not reported"); its version; daily counts (its
/// audit summary counts since its log began, shown as <see cref="VaultStatus.AuditSummary"/>); and a pending out-of-band
/// confirm (V-006). Setting a value never passes through this process: until the Vault helper exists (U-6), the page gives
/// the exact <c>corvid-vault set</c> command to run in a terminal, and opens nothing itself.
/// </para>
/// <para>It reads the host's options, not the runtime's, so it works while the models are missing and the runtime is down.</para>
/// </summary>
public sealed class EngineSecretsService : ISecretsService, IDisposable
{
    /// <summary>The session name this window gives Vault.</summary>
    public const string VaultSession = "corvid-app";

    /// <summary>What a set says until the Vault helper (U-6) exists.</summary>
    public const string SetNote =
        "Run the command in a terminal: Vault asks for the value there, and Corvid never sees it. Opening a terminal or the Vault helper from here comes with U-6.";

    public const string NotReportedVersion = "not reported by Vault yet (V-n)";

    /// <summary>The audit line while Vault does not answer: no counts, rather than zeros it never said.</summary>
    public const string NoCounts = "Vault does not answer, so it gives no counts.";

    private static readonly TimeSpan VaultTimeout = TimeSpan.FromSeconds(5);

    private readonly AppEngine _engine;
    private readonly ChangeSignal _changed;

    public EngineSecretsService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? Changed;

    public async Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var options = Options();
        var consumers = Consumers(options);
        string socket;
        try
        {
            socket = Socket(options);
        }
        catch (InvalidOperationException exception)
        {
            return new SecretsSnapshot(new VaultStatus(VaultState.Absent) { Socket = exception.Message, AuditSummary = NoCounts }, [], consumers);
        }

        var client = new VaultClient(VaultSession, socket);
        IReadOnlyList<Corvid.Vault.Contracts.CatalogEntry> catalogue;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(VaultTimeout);
            try
            {
                catalogue = await client.GetCatalogAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException
                or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new SecretsSnapshot(new VaultStatus(VaultState.Absent) { Socket = socket, AuditSummary = NoCounts }, [], consumers);
            }
            catch (VaultException exception)
            {
                throw new InvalidOperationException($"Vault at {socket} refused its catalogue: {exception.Message}", exception);
            }
        }

        string summary;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(VaultTimeout);
            try
            {
                summary = Summary(await client.GetAuditSummaryAsync(timeout.Token).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is VaultException or SocketException or IOException
                or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                summary = $"Vault gave no audit summary ({exception.Message}).";
            }
        }

        var secrets = catalogue.Select(entry => new SecretInfo(entry.Name)
        {
            Description = entry.Description,
            Hosts = [.. entry.Hosts],
            Toolsets = [.. entry.Toolsets],
            // A secret for model providers' toolset serves a model slot only (SecretBinding.Refusal), as Vault's policy has it.
            Role = entry.Toolsets.Contains(ModelHttp.Toolset, StringComparer.Ordinal) ? ModelHttp.Toolset : null,
            PolicyReported = false,
        }).OrderBy(secret => secret.Name, StringComparer.Ordinal).ToImmutableArray();
        var status = new VaultStatus(VaultState.Running) { Version = NotReportedVersion, Socket = socket, AuditSummary = summary };
        return new SecretsSnapshot(status, secrets, consumers);
    }

    public Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!SecretNames.IsValid(name))
            return Task.FromException<SetterLaunch>(new ArgumentException(SecretNames.Rule, nameof(name)));
        return Task.FromResult(new SetterLaunch(false, SecretNames.SetCommand(name), SetNote));
    }

    public async Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentNullException.ThrowIfNull(secretName);
        if (SafeText.HasHidden(secretName))
            throw new InvalidOperationException("That name holds hidden characters; it is not bound.");
        if (!SecretNames.IsValid(secretName))
            throw new InvalidOperationException(SecretNames.Rule);
        var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var consumer = snapshot.Consumers.FirstOrDefault(candidate => candidate.Id == consumerId)
            ?? throw new KeyNotFoundException($"No feature {consumerId} is configured.");
        if (snapshot.Vault.State != VaultState.Running)
            throw new InvalidOperationException("Vault does not answer, so it cannot say whether it holds that name; start it and try again.");
        var secret = snapshot.Secrets.FirstOrDefault(candidate => candidate.Name == secretName)
            ?? throw new InvalidOperationException($"Vault has no secret named {secretName}.");
        if (SecretBinding.Refusal(secret, consumer) is { } refusal)
            throw new InvalidOperationException(refusal);
        var writer = _engine.Services.GetRequiredService<CorvidConfigWriter>();
        await Task.Run(() =>
        {
            var result = writer.Apply(ConfigEdit.Set(Key(consumer), secretName));
            if (!result.Written)
                throw new InvalidOperationException("corvid.json was not changed: " + result.FailureText);
            if (!result.ShadowedKeys.IsEmpty)
                throw new InvalidOperationException(
                    $"Written to corvid.json, but {string.Join(", ", result.ShadowedKeys)} is set for this run by an environment variable or --set, which wins over the file.");
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
    }

    public void Dispose() => _changed.Dispose();

    private CorvidOptions Options()
    {
        try
        {
            return _engine.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        }
        catch (OptionsValidationException exception)
        {
            throw new ServiceUnavailableException("Corvid cannot read its configuration: " + string.Join(" ", exception.Failures));
        }
    }

    /// <summary>Vault's socket as the runtime reaches it: the configured path, or <c>$XDG_RUNTIME_DIR/corvid-vault/vault.sock</c>.</summary>
    internal static string Socket(CorvidOptions options) =>
        options.Vault.SocketPath is { Length: > 0 } configured ? CorvidPaths.ExpandHome(configured) : VaultClient.DefaultSocketPath();

    /// <summary>The audit summary's counts (<c>op.decision</c>, since its log began) in a sentence.</summary>
    internal static string Summary(IReadOnlyDictionary<string, int> counts)
    {
        int Sum(Func<string, bool> match) => counts.Where(pair => match(pair.Key)).Sum(pair => pair.Value);
        var sent = Sum(key => key is "forward.allowed" or "sandbox_forward.allowed");
        var minted = Sum(key => key == "placeholder.allowed");
        var refused = Sum(key => key.EndsWith(".refused", StringComparison.Ordinal));
        var failed = Sum(key => key.EndsWith(".error", StringComparison.Ordinal));
        return string.Create(CultureInfo.InvariantCulture,
            $"since Vault's audit log began: {sent} requests sent with a secret · {minted} placeholders minted · {refused} refused · {failed} failed. Vault does not count by day yet (V-n).");
    }

    /// <summary>The configuration key a binding writes for a feature.</summary>
    internal static string Key(SecretConsumer consumer)
    {
        var parts = consumer.Id.Split(':');
        return parts switch
        {
            ["model", var name] => $"Corvid:Models:Remote:{name}:Secret",
            ["gateway", var name] => $"Corvid:Gateway:Adapters:{name}:Secret",
            ["gateway", var name, "app"] => $"Corvid:Gateway:Adapters:{name}:AppSecret",
            ["mcp", var name] => $"Corvid:Mcp:Servers:{name}:Secret",
            _ => throw new KeyNotFoundException($"No feature {consumer.Id} is configured."),
        };
    }

    /// <summary>
    /// Every feature corvid.json gives a secret name, with the host it sends the secret to: the remote models (their
    /// endpoint, or the provider's), the enabled gateway adapters (their platform's API) and the HTTP MCP servers.
    /// </summary>
    internal static ImmutableArray<SecretConsumer> Consumers(CorvidOptions options)
    {
        var consumers = ImmutableArray.CreateBuilder<SecretConsumer>();
        foreach (var (name, settings) in options.Models.Remote.Where(pair => pair.Value is { Secret.Length: > 0 }).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var slots = settings.Slots.Count == 0 ? "generator, summarizer" : string.Join(", ", settings.Slots);
            consumers.Add(new SecretConsumer($"model:{name}", ConsumerKind.Model, name, slots, settings.Secret!, ModelHost(settings)));
        }
        foreach (var (key, settings) in options.Gateway.Adapters.Where(pair => pair.Value is { Enabled: true }).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = key.ToLowerInvariant();
            var host = GatewayHost(name);
            var detail = "gateway adapter";
            if (settings.Secret is { Length: > 0 } secret)
                consumers.Add(new SecretConsumer($"gateway:{name}", ConsumerKind.Gateway, name, detail, secret, host));
            if (name == "slack" && settings.AppSecret is { Length: > 0 } app)
                consumers.Add(new SecretConsumer($"gateway:{name}:app", ConsumerKind.Gateway, name + " (Socket Mode)", "gateway adapter's app-level token", app, host));
        }
        foreach (var (name, settings) in options.Mcp.Servers.Where(pair => pair.Value is { Enabled: true, Secret.Length: > 0 }).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var host = Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) ? url.Host : "";
            consumers.Add(new SecretConsumer($"mcp:{name}", ConsumerKind.Mcp, name, "MCP server", settings.Secret!, host));
        }
        return consumers.ToImmutable();
    }

    private static string ModelHost(RemoteModelSettings settings)
    {
        if (settings.Endpoint is { Length: > 0 } endpoint)
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "";
        var url = settings.Provider?.Trim().ToLowerInvariant() switch
        {
            "anthropic" => new Uri(AnthropicModelOptions.DefaultBaseUrl),
            "openai" => OpenAICompatModelOptions.DefaultEndpoint(OpenAICompatService.OpenAI),
            "openrouter" => OpenAICompatModelOptions.DefaultEndpoint(OpenAICompatService.OpenRouter),
            "gemini" => OpenAICompatModelOptions.DefaultEndpoint(OpenAICompatService.Gemini),
            _ => null,
        };
        return url?.Host ?? "";
    }

    private static string GatewayHost(string adapter)
    {
        var placeholder = new SecretRef("app.placeholder");
        return adapter switch
        {
            "telegram" => new TelegramOptions(placeholder).ApiBase.Host,
            "discord" => new DiscordOptions(placeholder).ApiBase.Host,
            "slack" => new SlackOptions(placeholder, placeholder).ApiBase.Host,
            _ => "",
        };
    }
}
