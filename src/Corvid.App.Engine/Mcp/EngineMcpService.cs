using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.App.Core;
using Corvid.App.Core.Mcp;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Ops;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CoreDefinition = Corvid.App.Core.Mcp.McpDefinition;
using CoreHeld = Corvid.App.Core.Mcp.McpHeldTool;
using CoreState = Corvid.App.Core.Mcp.McpServerState;
using CoreTrust = Corvid.App.Core.Mcp.McpTrust;
using EngineHeld = Corvid.Mcp.McpHeldTool;
using EngineState = Corvid.Mcp.McpServerState;
using EngineTrust = Corvid.Mcp.McpTrust;

namespace Corvid.App.Engine.Mcp;

/// <summary>
/// The MCP page over the runtime's MCP clients and pin store, the operations <c>/mcp</c> runs (C-018, C-022, C-033): the
/// configured servers with their state and trust, the definitions held back with their pins, accept and allow through
/// <see cref="McpClients.Accept"/> with the hash the page showed (so a review accepts exactly what it showed), forget
/// through <see cref="McpComposition.Forget"/>, and trust and new servers written to <c>corvid.json</c> through the config
/// writer.
/// <para>
/// The runtime reads its configuration once, at start, and starts the servers then: a server added here, or a trust
/// changed, applies from the next start of Corvid. The page shows both: what runs now, and what the next start will read
/// (<see cref="CorvidConfigWriter.NextStart"/>), in the server's detail line.
/// </para>
/// <para>
/// "Keep blocked" has no record in the engine: the tool stays held (which is the engine's own behaviour until someone
/// accepts it), and the choice is remembered for this window only.
/// </para>
/// </summary>
public sealed class EngineMcpService : IMcpService, IDisposable
{
    /// <summary>The note on <c>corvid mcp serve</c>: this window cannot see whether another client runs it.</summary>
    public const string ServeNote =
        "Another MCP client (an editor, another agent) starts `corvid mcp serve` over stdio; this app cannot see whether one runs. Approvals still apply to what it asks for.";

    /// <summary>How much of the pins session is read for when a tool was held: its newest events.</summary>
    private const int HeldEventsRead = 512;

    private readonly AppEngine _engine;
    private readonly TimeProvider _time;
    private readonly ChangeSignal _changed;
    private readonly ConcurrentDictionary<(string Server, string Tool, string Hash), byte> _keptBlocked = new();

    public EngineMcpService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _time = time;
        _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? Changed;

