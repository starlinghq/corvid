using System.Collections.Immutable;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Gateway;
using Corvid.Gateway.Discord;
using Corvid.Gateway.Slack;
using Corvid.Gateway.Telegram;
using Corvid.Host.Commands;
using Corvid.Host.Configuration;
using Corvid.Models;
using Corvid.Tools;
using Corvid.Vault.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Composition;

/// <summary>
/// Composes the gateway (C-020) over a running <see cref="CorvidRuntime"/>: the enabled adapters from configuration (or
/// registrations supplied in DI, which win by name), the pairing store beside the event log, the delivery ledger in it,
/// and the agent bridge that reaches sessions, the shared slash-command registry and approvals.
/// </summary>
public static class CorvidGateway
{
    /// <summary>The adapters Corvid ships.</summary>
    public static IReadOnlySet<string> Platforms { get; } = new HashSet<string>(["telegram", "discord", "slack"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The enabled adapters' names, lower case.</summary>
    public static IReadOnlyList<string> Enabled(CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [.. options.Gateway.Adapters.Where(pair => pair.Value is { Enabled: true }).Select(pair => pair.Key.ToLowerInvariant()).Order(StringComparer.Ordinal)];
    }

    /// <summary>The MCP server an MCP tool name (<c>server__tool</c>) belongs to, or null for any other name.</summary>
    public static string? McpServerOf(string tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var separator = tool.IndexOf("__", StringComparison.Ordinal);
        return separator > 0 ? tool[..separator] : null;
    }

    /// <summary>
    /// Guest tools (C-020) the gateway refuses to start with, by what the runtime's registry says of them: a name that is
    /// not in it and is not an MCP server's (whose tools are listed in the background; the validator has already checked
    /// that the server is configured), so a typo is not silently a tool nobody gets; and a tool whose risk metadata
    /// declares <see cref="SideEffects.Memory"/>, which a guest's turn never offers (the owner's memory and its review queue
    /// are not a guest's to write), so naming one is refused rather than silently dropped.
    /// </summary>
    public static IReadOnlyList<string> GuestToolProblems(CorvidOptions options, IEnumerable<ToolDescriptor> registered)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registered);
        var known = Known(registered);
        var problems = new List<string>();
        foreach (var (name, settings) in options.Gateway.Adapters.Where(pair => pair.Value is { Enabled: true }).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            foreach (var tool in settings.GuestTools)
                if (GuestToolProblem(tool, known) is { } problem)
                    problems.Add($"Corvid:Gateway:Adapters:{name}:GuestTools: {problem}");
        return problems;
    }

    /// <summary>
    /// What is wrong with a guest tool's name before any registry is read (the configuration's check, and a guest's own
    /// allowlist's): its shape; <c>cron_create</c>, since a job's run is a turn of its own with the whole toolset (no guest
    /// scope) that reports back to the chat, so a guest's job would read for the guest what the guest's own turn may not; and
    /// an MCP tool whose server is not configured. Null when it may be one.
    /// </summary>
    public static string? GuestToolShapeProblem(string tool, IReadOnlySet<string> mcpServers)
    {
        ArgumentNullException.ThrowIfNull(mcpServers);
        if (string.IsNullOrWhiteSpace(tool) || !tool.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return $"'{tool}' is not a tool name (letters, digits, '_' and '-').";
        if (tool == "cron_create")
            return "'cron_create' cannot be a guest tool: a scheduled job runs with every tool and reports to the chat.";
        // A5: a child session works for the session's owner, with the owner's tools and models.
        if (tool == Corvid.Kernel.Turns.DelegateTaskTool.ToolName)
            return $"'{tool}' cannot be a guest tool: a child session would work for the guest with the owner's tools and models.";
        if (McpServerOf(tool) is { } server && !mcpServers.Contains(server))
            return $"'{tool}' names MCP server '{server}', which is not configured under Corvid:Mcp:Servers.";
        return null;
    }

    /// <summary>
    /// One guest's own allowlist (G-14), checked as an adapter's is (<see cref="GuestToolShapeProblem"/>, then
    /// <see cref="GuestToolProblems"/>'s registry check when the registry is known): each refused name with why. The app's
    /// Gateway page and <c>corvid gateway pair tools</c> both check through this before writing; the CLI's verb works on the
    /// stores alone, without a runtime, so it passes no registry and a name the registry lacks is refused when it is used.
    /// </summary>
    public static IReadOnlyList<string> GuestAllowlistProblems(IEnumerable<string> tools, CorvidOptions options, IEnumerable<ToolDescriptor>? registered)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);
        var servers = options.Mcp.Servers.Keys.ToHashSet(StringComparer.Ordinal);
        var known = registered is null ? null : Known(registered);
        return [.. tools.Select(tool => GuestToolShapeProblem(tool, servers) ?? (known is null ? null : GuestToolProblem(tool, known))).OfType<string>()];
    }

