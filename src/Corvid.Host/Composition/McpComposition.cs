using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
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
    /// <param name="only">C-033: start only the enabled server of this name (<c>corvid mcp diff|accept|allow</c>); null for all.</param>
    /// <param name="transports">
    /// C-033: the transports to use instead of stdio under the sandbox and HTTP through Vault; an <see cref="IMcpTransports"/>
    /// registered in the host's services (in-process test servers) reaches the runtime this way. Null for the real ones.
    /// </param>
    public static McpClients? Start(CorvidOptions options, IEnumerable<string> deniedPaths, ToolRegistry tools, IEventLog? eventLog = null,
        string? only = null, IMcpTransports? transports = null)
    {
        var (servers, _) = Definitions(options, deniedPaths);
        if (only is not null)
            servers = [.. servers.Where(server => server.Name == only)];
        if (servers.IsEmpty)
            return null;
        transports ??= Transports(options, servers);
        var clients = new McpClients(servers, transports, tools, new JsonFileMcpPinStore(CorvidPaths.McpPins(options)), Notices(eventLog));
        clients.Start();
        return clients;
    }

    private static McpTransports Transports(CorvidOptions options, ImmutableArray<McpServerDefinition> servers)
    {
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
        return new McpTransports(new McpTransportOptions
        {
            Sandbox = new SandboxOptions { LauncherPath = launcher },
            Vault = vault?.Client,
            Placeholders = vault?.Placeholders,
        });
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
        Append(log, payload);
    };

    /// <summary>
    /// C-033: drops <paramref name="server"/>'s pins (<c>/mcp forget</c>, <c>corvid mcp forget</c>) and, when it had an entry,
    /// leaves a <c>corvid.mcp.pin</c> span with status <c>forgotten</c> and, with a log, <see cref="McpPinsForgotten"/> in the
    /// <see cref="McpSessions.Pins"/> session. Returns the number of pins dropped, or null when the server had no entry.
    /// </summary>
    public static int? Forget(IMcpPinStore pins, string server, IEventLog? log)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentException.ThrowIfNullOrEmpty(server);
        var dropped = pins.Remove(server);
        if (dropped is not { } count)
            return null;
        using (var activity = HostTelemetry.Source.StartActivity("corvid.mcp.pin", ActivityKind.Internal))
        {
            activity?.SetTag("corvid.mcp.server", server);
            activity?.SetTag("corvid.mcp.pin.status", "forgotten");
            activity?.SetTag("corvid.mcp.pin.count", count);
        }
        if (log is not null)
            Append(log, new McpPinsForgotten(server, count));
        return count;
    }

    private static void Append(IEventLog log, SessionEvent payload)
    {
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
    }

    /// <summary>
    /// What a served call leaves behind (C-028): one line on <paramref name="log"/> (serve's standard error; standard output is
    /// the protocol) whether or not any trace listener is attached, and a <c>corvid.mcp.serve</c> span when one is (the
    /// configured OTLP export). Both carry the tool, the outcome, how many items it returned, the scope's source, and the
    /// result's size and cut. Never the arguments or any memory text.
    /// </summary>
    public static Action<McpServeCall> ServeCalls(TextWriter? log = null) => call =>
    {
        var (items, project) = Summarize(call.Result);
        var outcome = call.IsError ? "error" : "ok";
        log?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"corvid mcp serve: call {call.Tool} {outcome} items={items} project={project ?? "-"} chars={call.Chars} cut={(call.Cut ? "yes" : "no")} ms={(long)call.Elapsed.TotalMilliseconds}"));
        using var activity = HostTelemetry.Source.StartActivity("corvid.mcp.serve", ActivityKind.Server);
        if (activity is null)
            return;
        // The span covers the call: it is reported when the call ends.
        activity.SetStartTime(DateTime.UtcNow - call.Elapsed);
        activity.SetTag("corvid.mcp.tool", call.Tool);
        activity.SetTag("corvid.mcp.call", call.CallId);
        activity.SetTag("corvid.mcp.serve.outcome", outcome);
        activity.SetTag("corvid.mcp.serve.items", items);
        if (project is not null)
            activity.SetTag("corvid.mcp.serve.project", project);
        activity.SetTag("corvid.mcp.serve.chars", call.Chars);
        activity.SetTag("corvid.mcp.serve.cut", call.Cut);
        if (call.IsError)
            activity.SetStatus(ActivityStatusCode.Error);
    };

    /// <summary>
    /// The number of memory items in a served result (its <c>results[].id</c>, or a single <c>id</c>) and its scope source, when
    /// it is one of <see cref="SharedMemoryTools"/>'s.
    /// </summary>
    internal static (int Items, string? Project) Summarize(System.Text.Json.JsonElement? result)
    {
        if (result is not { ValueKind: System.Text.Json.JsonValueKind.Object } json)
            return (0, null);
        // Only the three scope sources: any other tool's "project" string could be text.
        var project = json.TryGetProperty("project", out var scope) && scope.ValueKind == System.Text.Json.JsonValueKind.String
            && scope.GetString() is SharedMemoryTools.FromArgument or SharedMemoryTools.FromWorkspace or SharedMemoryTools.FromNone
            ? scope.GetString() : null;
        if (json.TryGetProperty("results", out var results) && results.ValueKind == System.Text.Json.JsonValueKind.Array)
            return (results.EnumerateArray().Count(item => item.ValueKind == System.Text.Json.JsonValueKind.Object && item.TryGetProperty("id", out _)), project);
        return (json.TryGetProperty("id", out _) ? 1 : 0, project);
    }

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
                var serve = options.Mcp.Serve;
                var episodes = new EpisodicIndex(database);
                var facts = new FactGraph(database);
                // C-028: lessons and the workspace's project only with project memory on, so a default serve never runs git.
                var lessons = serve.ProjectMemory && options.Memory.Lessons.Enabled ? new LessonStore(database) : null;
                var workspace = CorvidPaths.Workspace(options);
                ProjectIdentity? project = lessons is null ? null
                    : await Kernel.Sessions.ProjectResolver.ResolveAsync(workspace, CorvidPaths.ExpandHome(options.Checkpoints.GitPath),
                        CorvidPaths.Home).ConfigureAwait(false);
                var retriever = new HybridRetriever(episodes, facts, lessons: lessons);
                var memory = new SharedMemoryTools(retriever, new SharedMemoryOptions
                {
                    Facts = facts,
                    Episodes = episodes,
                    IncludeConversations = serve.Conversations,
                    ProjectMemory = serve.ProjectMemory,
                    Lessons = lessons,
                    Workspace = workspace,
                    WorkspaceProject = project,
                    Home = CorvidPaths.Home,
                });
                tools.AddRange(memory.ServedTools());
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