    public async Task<McpSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var writer = Writer();
        return await Task.Run(() => Snapshot(runtime, NextStart(writer)), cancellationToken).ConfigureAwait(false);
    }

    public Task AcceptAsync(string server, string tool, string hash, CancellationToken cancellationToken) =>
        TakeAsync(server, tool, hash, allow: false, cancellationToken);

    public Task AllowAsync(string server, string tool, string hash, CancellationToken cancellationToken) =>
        TakeAsync(server, tool, hash, allow: true, cancellationToken);

    public async Task KeepBlockedAsync(string server, string tool, CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var held = FindHeld(runtime, server, tool);
        _keptBlocked[(server, held.Tool, held.Definition.Hash)] = 0;
        _changed.Raise();
    }

    public async Task<int> ForgetAsync(string server, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var count = await Task.Run(() =>
        {
            int? dropped;
            try
            {
                dropped = McpComposition.Forget(Pins(runtime), server, runtime.Log);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The pins cannot be changed: {exception.Message}", exception);
            }
            return dropped ?? throw new InvalidOperationException($"{SafeText.Reveal(server)} has no pinned definitions; nothing was forgotten.");
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return count;
    }

    public async Task SetTrustAsync(string server, CoreTrust trust, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var writer = Writer();
        await Task.Run(() =>
        {
            var next = NextStart(writer);
            var name = Configured(next, server) ?? Configured(runtime.Options, server)
                ?? throw new InvalidOperationException($"There is no MCP server named {SafeText.Reveal(server)}.");
            var settings = next.Mcp.Servers.GetValueOrDefault(name) ?? runtime.Options.Mcp.Servers[name];
            if (trust == CoreTrust.Trusted && SafeText.HasHidden(name + Target(settings)))
                throw new InvalidOperationException($"{SafeText.Reveal(server)}'s configuration holds hidden characters; it cannot be trusted.");
            var result = writer.Apply(ConfigEdit.Set($"Corvid:Mcp:Servers:{name}:Trust", trust == CoreTrust.Trusted ? "Trusted" : "Untrusted"));
            if (!result.Written)
                throw new InvalidOperationException("corvid.json was not changed: " + result.FailureText);
            if (!result.ShadowedKeys.IsEmpty)
                throw new InvalidOperationException(
                    $"Written to corvid.json, but {string.Join(", ", result.ShadowedKeys)} is set for this run by an environment variable or --set, which wins over the file.");
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
    }

    public async Task<McpServerRecord> AddServerAsync(McpServerDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var writer = Writer();
        var name = draft.Name.Trim();
        var target = draft.Target.Trim();
        if (SafeText.HasHidden(draft.Name) || SafeText.HasHidden(draft.Target))
            throw new InvalidOperationException("The server holds hidden characters; nothing was added.");
        if (name.Length == 0 || name.Length > 24 || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException("A server's name is lower-case letters, digits, - and _, at most 24 characters (its tools are named <server>__<tool>).");
        if (target.Length == 0)
            throw new InvalidOperationException(draft.Transport == McpTransport.Stdio ? "Give the command that starts it." : "Give its address.");
        List<ConfigEdit> edits = [ConfigEdit.Set($"Corvid:Mcp:Servers:{name}:Trust", draft.Trust == CoreTrust.Trusted ? "Trusted" : "Untrusted")];
        if (draft.Transport == McpTransport.Http)
        {
            if (!(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
                throw new InvalidOperationException("An HTTP server's address starts with https:// (or http:// on this machine).");
            edits.Add(ConfigEdit.Set($"Corvid:Mcp:Servers:{name}:Transport", "http"));
            edits.Add(ConfigEdit.Set($"Corvid:Mcp:Servers:{name}:Url", target));
        }
        else
        {
            var words = Words(target);
            edits.Add(ConfigEdit.Set($"Corvid:Mcp:Servers:{name}:Transport", "stdio"));
            edits.Add(ConfigEdit.Set($"Corvid:Mcp:Servers:{name}:Command", words[0]));
            edits.Add(new ConfigEdit($"Corvid:Mcp:Servers:{name}:Arguments", new JsonArray([.. words.Skip(1).Select(word => (JsonNode?)JsonValue.Create(word))])));
        }

        var record = await Task.Run(() =>
        {
            if (Configured(NextStart(writer), name) is not null || Configured(runtime.Options, name) is not null)
                throw new InvalidOperationException($"There is already a server named {name}.");
            var result = writer.Apply(edits);
            if (!result.Written)
                throw new InvalidOperationException("The server was not added: " + result.FailureText);
            return new McpServerRecord(name, draft.Transport, draft.Trust, CoreState.NotStarted)
            {
                Target = target,
                Detail = "written to corvid.json; it starts with the next start of Corvid, and its tools are pinned as it first lists them",
            };
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return record;
    }

    public void Dispose() => _changed.Dispose();

    private CorvidConfigWriter Writer() => _engine.Services.GetRequiredService<CorvidConfigWriter>();

    private async Task TakeAsync(string server, string tool, string hash, bool allow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentException.ThrowIfNullOrEmpty(tool);
        ArgumentNullException.ThrowIfNull(hash);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var mcp = runtime.Mcp ?? throw new InvalidOperationException("No MCP servers run in this process (Corvid:Mcp:Servers).");
        await Task.Run(() =>
        {
            var held = FindHeld(runtime, server, tool);
            // What would be pinned is what the page showed: a name, server or definition hiding a character is never pinned.
            if (McpText.HasHidden(Held(held, null)))
                throw new InvalidOperationException(
                    $"{SafeText.Reveal(tool)}'s name, server or definition holds hidden characters; it cannot be {(allow ? "allowed" : "pinned")}.");
            if (!held.Definition.Hash.StartsWith(hash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{SafeText.Clean(tool)}'s definition changed since it was shown (now {Short(held.Definition.Hash)}); review it again.");
            var result = mcp.Accept(server, held.Tool, allowFlagged: allow, expectedHash: hash);
            if (!result.Accepted)
                throw new InvalidOperationException(result.Message);
            _keptBlocked.TryRemove((server, held.Tool, held.Definition.Hash), out _);
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
    }

    /// <summary>The held definition the page names by its Corvid name (<c>server__tool</c>), or by the server's own name.</summary>
    private static EngineHeld FindHeld(CorvidRuntime runtime, string server, string tool)
    {
        var status = runtime.Mcp?.Status().FirstOrDefault(candidate => candidate.Name == server)
            ?? throw new InvalidOperationException($"There is no running MCP server named {SafeText.Reveal(server)}.");
        return status.Held.FirstOrDefault(held => CorvidName(server, held.Tool) == tool || held.Tool == tool)
            ?? throw new InvalidOperationException($"{SafeText.Reveal(server)} has no held tool named {SafeText.Reveal(tool)}.");
    }

    private McpSnapshot Snapshot(CorvidRuntime runtime, CorvidOptions next)
    {
        var pins = Pins(runtime);
        var statuses = runtime.Mcp?.Status() ?? [];
        var heldAt = HeldTimes(runtime.Log);
        var servers = new List<McpServerRecord>();
        var held = new List<CoreHeld>();
        foreach (var status in statuses)
        {
            var serverPins = SafePins(pins, status.Name);
            var tools = new List<McpToolRecord>();
            foreach (var name in status.Tools)
            {
                var pin = serverPins.FirstOrDefault(pair => CorvidName(status.Name, pair.Key) == name).Value;
                tools.Add(pin is null
                    ? new McpToolRecord(status.Name, name, McpToolState.Unpinned)
                    : new McpToolRecord(status.Name, name, pin.AllowedFlagged == pin.Hash ? McpToolState.Allowed : McpToolState.Pinned) { Hash = Short(pin.Hash) });
            }
            foreach (var item in status.Held)
            {
                var record = Held(item, heldAt.GetValueOrDefault((item.Server, item.Tool, item.Definition.Hash)));
                held.Add(record);
                tools.Add(new McpToolRecord(status.Name, record.Tool, McpToolState.Held)
                {
                    Hash = item.Pin is { } pin ? Short(pin.Hash) : "",
                    HeldReason = record.Reason,
                    KeptBlocked = record.KeptBlocked,
                });
            }
            var connection = runtime.Mcp!.Servers.First(candidate => candidate.Definition.Name == status.Name);
            servers.Add(new McpServerRecord(status.Name, Transport(status.Transport), Trust(status.Trust), State(status.State))
            {
                Target = Target(connection.Definition),
                Detail = Detail(status, next),
                Tools = tools,
                HasPins = serverPins.Count > 0,
            });
        }

        // Configured, but not running in this process: added since it started, enabled since, or turned off.
        var running = statuses.Select(status => status.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, settings) in next.Mcp.Servers.Where(pair => pair.Value is not null && !running.Contains(pair.Key))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var http = settings.Transport?.Trim().Equals("http", StringComparison.OrdinalIgnoreCase) == true
                || (settings.Transport is null or "" && settings.Url is { Length: > 0 });
            servers.Add(new McpServerRecord(name, http ? McpTransport.Http : McpTransport.Stdio, Trust(settings.Trust), CoreState.NotStarted)
            {
                Target = Target(settings),
                Detail = !settings.Enabled ? "turned off in corvid.json (Enabled is false); it does not start"
                    : "configured since Corvid started; it starts with the next start of Corvid",
                HasPins = SafePins(pins, name).Count > 0,
            });
        }

        return new McpSnapshot(servers, held, new McpServeStatus(false, Exposes(runtime.Options), ServeNote),
            pins is JsonFileMcpPinStore file ? file.FilePath : CorvidPaths.McpPins(runtime.Options));
    }

    private CoreHeld Held(EngineHeld held, DateTimeOffset? at)
    {
        var reason = held.Reason switch
        {
            McpHoldReasons.Changed => McpHoldReason.Changed,
            McpHoldReasons.Flagged => McpHoldReason.Flagged,
            McpHoldReasons.Injection => McpHoldReason.Injection,
            _ => McpHoldReason.New,
        };
        // A withheld definition is its pin's: the whole of it is what was judged, so there is nothing to compare it with.
        var pin = reason == McpHoldReason.Injection ? null : held.Pin;
        return new CoreHeld(held.Server, CorvidName(held.Server, held.Tool), reason, Definition(held.Definition.Canonical), Short(held.Definition.Hash),
            at ?? _time.GetUtcNow())
        {
            Pinned = pin is null ? null : Definition(pin.Definition),
            PinnedHash = held.Pin is { } any ? Short(any.Hash) : "",
            PinnedAt = held.Pin?.PinnedAt,
            Flag = held.Flag ?? (reason == McpHoldReason.Injection && held.Decision is { } decision ? "judged an injection (decision " + decision + ")" : ""),
            KeptBlocked = _keptBlocked.ContainsKey((held.Server, held.Tool, held.Definition.Hash)),
        };
    }

    /// <summary>When each held definition was last held, from the pins session's newest events (<see cref="McpToolHeld"/>).</summary>
    private static Dictionary<(string Server, string Tool, string Hash), DateTimeOffset> HeldTimes(IEventLog log)
    {
        var times = new Dictionary<(string, string, string), DateTimeOffset>();
        var head = log.GetHead(McpSessions.Pins);
        if (head == 0)
            return times;
        foreach (var stored in log.Read(McpSessions.Pins, Math.Max(0, head - HeldEventsRead), head, HeldEventsRead))
            if (stored.Payload is McpToolHeld held)
                times[(held.Server, held.Tool, held.Hash)] = stored.RecordedAt;
        return times;
    }

    private static CoreDefinition Definition(string canonical) => new(
        McpToolDefinition.Field(canonical, "description") ?? "",
        McpToolDefinition.Field(canonical, "title") ?? "",
        McpToolDefinition.Field(canonical, "inputSchema") ?? "",
        McpToolDefinition.Field(canonical, "annotations") ?? "");

    /// <summary>What the server's line says beyond its state: the server's own detail, and a trust the next start reads differently.</summary>
    private static string Detail(McpServerStatus status, CorvidOptions next)
    {
        var parts = new List<string>();
        if (status.Detail is { Length: > 0 } detail)
            parts.Add(detail);
        if (status.ToolsChanged)
            parts.Add(McpServerStatus.ToolsChangedNote);
        parts.AddRange(status.Issues.Where(issue => !status.Held.Any(held => held.Issue == issue)).Select(issue => "left out " + issue));
        var name = Configured(next, status.Name);
        if (name is null)
            parts.Add("removed from corvid.json since Corvid started; it runs until the next start");
        else if (next.Mcp.Servers[name] is { } settings)
        {
            if (!settings.Enabled)
                parts.Add("turned off in corvid.json since Corvid started; it runs until the next start");
            else if (settings.Trust != status.Trust)
                parts.Add($"{(settings.Trust == EngineTrust.Trusted ? "trusted" : "untrusted")} from the next start of Corvid (written to corvid.json)");
        }
        return string.Join("; ", parts);
    }

    private static IReadOnlyList<string> Exposes(CorvidOptions options)
    {
        var serve = options.Mcp.Serve;
        var exposes = new List<string>();
        if (serve.Memory && options.Memory.Enabled)
        {
            exposes.Add(serve.Conversations ? "search_memory (facts and earlier conversations)" : "search_memory (facts)");
            if (serve.ProjectMemory)
                exposes.AddRange(["get_memory", "get_memory_context"]);
        }
        if (serve.Skills && options.Skills.Enabled)
            exposes.Add("skill_list and the skills' read-only tools");
        return exposes;
    }

    private static IMcpPinStore Pins(CorvidRuntime runtime) =>
        runtime.Mcp?.PinStore ?? new JsonFileMcpPinStore(CorvidPaths.McpPins(runtime.Options));

    private static IReadOnlyDictionary<string, McpToolPin> SafePins(IMcpPinStore pins, string server)
    {
        try
        {
            return pins.Pins(server);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException($"The MCP pins cannot be read: {exception.Message}", exception);
        }
    }

    /// <summary>The configuration the next start reads, as options; the running ones when it cannot be read.</summary>
    private static CorvidOptions NextStart(CorvidConfigWriter writer)
    {
        try
        {
            return writer.NextStart().GetSection("Corvid").Get<CorvidOptions>() ?? new CorvidOptions();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException($"corvid.json cannot be read: {exception.Message}", exception);
        }
    }

    /// <summary>The configured key for a server (configuration keys ignore case; the server names are lower case).</summary>
    private static string? Configured(CorvidOptions options, string server) =>
        options.Mcp.Servers.Keys.FirstOrDefault(key => string.Equals(key, server, StringComparison.OrdinalIgnoreCase));

    internal static string CorvidName(string server, string tool) => McpToolMapping.ToolName(server, tool);

    /// <summary>The hash as the page shows and gives back: the shortest prefix <c>--hash</c> takes (C-033).</summary>
    private static string Short(string hash) => hash.Length > McpClients.MinHashPrefix ? hash[..McpClients.MinHashPrefix] : hash;

    private static string Target(McpServerDefinition definition) => definition.Transport == McpTransportKind.Http
        ? definition.Url?.ToString() ?? ""
        : string.Join(' ', [definition.Command ?? "", .. definition.Arguments.Select(Quote)]).Trim();

    private static string Target(McpServerSettings settings) => settings.Url is { Length: > 0 } url && settings.Command is not { Length: > 0 }
        ? url
        : string.Join(' ', [settings.Command ?? "", .. settings.Arguments.Select(Quote)]).Trim();

    private static string Quote(string word) => word.Length == 0 || word.Any(char.IsWhiteSpace) ? "\"" + word.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : word;

    /// <summary>A command line as words: whitespace separates them, single or double quotes keep spaces, a backslash escapes in double quotes.</summary>
    internal static IReadOnlyList<string> Words(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var started = false;
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } open)
            {
                if (c == open)
                    quote = null;
                else if (open == '"' && c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
                    word.Append(line[++i]);
                else
                    word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                started = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (started)
                    words.Add(word.ToString());
                word.Clear();
                started = false;
            }
            else
            {
                word.Append(c);
                started = true;
            }
        }
        if (quote is not null)
            throw new InvalidOperationException("The command has a quote that is never closed.");
        if (started)
            words.Add(word.ToString());
        return words.Count == 0 ? throw new InvalidOperationException("Give the command that starts it.") : words;
    }

    private static McpTransport Transport(McpTransportKind kind) => kind == McpTransportKind.Http ? McpTransport.Http : McpTransport.Stdio;

    private static CoreTrust Trust(EngineTrust trust) => trust == EngineTrust.Trusted ? CoreTrust.Trusted : CoreTrust.Untrusted;

    private static CoreState State(EngineState state) => state switch
    {
        EngineState.Ready => CoreState.Running,
        EngineState.Failed or EngineState.Stopped => CoreState.Failed,
        _ => CoreState.Starting,
    };
}
