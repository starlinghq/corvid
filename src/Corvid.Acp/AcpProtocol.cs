using System.Text.Json;
using System.Text.Json.Serialization;

// The Agent Client Protocol's messages as Corvid uses them, from the published schema (agentclientprotocol.com, schema.json;
// session/fork from schema.unstable.json). Property names are the schema's; unknown members (_meta and anything newer) are
// ignored on input, and null members are left out on output.
namespace Corvid.Acp;

internal static class AcpMethods
{
    public const string Initialize = "initialize";
    public const string Authenticate = "authenticate";
    public const string SessionNew = "session/new";
    public const string SessionLoad = "session/load";
    public const string SessionResume = "session/resume";
    public const string SessionFork = "session/fork";
    public const string SessionList = "session/list";
    public const string SessionClose = "session/close";
    public const string SessionPrompt = "session/prompt";
    public const string SessionCancel = "session/cancel";
    public const string SessionUpdate = "session/update";
    public const string RequestPermission = "session/request_permission";
    public const string CancelRequest = "$/cancel_request";

    /// <summary>
    /// A5: Corvid's extension notification (tui.md §3.2's <c>corvid.question</c>, as ACP's <c>_</c>-prefixed custom methods
    /// go): the clarifying question a prompt turn ended on (K-6), sent before the prompt's response so an editor that
    /// knows it can show its options. A client that does not ignores it, as ACP asks of unknown notifications.
    /// </summary>
    public const string CorvidQuestion = "_corvid/question";
}

/// <summary>JSON-RPC and ACP error codes (schema <c>ErrorCode</c>).</summary>
internal static class AcpErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;
    public const int ResourceNotFound = -32002;
}

/// <summary>A JSON-RPC error answered to a request.</summary>
internal sealed class AcpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed record Implementation(string Name, string Version)
{
    public string? Title { get; init; }
}

internal sealed record InitializeRequest
{
    [JsonRequired]
    public int ProtocolVersion { get; init; }

    public Implementation? ClientInfo { get; init; }
}

/// <summary>An empty capability object: present means supported.</summary>
internal sealed record Supported;

internal sealed record PromptCapabilities(bool Image, bool Audio, bool EmbeddedContext);

internal sealed record McpCapabilities(bool Http, bool Sse);

internal sealed record SessionCapabilities
{
    public Supported? List { get; init; }

    public Supported? Resume { get; init; }

    public Supported? Fork { get; init; }

    public Supported? Close { get; init; }
}

internal sealed record AgentCapabilities(bool LoadSession, PromptCapabilities PromptCapabilities, McpCapabilities McpCapabilities,
    SessionCapabilities SessionCapabilities);

internal sealed record InitializeResponse(int ProtocolVersion, AgentCapabilities AgentCapabilities, Implementation AgentInfo,
    JsonElement[] AuthMethods);

/// <summary>session/new, session/load, session/resume and session/fork share these fields.</summary>
internal sealed record SessionRequest
{
    public string? SessionId { get; init; }

    [JsonRequired]
    public string Cwd { get; init; } = "";

    public JsonElement[]? McpServers { get; init; }

    public string[]? AdditionalDirectories { get; init; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }
}

internal sealed record NewSessionResponse(string SessionId)
{
    /// <summary>A5: the session's configuration options (the generator, the decider); see AcpServer.Models.cs.</summary>
    public ConfigOption[]? ConfigOptions { get; init; }
}

internal sealed record EmptyResponse;

internal sealed record ListSessionsRequest
{
    public string? Cwd { get; init; }

    public string? Cursor { get; init; }
}

internal sealed record SessionInfo(string SessionId, string Cwd)
{
    public string? Title { get; init; }

    public string? UpdatedAt { get; init; }
}

internal sealed record ListSessionsResponse(SessionInfo[] Sessions)
{
    public string? NextCursor { get; init; }
}

internal sealed record SessionIdRequest
{
    [JsonRequired]
    public string SessionId { get; init; } = "";
}

internal sealed record PromptRequest
{
    [JsonRequired]
    public string SessionId { get; init; } = "";

    [JsonRequired]
    public JsonElement[] Prompt { get; init; } = [];
}

internal static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTurnRequests = "max_turn_requests";
    public const string Refusal = "refusal";
    public const string Cancelled = "cancelled";
}

internal sealed record PromptResponse(string StopReason);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
internal abstract record ContentBlock;

internal sealed record TextContent(string Text) : ContentBlock;

/// <summary>K-8 (A6): an image block, base64 data and its media type, as <c>session/load</c> replays a message's images.</summary>
internal sealed record ImageContent(string Data, string MimeType) : ContentBlock;

