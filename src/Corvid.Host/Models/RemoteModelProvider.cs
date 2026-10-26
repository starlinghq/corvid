using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Corvid.Host.Configuration;
using Corvid.Models;
using Corvid.Models.Anthropic;
using Corvid.Models.OpenAICompat;
using Corvid.Models.Remote;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Corvid.Host.Models;

/// <summary>
/// Remote models from <c>Corvid:Models:Remote</c> (C-015, plan §4.1): local servers and cloud providers. A model with a key
/// reaches its service only through Vault (plan §10.5): when Vault is not running, or holds no such secret bound to the
/// model's host, the model is <see cref="ModelReadiness.Unavailable"/>, never keyed from anywhere else. Entries that do not
/// make sense are left out of the catalogue and reported by <see cref="Problems"/> (the options validator lists them).
/// </summary>
public sealed class RemoteModelProvider : IModelProvider
{
    public const string ProviderName = "remote";

    private static readonly string[] ChatSlots = [ModelSlots.Generator, ModelSlots.Summarizer, ModelSlots.Vision];
    private static readonly TimeSpan VaultTimeout = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _socketPath;
    private readonly HttpMessageHandler? _network;
    private readonly Lock _gate = new();
    private VaultAccess? _vault;

    public RemoteModelProvider(IConfiguration configuration)
        : this(configuration, network: null)
    {
    }

    internal RemoteModelProvider(IConfiguration configuration, HttpMessageHandler? network)
        : this(configuration.GetSection($"{CorvidOptions.Section}:Models:Remote").Get<Dictionary<string, RemoteModelSettings>>() ?? [],
            configuration[$"{CorvidOptions.Section}:Vault:SocketPath"], network)
    {
    }

    /// <param name="network">The direct route for local servers; tests pass a fake. Cloud models never use it.</param>
    public RemoteModelProvider(IReadOnlyDictionary<string, RemoteModelSettings> models, string? vaultSocket, HttpMessageHandler? network = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        _socketPath = vaultSocket is { Length: > 0 } socket ? CorvidPaths.ExpandHome(socket) : DefaultSocket();
        _network = network;
        var problems = new List<string>();
        foreach (var (name, settings) in models.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var errors = Validate(name, settings).ToList();
            problems.AddRange(errors);
            if (errors.Count == 0)
                _entries[name] = Describe(name, settings);
        }
        Problems = [.. problems];
        Models = [.. _entries.Values.Select(entry => entry.Descriptor)];
    }

    public string Name => ProviderName;

    public IReadOnlyList<ModelDescriptor> Models { get; }

    /// <summary>Configuration errors, one line each, keyed by <c>Corvid:Models:Remote:&lt;name&gt;</c>.</summary>
    public ImmutableArray<string> Problems { get; }

    /// <summary>The settings of a remote model by its profile id, for the egress policy.</summary>
    public bool TryGetSettings(string profileId, out RemoteModelSettings settings)
    {
        var entry = _entries.Values.FirstOrDefault(item => item.Descriptor.Profile.Id == profileId);
        settings = entry?.Settings!;
        return entry is not null;
    }

    public ModelAvailability Check(string model, bool thorough = false)
    {
        var entry = Find(model);
        if (entry.Secret is not { } secret)
            return Ready(entry, $"{entry.Descriptor.Name}: {entry.Kind} at {entry.Endpoint} (no key; not checked over the network).");
        if (_socketPath is null || !File.Exists(_socketPath))
            return Unavailable(entry, $"{entry.Descriptor.Name} authenticates through Vault, which is not running" +
                (_socketPath is null ? " (XDG_RUNTIME_DIR is unset and Corvid:Vault:SocketPath is not configured)." : $" (no socket at {_socketPath}).") +
                " Cloud models are unavailable without Vault; Corvid never reads keys from the environment.");
        IReadOnlyList<CatalogEntry> catalogue;
        try
        {
            using var timeout = new CancellationTokenSource(VaultTimeout);
            catalogue = Task.Run(() => new VaultClient("corvid-check", _socketPath).GetCatalogAsync(timeout.Token)).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is SocketException or IOException or VaultException or OperationCanceledException or InvalidDataException)
        {
            return Unavailable(entry, $"{entry.Descriptor.Name}: Vault at {_socketPath} did not answer ({exception.Message}).");
        }
        var stored = catalogue.FirstOrDefault(item => item.Name == secret.Name);
        if (stored is null)
            return Unavailable(entry, $"{entry.Descriptor.Name}: Vault has no secret '{secret.Name}'. Add it with `corvid-vault set {secret.Name}` " +
                $"and bind it to {entry.Endpoint.Host} in Vault's policy.");
        if (!stored.Hosts.Any(host => HostMatches(host, entry.Endpoint.Host)))
            return Unavailable(entry, $"{entry.Descriptor.Name}: Vault's secret '{secret.Name}' is not bound to {entry.Endpoint.Host}; " +
                "add the host to its policy.");
        return Ready(entry, $"{entry.Descriptor.Name}: {entry.Kind} at {entry.Endpoint.Host}, key '{secret.Name}' in Vault.");
    }

