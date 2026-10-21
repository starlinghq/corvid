using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;

namespace Corvid.Kernel.Traces;

public enum BeaconRetention
{
    /// <summary>Content is kept, redacted and cut to Beacon's per-string limit.</summary>
    Full,
    /// <summary>Beacon's metadata-only form: ids, names, paths, commands and decisions; content is withheld, hashes kept.</summary>
    Metadata,
}

/// <param name="WorkingDirectory">Written as <c>session.working_directory</c>, and what relative tool paths are resolved
/// against. Corvid records no per-session directory, so the CLI passes the configured workspace.</param>
/// <param name="HarnessVersion">Written as <c>harness.version</c>; omitted when empty.</param>
/// <param name="Os">Written as <c>endpoint.os</c> in Go's spelling (<c>linux</c>, <c>darwin</c>, <c>windows</c>).</param>
public sealed record BeaconExportOptions(string WorkingDirectory, string HarnessVersion, string Os)
{
    public BeaconRetention Retention { get; init; } = BeaconRetention.Full;

    /// <summary>
    /// Export the arguments and results of memory tool calls at full retention. Without it they are written at metadata
    /// retention even in a full export: memory holds local-only facts (plan §10.4), and the results do not mark which are.
    /// </summary>
    public bool IncludeLocalMemory { get; init; }

    /// <summary>The largest line, in UTF-8 bytes; content over it is dropped (<see cref="BeaconExporter.MaxEventBytes"/>).</summary>
    public int MaxEventBytes { get; init; } = BeaconExporter.MaxEventBytes;

    /// <summary>The OS name Go's <c>runtime.GOOS</c> gives for this process.</summary>
    public static string CurrentOs => OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsFreeBSD() ? "freebsd" : "unknown";
}

/// <summary>
/// Writes Corvid's event log as Beacon telemetry (C-027): one JSON object per event in Beacon's schema 1.0
/// (Asymptote-Labs/agent-beacon, MIT, <c>docs/telemetry-schema</c> and <c>pkg/asymptoteobserve/event.go</c>), so Beacon's
/// detections, dashboards and SIEM forwarding read a Corvid session like any harness it collects. A pure mapping over
/// stored events: the caller reads the log.
/// </summary>
/// <remarks>
/// <para>
/// One pass per session, pairing each tool result with its call, as Beacon's own session-file readers do. User and assistant
/// messages, tool calls and results, and approvals are mapped; every other event kind is skipped, and no session start or
/// end is synthesized. <c>vendor</c> and <c>product</c> are Beacon's schema identifiers, which its validator requires;
/// <c>harness.name</c> <c>corvid</c> names the producer.
/// </para>
/// <para>
/// <c>event.id</c> is computed once, on the full-retention line after redaction and the per-string limit, with memory content
/// withheld, and without <c>endpoint</c>, <c>harness.version</c> and <c>session.working_directory</c> (<see cref="BeaconEventId.Identity"/>),
/// so an event has the same id in either retention, with or without <see cref="BeaconExportOptions.IncludeLocalMemory"/>,
/// whether it was exported alone or with the whole log, and from any Corvid build, workspace or host.
/// </para>
/// </remarks>
public static class BeaconExporter
{
    public const string SchemaVersion = "1.0";
    public const string HarnessName = "corvid";