internal sealed record SessionNotification(string SessionId, SessionUpdate Update);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "sessionUpdate")]
[JsonDerivedType(typeof(UserMessageChunk), "user_message_chunk")]
[JsonDerivedType(typeof(AgentMessageChunk), "agent_message_chunk")]
[JsonDerivedType(typeof(ToolCall), "tool_call")]
[JsonDerivedType(typeof(ToolCallUpdate), "tool_call_update")]
[JsonDerivedType(typeof(AvailableCommandsUpdate), "available_commands_update")]
[JsonDerivedType(typeof(ConfigOptionUpdate), "config_option_update")]
internal abstract record SessionUpdate;

internal sealed record UserMessageChunk(ContentBlock Content) : SessionUpdate;

internal sealed record AgentMessageChunk(ContentBlock Content) : SessionUpdate;

internal static class ToolKinds
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Search = "search";
    public const string Execute = "execute";
    public const string Fetch = "fetch";
    public const string Other = "other";
}

internal static class ToolStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>A new tool call (<c>tool_call</c>).</summary>
internal sealed record ToolCall(string ToolCallId, string Title, string Kind, string Status) : SessionUpdate
{
    public ToolCallContent[]? Content { get; init; }

    public ToolCallLocation[]? Locations { get; init; }

    public JsonElement? RawInput { get; init; }

    public JsonElement? RawOutput { get; init; }
}

/// <summary>Changes to a tool call (<c>tool_call_update</c>); also the tool call shown in a permission request.</summary>
internal sealed record ToolCallUpdate(string ToolCallId) : SessionUpdate
{
    public string? Title { get; init; }

    public string? Kind { get; init; }

    public string? Status { get; init; }

    public ToolCallContent[]? Content { get; init; }

    public ToolCallLocation[]? Locations { get; init; }

    public JsonElement? RawInput { get; init; }

    public JsonElement? RawOutput { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ContentToolCallContent), "content")]
[JsonDerivedType(typeof(DiffToolCallContent), "diff")]
internal abstract record ToolCallContent;

internal sealed record ContentToolCallContent(ContentBlock Content) : ToolCallContent;

/// <summary>A file edit: the whole file before (null for a new file) and after.</summary>
internal sealed record DiffToolCallContent(string Path, string? OldText, string NewText) : ToolCallContent;

internal sealed record ToolCallLocation(string Path)
{
    public int? Line { get; init; }
}

internal sealed record AvailableCommandInput(string Hint);

internal sealed record AvailableCommand(string Name, string Description)
{
    public AvailableCommandInput? Input { get; init; }
}

internal sealed record AvailableCommandsUpdate(AvailableCommand[] AvailableCommands) : SessionUpdate;

internal static class PermissionKinds
{
    public const string AllowOnce = "allow_once";
    public const string AllowAlways = "allow_always";
    public const string RejectOnce = "reject_once";
}

internal sealed record PermissionOption(string OptionId, string Name, string Kind);

internal sealed record RequestPermissionRequest(string SessionId, ToolCallUpdate ToolCall, PermissionOption[] Options);

internal sealed record PermissionOutcome
{
    [JsonRequired]
    public string Outcome { get; init; } = "";

    public string? OptionId { get; init; }
}

internal sealed record RequestPermissionResponse
{
    [JsonRequired]
    public PermissionOutcome Outcome { get; init; } = new();
}

/// <summary>A5: <c>_corvid/question</c>'s parameters. The question and options are the model's text.</summary>
internal sealed record CorvidQuestionNotification(string SessionId, string QuestionId, string Question, string[] Options);

internal sealed record CorvidMeta
{
    /// <summary>For session/fork: the log position to fork at (default the source's head).</summary>
    public long? ThroughSequence { get; init; }
}

internal sealed record JsonRpcError(int Code, string Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // Clients may put a discriminator after other members.
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(NewSessionResponse))]
[JsonSerializable(typeof(EmptyResponse))]
[JsonSerializable(typeof(ListSessionsRequest))]
[JsonSerializable(typeof(ListSessionsResponse))]
[JsonSerializable(typeof(SessionIdRequest))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResponse))]
[JsonSerializable(typeof(SessionNotification))]
[JsonSerializable(typeof(RequestPermissionRequest))]
[JsonSerializable(typeof(RequestPermissionResponse))]
[JsonSerializable(typeof(CorvidMeta))]
[JsonSerializable(typeof(SetConfigOptionRequest))]
[JsonSerializable(typeof(ConfigOptionsResponse))]
[JsonSerializable(typeof(CorvidQuestionNotification))]
[JsonSerializable(typeof(JsonRpcError))]
internal sealed partial class AcpJson : JsonSerializerContext;