    private static Dictionary<string, ToolDescriptor> Known(IEnumerable<ToolDescriptor> registered)
    {
        var known = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in registered)
            known.TryAdd(descriptor.Name, descriptor);
        return known;
    }

    private static string? GuestToolProblem(string tool, Dictionary<string, ToolDescriptor> known)
    {
        if (known.TryGetValue(tool, out var descriptor))
            return descriptor.Risk.SideEffects.HasFlag(SideEffects.Memory)
                ? $"'{tool}' cannot be a guest tool: it writes memory, and a guest does not write the owner's memory or answer its review queue."
                : null;
        return McpServerOf(tool) is null
            ? $"no tool is named '{tool}'; the tools are {string.Join(", ", known.Keys.Order(StringComparer.Ordinal))}."
            : null;
    }

    public static GatewayOptions OptionsFor(CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var gateway = options.Gateway;
        var adapters = ImmutableDictionary.CreateBuilder<string, AdapterOptions>(StringComparer.Ordinal);
        foreach (var (key, settings) in gateway.Adapters.Where(pair => pair.Value is not null))
        {
            var name = key.ToLowerInvariant();
            adapters[name] = new AdapterOptions(name)
            {
                Owners = [.. settings.Owners],
                Guests = [.. settings.Guests],
                GuestTools = ImmutableHashSet.CreateRange(StringComparer.Ordinal, settings.GuestTools),
                HomeChat = settings.HomeChat is { Length: > 0 } home ? home : null,
                Chats = settings.Chats.Where(chat => chat.Value is not null).ToImmutableDictionary(chat => chat.Key,
                    chat => new ChatSettings(chat.Value.Persona, chat.Value.Slots), StringComparer.Ordinal),
            };
        }
        return new GatewayOptions
        {
            Adapters = adapters.ToImmutable(),
            ConnectTimeout = TimeSpan.FromSeconds(gateway.ConnectTimeoutSeconds),
            ApprovalTimeout = TimeSpan.FromSeconds(gateway.ApprovalTimeoutSeconds),
        };
    }

    /// <exception cref="InvalidOperationException">No adapter is enabled, one is enabled that Corvid does not have, or a guest tool is
    /// refused (<see cref="GuestToolProblems"/>).</exception>
    public static GatewayHost Create(CorvidRuntime runtime, SlashCommandRegistry commands, CorvidOptions options,
        IEnumerable<GatewayAdapterRegistration>? supplied = null, Action<string>? notice = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(options);
        if (GuestToolProblems(options, runtime.Tools.List()) is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        var extra = (supplied ?? []).ToDictionary(registration => registration.Name, StringComparer.OrdinalIgnoreCase);
        var registrations = new List<GatewayAdapterRegistration>();
        GatewayVault? vault = null;
        foreach (var name in Enabled(options))
        {
            var settings = options.Gateway.Adapters.First(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (extra.TryGetValue(name, out var registration))
            {
                registrations.Add(registration with { Name = name });
                continue;
            }
            // One Vault session for every adapter; nothing connects until the gateway starts each adapter.
            vault ??= GatewayVault.Create(options.Vault.SocketPath is { Length: > 0 } socket ? CorvidPaths.ExpandHome(socket) : VaultClient.DefaultSocketPath());
            var access = vault;
            registrations.Add(name switch
            {
                "telegram" => new GatewayAdapterRegistration(name, () => TelegramAdapter.Create(new TelegramOptions(new SecretRef(settings.Secret!))
                {
                    Name = name,
                    PollTimeout = TimeSpan.FromSeconds(settings.PollSeconds ?? 25),
                }, access)),
                "discord" => new GatewayAdapterRegistration(name, () => DiscordAdapter.Create(new DiscordOptions(new SecretRef(settings.Secret!))
                {
                    Name = name,
                    Channels = [.. settings.Channels],
                    PollInterval = TimeSpan.FromSeconds(settings.PollSeconds ?? 5),
                }, access)),
                "slack" => new GatewayAdapterRegistration(name, () => SlackAdapter.Create(
                    new SlackOptions(new SecretRef(settings.Secret!), new SecretRef(settings.AppSecret!)) { Name = name }, access)),
                _ => throw new InvalidOperationException($"No gateway adapter named '{name}'; Corvid has {string.Join(", ", Platforms.Order(StringComparer.Ordinal))}."),
            });
        }
        if (registrations.Count == 0)
            throw new InvalidOperationException("No gateway adapter is enabled. Set Corvid:Gateway:Adapters:<telegram|discord|slack>:Enabled " +
                "to true, with the Vault secret that holds its token.");
        var pairing = new PairingStore(CorvidPaths.GatewayDatabase(options));
        return new GatewayHost(registrations, new RuntimeGatewayAgent(runtime, commands), pairing, new DeliveryLedger(runtime.Log),
            OptionsFor(options)) { Notice = notice };
    }
}

/// <summary>The gateway's view of the runtime: chat sessions, the shared command registry and approval routing.</summary>
internal sealed class RuntimeGatewayAgent(CorvidRuntime runtime, SlashCommandRegistry commands) : IGatewayAgent
{
    public async Task OpenAsync(string sessionId, string adapter, ChatSettings settings, CancellationToken cancellationToken)
    {
        // Applied when the session is first opened: its turn options (persona, deliver) are built with its actor.
        var session = runtime.OpenSession(sessionId, adapter, new SessionSettings(Persona: settings.Persona, AskDeliver: true));
        // Per-channel slot overrides (plan §12), pinned so the end of a turn does not undo them. Slots are not restored from the
        // log on resume (C-006), so each start switches again.
        foreach (var (slot, model) in settings.Slots)
        {
            if (slot == ModelSlots.TextEmbedder && !runtime.IsConfigured(slot, model))
                throw new InvalidOperationException($"Memory embeds only with the embedder.text models configured at start, not {model}.");
            var availability = runtime.Catalogue.Check(model);
            if (!availability.IsReady)
                throw new InvalidOperationException(availability.Detail);
            var binding = runtime.Bind(slot, model);
            if (session.Slots.TryCurrent(slot, out var current) && current.Profile.Id == binding.Profile.Id)
                continue;
            var result = await session.Actor.SwitchModelAsync(slot, binding, pin: true, cancellationToken).ConfigureAwait(false);
            if (result.Status is not (SwitchStatus.Switched or SwitchStatus.Unchanged))
                throw new InvalidOperationException($"{slot} could not move to {binding.Profile.Id} for {sessionId}: {result.Detail}");
        }
    }

    public async ValueTask<Task<AgentReply>> PostAsync(string sessionId, string text, GuestScope? guest, CancellationToken cancellationToken)
    {
        var session = Session(sessionId);
        // Anyone but the owner is outside content (plan §10.3): their message taints the session until the owner speaks, and
        // their turn gets only the adapter's guest tools. The owner's next turn has the whole toolset again.
        var delivery = await (guest is null
            ? session.PostAsync(text, EnvelopeOrigin.User, cancellationToken)
            : session.PostAsGuestAsync(text, guest, cancellationToken)).ConfigureAwait(false);
        return ReplyAsync(session, delivery);
    }

    public bool IsCommand(string text) => SlashCommandRegistry.IsCommand(text);

    public async Task<string> RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken)
    {
        var output = await commands.ExecuteAsync(line, new SlashCommandContext(runtime, Session(sessionId), commands), cancellationToken)
            .ConfigureAwait(false);
        return output.ToPlainText();
    }

    public IDisposable AttachPrompter(string sessionId, IApprovalPrompter prompter) => runtime.AttachPrompter(sessionId, prompter);

    private CorvidSession Session(string sessionId) =>
        runtime.TryGetSession(sessionId, out var session) ? session : throw new InvalidOperationException($"Session {sessionId} is not open.");

    private async Task<AgentReply> ReplyAsync(CorvidSession session, Task<Kernel.Sessions.Delivery> pending)
    {
        var delivery = await pending.ConfigureAwait(false);
        var head = runtime.Log.GetHead(session.Id);
        // The exception stays in the log and on the host; a chat (possibly a group) hears only that the turn failed.
        if (delivery.Result is not { } result)
            return new AgentReply("That turn failed; the details are in the session's log.", "reply", session.Tainted, head, Failed: true);
        return new AgentReply(result.Text, result.Deliver, result.Tainted, head);
    }
}

