using System.Collections.Immutable;
using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.Host.Configuration;
using Corvid.Host.Telemetry;
using Corvid.Mcp;
using Corvid.Memory;
using Corvid.Models.Remote;
using Corvid.Sandbox;
using Corvid.Skills;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Corvid.Vault.Client;

namespace Corvid.Host.Composition;

/// <summary>C-018: the configured MCP servers as tools, and <c>corvid mcp serve</c>.</summary>
public static class McpComposition
{
    /// <summary>
    /// The enabled servers as definitions, with the paths every sandboxed server is denied (Vault's locations and Corvid's
    /// own state, as for the shell) and each server's private workspace. Problems are reported, not thrown, so the validator
    /// lists them with everything else.
    /// </summary>
    public static (ImmutableArray<McpServerDefinition> Servers, ImmutableArray<string> Problems) Definitions(CorvidOptions options,
        IEnumerable<string>? deniedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var denied = (deniedPaths ?? []).Distinct(StringComparer.Ordinal).ToImmutableArray();
        var servers = ImmutableArray.CreateBuilder<McpServerDefinition>();
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var (name, settings) in options.Mcp.Servers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (settings is null || !settings.Enabled)
                continue;
            var prefix = $"Corvid:Mcp:Servers:{name}: ";
            McpTransportKind transport;
            switch (settings.Transport?.Trim().ToLowerInvariant())
            {
                case null or "":
                    transport = settings.Url is { Length: > 0 } ? McpTransportKind.Http : McpTransportKind.Stdio;
                    break;
                case "stdio":
                    transport = McpTransportKind.Stdio;
                    break;
                case "http":
                    transport = McpTransportKind.Http;
                    break;
                default:
                    problems.Add(prefix + $"Transport '{settings.Transport}' is not stdio or http.");
                    continue;
            }
            SecretRef? secret = null;
            if (settings.Secret is { Length: > 0 } secretName)
            {
                try
                {
                    secret = new SecretRef(secretName);
                }
                catch (ArgumentException exception)
                {
                    problems.Add(prefix + exception.Message);
                }
            }
            Uri? url = null;
            if (settings.Url is { Length: > 0 } text && !Uri.TryCreate(text, UriKind.Absolute, out url))
                problems.Add(prefix + $"'{text}' is not an absolute URL.");
            if (!Enum.IsDefined(settings.Trust))
                problems.Add(prefix + "Trust must be Untrusted or Trusted.");
            var workspace = settings.Workspace is { Length: > 0 } configured ? CorvidPaths.ExpandHome(configured) : CorvidPaths.McpWorkspace(name);
            var definition = new McpServerDefinition
            {
                Name = name,
                Transport = transport,
                Trust = settings.Trust,
                Command = settings.Command is { Length: > 0 } command ? command : null,
                Arguments = [.. settings.Arguments],
                Environment = settings.Environment.ToImmutableDictionary(StringComparer.Ordinal),
                Sandboxed = settings.Sandboxed,
                Network = settings.Network,
                Workspace = transport == McpTransportKind.Stdio ? workspace : null,
                ReadOnlyPaths = [.. settings.ReadOnlyPaths.Select(CorvidPaths.ExpandHome)],
                Url = url,
                Secret = secret,
                Tools = [.. settings.Tools],
                ReadOnlyTools = [.. settings.ReadOnlyTools],
                StartTimeout = TimeSpan.FromSeconds(settings.StartTimeoutSeconds),
                CallTimeout = TimeSpan.FromSeconds(settings.CallTimeoutSeconds),
                TaskTimeout = TimeSpan.FromSeconds(settings.TaskTimeoutSeconds),
                MaxResultChars = settings.MaxResultChars,
                MaxTools = settings.MaxTools,
                DeniedPaths = denied,
            };
            var invalid = definition.Validate();
            if (invalid.Count > 0)
            {
                problems.AddRange(invalid.Select(problem => "Corvid:Mcp:Servers: " + problem));
                continue;
            }
            servers.Add(definition);
        }
        return (servers.ToImmutable(), problems.ToImmutable());
    }

    /// <summary>What a sandboxed server is denied for this configuration, as the runtime computes it.</summary>
    public static IEnumerable<string> DeniedPaths(CorvidOptions options, string configFile) =>
        CorvidRuntimeFactory.McpDeniedPaths(options, configFile);

    /// <summary>
    /// Starts the configured servers in the background and returns at once (null when none is configured). Tools register
    /// as each server lists them; built-in and DI tools were registered first, so they keep their names. Definitions are
    /// checked against the pins in <see cref="CorvidPaths.McpPins"/> (C-022), so <c>corvid mcp list</c> pins too; held and
    /// accepted definitions go to <paramref name="eventLog"/>'s <see cref="McpSessions.Pins"/> session when one is given.
    /// </summary>
    public static McpClients? Start(CorvidOptions options, IEnumerable<string> deniedPaths, ToolRegistry tools, IEventLog? eventLog = null)
    {
        var (servers, _) = Definitions(options, deniedPaths);
        if (servers.IsEmpty)
            return null;
        VaultAccess? vault = null;
        if (servers.Any(server => server.Secret is not null))
        {
            try
            {
                vault = VaultAccess.Create(options.Vault.SocketPath is { Length: > 0 } socket ? CorvidPaths.ExpandHome(socket) : VaultClient.DefaultSocketPath());
            }
            catch (InvalidOperationException)
            {
                // No runtime directory, so no Vault socket: those servers fail at their start, with the reason in /mcp, and the
                // host goes on without them.
            }
        }
        var launcher = options.Sandbox.LauncherPath is { Length: > 0 } path ? CorvidPaths.ExpandHome(path) : null;
        var transports = new McpTransports(new McpTransportOptions
        {
            Sandbox = new SandboxOptions { LauncherPath = launcher },
            Vault = vault?.Client,
            Placeholders = vault?.Placeholders,
        });
        var clients = new McpClients(servers, transports, tools, new JsonFileMcpPinStore(CorvidPaths.McpPins(options)), Notices(eventLog));
        clients.Start();
        return clients;
    }

    private const int MaxAppendAttempts = 32;

    /// <summary>
    /// What a held, accepted or allowed definition leaves behind (C-022): a short <c>corvid.mcp.pin</c> span, and, with an
    /// event log, <see cref="McpToolHeld"/> or <see cref="McpToolAccepted"/> in the <see cref="McpSessions.Pins"/> session.
    /// </summary>
    internal static Action<McpDefinitionNotice> Notices(IEventLog? log) => notice =>
    {
        using (var activity = HostTelemetry.Source.StartActivity("corvid.mcp.pin", ActivityKind.Internal))
        {
            activity?.SetTag("corvid.mcp.server", notice.Server);
            activity?.SetTag("corvid.mcp.tool", notice.Tool);
            activity?.SetTag("corvid.mcp.pin.status", notice.Kind);
            activity?.SetTag("corvid.mcp.pin.hash", notice.Hash);
        }
        if (log is null)
            return;
        SessionEvent payload = notice.Kind is McpPinKinds.Accepted or McpPinKinds.Allowed
            ? new McpToolAccepted(notice.Server, notice.Tool, notice.Hash, notice.Kind == McpPinKinds.Allowed)
            : new McpToolHeld(notice.Server, notice.Tool, notice.Kind, notice.Hash, notice.PinnedHash, notice.Detail);
        var events = new[] { EventData.Create(payload) };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                log.Append(McpSessions.Pins, log.GetHead(McpSessions.Pins), events);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
                // Another server's notice, or another process, appended first; try at the new head.
            }
        }
    };

    /// <summary>
    /// The tools <c>corvid mcp serve</c> offers, opened without loading any model: memory search runs on FTS and the fact
    /// graph (no embedder in this process), and skills are read from the store. Returns why a part is off as well.
    /// </summary>
    public static async Task<(IReadOnlyList<ToolFunction> Tools, IReadOnlyList<string> Notes)> ServeToolsAsync(
        CorvidOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tools = new List<ToolFunction>();
        var notes = new List<string>();
        if (options.Mcp.Serve.Memory && options.Memory.Enabled)
        {
            var path = CorvidPaths.MemoryDatabase(options);
            if (File.Exists(path))
            {
                var database = new MemoryDatabase(path);
                var retriever = new HybridRetriever(new EpisodicIndex(database), new FactGraph(database));
                tools.AddRange(new SharedMemoryTools(retriever, options.Mcp.Serve.Conversations).CreateTools());
            }
            else
            {
                notes.Add($"memory: no store at {path} yet");
            }
        }
        if (options.Mcp.Serve.Skills && options.Skills.Enabled)
        {
            try
            {
                var store = await SkillStore.OpenAsync(new SkillStoreOptions
                {
                    Root = CorvidPaths.SkillsDirectory(options),
                    GitExecutable = options.Skills.Git,
                }, cancellationToken).ConfigureAwait(false);
                tools.AddRange(new SkillCatalogTools(store).CreateTools());
                tools.AddRange(new SkillFileTools(store).CreateTools().Where(tool => tool.Descriptor.Risk.ReadOnly));
            }
            catch (Exception exception) when (exception is GitException or System.ComponentModel.Win32Exception or IOException
                                                  or UnauthorizedAccessException or InvalidDataException)
            {
                notes.Add("skills: " + exception.Message);
            }
        }
        return (tools, notes);
    }
}