    public ModelBinding Load(string model, ModelLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entry = Find(model);
        VaultAccess? vault = null;
        if (entry.Secret is not null)
        {
            if (_socketPath is null || !File.Exists(_socketPath))
                throw new ModelUnavailableException(Check(model));
            lock (_gate)
                vault = _vault ??= VaultAccess.Create(_socketPath);
        }
        var profile = entry.Descriptor.Profile;
        var settings = entry.Settings;
        IChatClient chat = entry.Kind == Anthropic
            ? AnthropicBindings.CreateChatClient(profile,
                new AnthropicModelOptions(settings.Model!, entry.Endpoint.ToString().TrimEnd('/'), ForcedToolChoice: settings.ForcedToolChoice ?? false),
                entry.Secret!.Value, vault, _network)
            : OpenAICompatBindings.CreateChatClient(profile,
                new OpenAICompatModelOptions(settings.Model!, entry.Endpoint, settings.ForcedToolChoice ?? true), entry.Secret, vault, _network);
        return new ModelBinding(profile, chat);
    }

    public Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"'{model}' is a remote model; there is nothing to download.");

    // ---- configuration ----

    private const string Anthropic = "anthropic";
    private const string OpenAI = "openai";
    private const string OpenRouter = "openrouter";
    private const string Gemini = "gemini";
    private const string Compatible = "openai-compatible";

    private static readonly string[] Kinds = [Anthropic, OpenAI, OpenRouter, Gemini, Compatible];

    /// <summary>
    /// What is wrong with remote model settings, one line each, as <see cref="Problems"/> words them for the configuration
    /// this provider was built from: for any other configuration (the file a writer is about to save).
    /// </summary>
    public static IEnumerable<string> ProblemsOf(IReadOnlyDictionary<string, RemoteModelSettings> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => Validate(pair.Key, pair.Value)).ToList();
    }

    /// <summary>
    /// The models remote model settings describe, as a provider built from them would list them (the entries without
    /// problems): for any other configuration than the one this provider was built from (the file a writer is about to save).
    /// </summary>
    public static IReadOnlyList<ModelDescriptor> DescriptorsOf(IReadOnlyDictionary<string, RemoteModelSettings> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        return [.. models.OrderBy(pair => pair.Key, StringComparer.Ordinal).Where(pair => !Validate(pair.Key, pair.Value).Any())
            .Select(pair => Describe(pair.Key, pair.Value).Descriptor)];
    }

    private static IEnumerable<string> Validate(string name, RemoteModelSettings? settings)
    {
        var key = $"Corvid:Models:Remote:{name}";
        if (settings is null)
        {
            yield return $"{key}: empty.";
            yield break;
        }
        var kind = settings.Provider?.Trim().ToLowerInvariant();
        if (kind is null || !Kinds.Contains(kind))
        {
            yield return $"{key}:Provider must be one of {string.Join(", ", Kinds)}.";
            yield break;
        }
        if (string.IsNullOrWhiteSpace(settings.Model))
            yield return $"{key}:Model must name the provider's model id.";
        if (settings.Privacy is { } privacy && (kind != Compatible || privacy == PrivacyClass.Local))
            yield return $"{key}:Privacy can be set only for openai-compatible, to LocalServer or Cloud.";
        var cloud = Privacy(kind, settings) == PrivacyClass.Cloud;
        Uri? endpoint = null;
        if (settings.Endpoint is { Length: > 0 } text)
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https"))
                yield return $"{key}:Endpoint must be an absolute http or https URI, not '{text}'.";
        }
        else if (kind == Compatible)
        {
            yield return $"{key}:Endpoint is required for openai-compatible.";
        }
        if (endpoint is not null && cloud && endpoint.Scheme != "https")
            yield return $"{key}:Endpoint must use https for a cloud model.";
        // A local server is not gated by default, so the class must be true: content sent there must stay on the user's machines.
        if (endpoint is not null && !cloud && !IsLocalHost(endpoint.Host))
            yield return $"{key}:Endpoint {endpoint.Host} is not a loopback or private address; a remote host is a cloud model (Privacy: Cloud, with a Secret).";
        if (settings.Secret is { Length: > 0 } secret)
        {
            if (!Placeholders.IsValidSecretName(secret))
                yield return $"{key}:Secret '{secret}' is not a valid Vault secret name (lower-case letters, digits, '.', '-').";
            // Vault opens TLS to the bound host itself and refuses plain HTTP, so a keyed endpoint must be https.
            if (endpoint is not null && endpoint.Scheme != "https")
                yield return $"{key}:Endpoint must use https when a Secret is set: Vault sends keys only over verified TLS.";
        }
        else if (cloud)
        {
            yield return $"{key}:Secret must name the Vault secret holding the key: cloud models authenticate only through Vault.";
        }
        foreach (var slot in settings.Slots.Where(slot => !ChatSlots.Contains(slot, StringComparer.Ordinal)))
            yield return $"{key}:Slots: '{slot}' is not a chat slot ({string.Join(", ", ChatSlots)}).";
        if (settings.ContextWindow <= 0)
            yield return $"{key}:ContextWindow must be positive.";
        if (settings.AcceptedContext is { } accepted && (accepted <= 0 || accepted > settings.ContextWindow))
            yield return $"{key}:AcceptedContext must be positive and at most ContextWindow.";
        if (settings.InputCost < 0 || settings.OutputCost < 0 || settings.CachedInputCost < 0)
            yield return $"{key}: costs cannot be negative.";
        if (settings.Egress?.Threshold is { } threshold && threshold is < 0 or > 1)
            yield return $"{key}:Egress:Threshold must be between 0 and 1.";
        if (!Enum.IsDefined(settings.ToolCalls) || !Enum.IsDefined(settings.StructuredOutput)
            || settings.Caching is { } caching && !Enum.IsDefined(caching))
            yield return $"{key}: unknown ToolCalls, StructuredOutput or Caching value.";
    }

    private static PrivacyClass Privacy(string kind, RemoteModelSettings settings) =>
        kind == Compatible ? settings.Privacy ?? PrivacyClass.LocalServer : PrivacyClass.Cloud;

    private static Entry Describe(string name, RemoteModelSettings settings)
    {
        var kind = settings.Provider!.Trim().ToLowerInvariant();
        var endpoint = settings.Endpoint is { Length: > 0 } text
            ? new Uri(text)
            : kind == Anthropic
                ? new Uri(AnthropicModelOptions.DefaultBaseUrl)
                : OpenAICompatModelOptions.DefaultEndpoint(kind switch
                {
                    OpenAI => OpenAICompatService.OpenAI,
                    OpenRouter => OpenAICompatService.OpenRouter,
                    _ => OpenAICompatService.Gemini,
                })!;
        var privacy = Privacy(kind, settings);
        var caching = settings.Caching ?? (kind == Anthropic ? PromptCachingStyle.Breakpoints : PromptCachingStyle.Automatic);
        var profile = new ModelProfile($"{name}@{kind}", settings.ContextWindow, settings.AcceptedContext ?? settings.ContextWindow,
            settings.ToolCalls, settings.StructuredOutput, caching, new ModelCost(settings.InputCost, settings.OutputCost, settings.CachedInputCost),
            privacy, endpoint.Authority).Validate();
        var slots = settings.Slots.Count == 0 ? [ModelSlots.Generator, ModelSlots.Summarizer] : settings.Slots.Distinct(StringComparer.Ordinal).ToImmutableArray();
        // K-8 (A6): a model that says it reads images, or serves the vision slot, is sent images.
        if (settings.Vision || slots.Contains(ModelSlots.Vision))
            profile = profile with { Vision = true };
        SecretRef? secret = settings.Secret is { Length: > 0 } secretName ? new SecretRef(secretName) : null;
        return new Entry(new ModelDescriptor(name, slots, profile, ProviderName), settings, kind, endpoint, secret);
    }

    /// <summary>Loopback, private and link-local addresses, <c>localhost</c>, single-label names and local-only suffixes.</summary>
    internal static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            if (IPAddress.IsLoopback(address))
                return true;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }
        return !host.Contains('.', StringComparison.Ordinal)
            || new[] { ".local", ".lan", ".internal", ".home.arpa", ".localhost" }.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HostMatches(string pattern, string host) =>
        string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase)
        || pattern.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);

    private static string? DefaultSocket()
    {
        try
        {
            return VaultClient.DefaultSocketPath();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Entry Find(string model) =>
        _entries.TryGetValue(model, out var entry) ? entry : throw new ArgumentException($"No remote model is named '{model}'.", nameof(model));

    private static ModelAvailability Ready(Entry entry, string detail) => new(entry.Descriptor, ModelReadiness.Ready, detail);

    private static ModelAvailability Unavailable(Entry entry, string detail) => new(entry.Descriptor, ModelReadiness.Unavailable, detail);

    private sealed record Entry(ModelDescriptor Descriptor, RemoteModelSettings Settings, string Kind, Uri Endpoint, SecretRef? Secret);
}