/// <summary>
/// The gateway as one hosted service (plan §12): it starts the runtime, then the gateway's adapters, and runs until the host
/// stops. <see cref="Started"/> completes with the running gateway, or faults with why it could not start.
/// </summary>
public sealed class GatewayService(CorvidRuntimeFactory factory, IOptions<CorvidOptions> options, SlashCommandRegistry commands,
    IEnumerable<GatewayAdapterRegistration> registrations) : BackgroundService
{
    private readonly TaskCompletionSource<(CorvidRuntime Runtime, GatewayHost Gateway)> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(CorvidRuntime Runtime, GatewayHost Gateway)> Started => _started.Task;

    /// <summary>Connects, failures and pairing requests, for the surface running the service to print.</summary>
    public Action<string>? Notice { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // Stopped before it started: whoever waits on Started is not left waiting.
            _started.TrySetCanceled(stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        CorvidRuntime runtime;
        try
        {
            runtime = await factory.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            return;
        }
        await using (runtime.ConfigureAwait(false))
        {
            // The same hosting the windowed app uses (G-14): one process holds gateway.db.lock and leaves a note saying so.
            var hosted = new HostedGateway(runtime, commands, options.Value, registrations) { Notice = message => Notice?.Invoke(message) };
            await using (hosted.ConfigureAwait(false))
            {
                GatewayStatus status;
                try
                {
                    status = await hosted.StartAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _started.TrySetException(exception);
                    return;
                }
                if (status.Hosting != GatewayHosting.Here || hosted.Gateway is not { } gateway)
                {
                    _started.TrySetException(new InvalidOperationException(status.Hosting == GatewayHosting.Elsewhere
                        ? "Another corvid gateway is running against this data directory: " + status.Detail
                        : status.Detail));
                    return;
                }
                _started.TrySetResult((runtime, gateway));
                try
                {
                    await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                // Answers still being written reach the ledger (and are sent) before the runtime goes.
                await hosted.StopAsync().ConfigureAwait(false);
            }
            await runtime.MaintenanceIdleAsync().ConfigureAwait(false);
        }
    }
}