    /// <summary>The cap on one line: 64 KiB.</summary>
    public const int MaxEventBytes = 64 * 1024;

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The Beacon lines for <paramref name="events"/>, in order. Events of several sessions may be interleaved.</summary>
    public static IEnumerable<string> Export(IEnumerable<StoredEvent> events, BeaconExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        var calls = new Dictionary<(string Session, string CallId), ToolCallIssued>();
        var approvals = new Dictionary<(string Session, string ApprovalId), string>();
        foreach (var stored in events)
        {
            if (Map(stored, options, calls, approvals) is { } line)
                yield return line;
        }
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(LineOptions);

    private static string? Map(StoredEvent stored, BeaconExportOptions options, Dictionary<(string, string), ToolCallIssued> calls,
        Dictionary<(string, string), string> approvals)
    {
        var draft = stored.Payload switch
        {
            UserMessage user => Prompt(user),
            AssistantMessage assistant => AgentMessage(assistant),
            ToolCallIssued call => Invoked(stored.SessionId, call, options, calls),
            ToolResult result => Completed(stored.SessionId, result, options, calls),
            ApprovalRequested requested => ApprovalAsked(stored.SessionId, requested, options, calls, approvals),
            ApprovalResolved resolved => ApprovalAnswered(stored.SessionId, resolved, options, calls, approvals),
            _ => null,
        };
        if (draft is null)
            return null;

        var full = Envelope(stored, draft, options);
        // The memory rule: the full form of a memory tool call keeps its fixed message but not its content.
        var withheld = full.DeepClone().AsObject();
        if (draft.Memory)
            BeaconContent.Withhold(withheld, message: false);
        var canonical = draft.Memory ? withheld : full;
        var identity = BeaconEventId.Identity(canonical);
        var id = BeaconEventId.For(identity, Serialize(identity));

        JsonObject output;
        if (options.Retention == BeaconRetention.Metadata)
        {
            output = full;
            BeaconContent.Withhold(output, message: true);
        }
        else
        {
            output = draft.Memory && !options.IncludeLocalMemory ? withheld : full;
        }
        ((JsonObject)output["event"]!).Insert(0, "id", id);
        return BeaconContent.Cap(output, options.MaxEventBytes, Serialize);
    }

    // ---- the envelope ----

    private static JsonObject Envelope(StoredEvent stored, Draft draft, BeaconExportOptions options)
    {
        var @event = new JsonObject
        {
            ["timestamp"] = Timestamp(stored.RecordedAt),
            ["sequence"] = stored.Sequence,
            ["vendor"] = "beacon",
            ["product"] = "endpoint-agent",
            ["schema_version"] = SchemaVersion,
            ["event"] = new JsonObject
            {
                ["kind"] = "agent_runtime",
                ["action"] = draft.Action,
                ["category"] = draft.Category,
                ["fidelity"] = "observed",
            },
            ["severity"] = draft.Severity,
            ["endpoint"] = new JsonObject { ["os"] = options.Os },
        };
        var harness = new JsonObject { ["name"] = HarnessName };
        if (options.HarnessVersion.Length > 0)
            harness["version"] = options.HarnessVersion;
        harness["collection_method"] = "poll";
        @event["harness"] = harness;
        @event["origin"] = "local";
        if (draft.ErrorType is { } error)
            @event["error"] = new JsonObject { ["type"] = error };
        var session = new JsonObject { ["id"] = stored.SessionId };
        if (options.WorkingDirectory.Length > 0)
            session["working_directory"] = options.WorkingDirectory;
        @event["session"] = session;
        // Beacon's Event field order, so every line reads the same way.
        Add(@event, "tool", draft.Tool);
        Add(@event, "file", draft.File);
        Add(@event, "command", draft.Command);
        Add(@event, "mcp", draft.Mcp);
        Add(@event, "approval", draft.Approval);
        Add(@event, "prompt", draft.Prompt);
        if (draft.Primary is { } primary)
            @event["content"] = BeaconContent.Marker(primary, draft.Redacted, draft.Truncated);
        Add(@event, "gen_ai", draft.GenAi);
        @event["message"] = draft.Message;
        if (draft.Truncated)
            @event["field_truncated"] = true;
        return @event;
    }

    /// <summary>Beacon's timestamp: UTC, RFC 3339 with a fixed nine-digit fraction, so timestamps compare as strings.</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00Z";

    private static void Add(JsonObject parent, string key, JsonObject? value)
    {
        if (value is { Count: > 0 })
            parent[key] = value;
    }

    // ---- the mapping ----

    private static Draft Prompt(UserMessage user)
    {
        var draft = new Draft("prompt.submitted", "prompt", "Corvid prompt submitted");
        draft.Prompt = new JsonObject { ["text"] = draft.Retain(user.Text, primary: true) };
        return draft;
    }

    private static Draft AgentMessage(AssistantMessage assistant)
    {
        var draft = new Draft("agent.message", "agent", "Corvid assistant message");
        var text = draft.Retain(assistant.Text, primary: true);
        draft.GenAi = new JsonObject
        {
            ["output"] = new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "assistant",
                    ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["content"] = text }),
                }),
            },
        };
        return draft;
    }

    private static Draft Invoked(string session, ToolCallIssued call, BeaconExportOptions options,
        Dictionary<(string, string), ToolCallIssued> calls)
    {
        calls[(session, call.CallId)] = call;
        var draft = new Draft("tool.invoked", "tool", "Corvid tool invoked") { Memory = IsMemoryTool(call.ToolName) };
        draft.Tool = ToolInfo(draft, call, options);
        draft.GenAi = new JsonObject
        {
            ["operation"] = new JsonObject { ["name"] = "execute_tool" },
            ["tool"] = new JsonObject
            {
                ["call"] = new JsonObject
                {
                    ["arguments"] = draft.Retain(call.Arguments.GetRawText(), primary: true),
                    ["id"] = call.CallId,
                },
                ["name"] = call.ToolName,
            },
        };
        return draft;
    }

    private static Draft Completed(string session, ToolResult result, BeaconExportOptions options,
        Dictionary<(string, string), ToolCallIssued> calls)
    {
        calls.TryGetValue((session, result.CallId), out var call);
        var name = call?.ToolName ?? "";
        var resultText = result.Result.ValueKind == JsonValueKind.String ? result.Result.GetString() ?? "" : result.Result.GetRawText();
        Draft draft;
        if (result.IsError)
        {
            draft = new Draft("tool.failed", "tool", "Corvid tool failed") { Severity = "high", ErrorType = "tool_error" };
        }
        else if (name == "shell")
        {
            draft = new Draft("command.executed", "command", "Corvid command executed");
            var command = new JsonObject();
            if (Argument(call, "command") is { } line)
                command["command"] = draft.Retain(line, primary: false);
            if (result.Result.ValueKind == JsonValueKind.Object && result.Result.TryGetProperty("exit_code", out var exit)
                && exit.TryGetInt32(out var code))
            {
                command["exit_code"] = code;
                if (code != 0)
                    draft.ErrorType = "command_failed";
            }
            var output = ShellOutput(result.Result);
            if (output.Length > 0)
                command["output"] = draft.Retain(output, primary: true);
            draft.Command = command;
        }
        else if (name is "read_file" or "list_files" or "search_files" && ResolvedPath(call, options, name != "read_file") is { } read)
        {
            draft = new Draft("file.read", "file", "Corvid file read") { File = new JsonObject { ["path"] = read, ["operation"] = "read" } };
        }
        else if (name is "write_file" or "patch_file" && ResolvedPath(call, options, false) is { } written)
        {
            var created = name == "write_file" && result.Result.ValueKind == JsonValueKind.Object
                && result.Result.TryGetProperty("created", out var flag) && flag.ValueKind == JsonValueKind.True;
            draft = new Draft("file.modified", "file", "Corvid file modified")
            {
                File = new JsonObject { ["path"] = written, ["operation"] = created ? "create" : "modify" },
            };
        }
        else if (McpParts(name) is { } mcp)
        {
            draft = new Draft("mcp.tool_invoked", "mcp", "Corvid MCP tool invoked")
            {
                Mcp = new JsonObject { ["server"] = mcp.Server, ["tool"] = mcp.Tool },
            };
        }
        else
        {
            draft = new Draft("tool.completed", "tool", "Corvid tool completed");
        }
        draft.Memory = IsMemoryTool(name);
        if (call is not null)
            draft.Tool = ToolInfo(draft, call, options);
        var toolCall = new JsonObject
        {
            ["id"] = result.CallId,
            ["result"] = draft.Retain(resultText, primary: draft.Primary is null),
        };
        var tool = new JsonObject { ["call"] = toolCall };
        if (name.Length > 0)
            tool["name"] = name;
        draft.GenAi = new JsonObject { ["operation"] = new JsonObject { ["name"] = "execute_tool" }, ["tool"] = tool };
        return draft;
    }

    private static Draft ApprovalAsked(string session, ApprovalRequested requested, BeaconExportOptions options,
        Dictionary<(string, string), ToolCallIssued> calls, Dictionary<(string, string), string> approvals)
    {
        if (requested.CallId is { } callId)
            approvals[(session, requested.ApprovalId)] = callId;
        var draft = new Draft("approval.requested", "approval", "Corvid approval requested");
        Approval(draft, session, requested.CallId, options, calls);
        // Beacon writes no decision on a request; required says one was asked for.
        draft.Approval = new JsonObject { ["required"] = true };
        if (requested.Reason.Length > 0)
            draft.Approval["reason"] = draft.Retain(requested.Reason, primary: true);
        return draft;
    }

    private static Draft ApprovalAnswered(string session, ApprovalResolved resolved, BeaconExportOptions options,
        Dictionary<(string, string), ToolCallIssued> calls, Dictionary<(string, string), string> approvals)
    {
        approvals.TryGetValue((session, resolved.ApprovalId), out var callId);
        var decision = resolved.Approved ? "allowed" : "denied";
        var draft = new Draft("approval." + decision, "approval", "Corvid approval " + decision);
        Approval(draft, session, callId, options, calls);
        draft.Approval = new JsonObject { ["required"] = true, ["decision"] = decision };
        if (resolved.Reason.Length > 0)
            draft.Approval["reason"] = draft.Retain(resolved.Reason, primary: true);
        return draft;
    }

    // What an approval was for: the tool, and the command line for a shell call, which Beacon's approval rules match on.
    private static void Approval(Draft draft, string session, string? callId, BeaconExportOptions options,
        Dictionary<(string, string), ToolCallIssued> calls)
    {
        if (callId is null)
            return;
        var tool = new JsonObject { ["call"] = new JsonObject { ["id"] = callId } };
        if (calls.TryGetValue((session, callId), out var call))
        {
            draft.Tool = ToolInfo(draft, call, options);
            if (call.ToolName == "shell" && draft.Tool["command"] is { } command)
                draft.Command = new JsonObject { ["command"] = command.DeepClone() };
            tool["name"] = call.ToolName;
        }
        draft.GenAi = new JsonObject { ["tool"] = tool };
    }

    private static JsonObject ToolInfo(Draft draft, ToolCallIssued call, BeaconExportOptions options)
    {
        var tool = new JsonObject { ["name"] = call.ToolName };
        if (call.ToolName == "shell" && Argument(call, "command") is { } command)
            tool["command"] = draft.Retain(command, primary: false);
        if (ResolvedPath(call, options, call.ToolName is "list_files" or "search_files") is { } path)
            tool["path"] = path;
        return tool;
    }

    // ---- helpers ----

    /// <summary>Tools whose arguments and results are memory content: Corvid's memory tools and the shared, read-only search.</summary>
    internal static bool IsMemoryTool(string name) =>
        name.StartsWith("memory_", StringComparison.Ordinal) || name == "search_memory";

    // McpTool names a server's tool server__tool.
    private static (string Server, string Tool)? McpParts(string name)
    {
        var split = name.IndexOf("__", StringComparison.Ordinal);
        return split > 0 && split + 2 < name.Length ? (name[..split], name[(split + 2)..]) : null;
    }

    private static string? Argument(ToolCallIssued? call, string name) =>
        call is not null && call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The path argument of the file tools, resolved against the working directory. list_files and search_files default to
    // the workspace root, as the tools do.
    private static string? ResolvedPath(ToolCallIssued? call, BeaconExportOptions options, bool defaultsToRoot)
    {
        if (call is null || call.ToolName is not ("read_file" or "list_files" or "search_files" or "write_file" or "patch_file"))
            return null;
        var path = Argument(call, "path") ?? (defaultsToRoot ? "." : null);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (options.WorkingDirectory.Length == 0 || Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(path, options.WorkingDirectory);
    }

    private static string ShellOutput(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : "";
        var stdout = result.TryGetProperty("stdout", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
        var stderr = result.TryGetProperty("stderr", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        return stdout.Length == 0 ? stderr : stderr.Length == 0 ? stdout : stdout + "\n" + stderr;
    }

    /// <summary>One event being built: its classification, entities and what its retained strings went through.</summary>
    private sealed class Draft(string action, string category, string message)
    {
        public string Action { get; } = action;
        public string Category { get; } = category;
        public string Message { get; } = message;
        public string Severity { get; init; } = "info";
        public string? ErrorType { get; set; }
        public bool Memory { get; set; }
        public JsonObject? Tool { get; set; }
        public JsonObject? File { get; init; }
        public JsonObject? Command { get; set; }
        public JsonObject? Mcp { get; init; }
        public JsonObject? Approval { get; set; }
        public JsonObject? Prompt { get; set; }
        public JsonObject? GenAi { get; set; }

        /// <summary>The string the content marker describes.</summary>
        public RetainedText? Primary { get; private set; }

        public bool Redacted { get; private set; }
        public bool Truncated { get; private set; }

        public string Retain(string text, bool primary)
        {
            var retained = BeaconContent.Retain(text);
            Redacted |= retained.Redacted;
            Truncated |= retained.Truncated;
            if (primary && text.Length > 0)
                Primary = retained;
            return retained.Stored;
        }
    }
}
