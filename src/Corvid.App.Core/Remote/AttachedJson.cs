using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Remote;

/// <summary>
/// The update contract on the wire (G-1, docs/plan/ui.md §8): <see cref="SessionUpdate"/> and the records the session
/// methods take and return, as JSON. Source-generated, so the WebAssembly head's trimmer keeps every shape, and polymorphic
/// through a contract modifier (no attribute on the contract's records): each update carries its kind in
/// <c>sessionUpdate</c>, ACP's discriminator, named as ACP names it where ACP has the update (<c>agent_message_chunk</c>,
/// <c>tool_call</c>, <c>plan</c>…) and in snake case otherwise. An update of a kind this side does not know fails to read,
/// and the connection reports it rather than skipping it.
/// </summary>
public static class AttachedJson
{
    /// <summary>The discriminator's property, as ACP's <c>session/update</c> names it.</summary>
    public const string KindProperty = "sessionUpdate";

    // ACP's names for the updates it has (agent-client-protocol schema, session/update); Corvid's own go as _corvid/update.
    private static readonly FrozenDictionary<Type, string> AcpKinds = new Dictionary<Type, string>
    {
        [typeof(UserMessage)] = "user_message_chunk",
        [typeof(AgentMessageChunk)] = "agent_message_chunk",
        [typeof(AgentThoughtChunk)] = "agent_thought_chunk",
        [typeof(ToolCall)] = "tool_call",
        [typeof(ToolCallUpdate)] = "tool_call_update",
        [typeof(PlanUpdate)] = "plan",
        [typeof(AvailableCommandsUpdate)] = "available_commands_update",
    }.ToFrozenDictionary();

    /// <summary>Every update kind the contract has. A record added to the contract must be added here and to the context.</summary>
    public static IReadOnlyList<Type> UpdateTypes { get; } =
    [
        typeof(UserMessage), typeof(AgentMessageChunk), typeof(AgentThoughtChunk), typeof(TurnStarted), typeof(TurnProgress),
        typeof(TurnEnded), typeof(UsageUpdate), typeof(PlanUpdate), typeof(TitleChanged), typeof(GoalChanged),
        typeof(ApprovalModeChanged), typeof(SessionForked), typeof(DriverChanged), typeof(AvailableCommandsUpdate),
        typeof(CommandResult), typeof(QueueChanged), typeof(ChildSessionChanged), typeof(ChildOfSession),
        typeof(CheckpointDecided), typeof(SlotModelChanged), typeof(ModelSwitched), typeof(TaintChanged), typeof(ChangesUpdate),
        typeof(CheckpointRecorded), typeof(SessionRewound), typeof(EngineStatus), typeof(QuestionAsked), typeof(QuestionAnswered),
        typeof(CompactionUpdate), typeof(Notice), typeof(ApprovalRequested), typeof(ApprovalResolved), typeof(DetectionMatched),
        typeof(ToolCall), typeof(ToolCallUpdate), typeof(TurnPlanned),
    ];

    /// <summary>The wire name of an update kind.</summary>
    public static string KindOf(Type type) => AcpKinds.TryGetValue(type, out var acp) ? acp : SnakeCase(type.Name);

    /// <summary>Whether the kind is one of ACP's, sent as <c>session/update</c>; Corvid's own go as <c>_corvid/update</c>.</summary>
    public static bool IsAcpKind(Type type) => AcpKinds.ContainsKey(type);

    /// <summary>The options both ends use: camel case, enums as strings, the update union, nothing read that is not declared.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonTypeInfo<SessionUpdate> Update => (JsonTypeInfo<SessionUpdate>)Options.GetTypeInfo(typeof(SessionUpdate));

    public static JsonTypeInfo<SessionUpdate[]> Updates => (JsonTypeInfo<SessionUpdate[]>)Options.GetTypeInfo(typeof(SessionUpdate[]));

    public static JsonTypeInfo<SessionSummary> Summary => (JsonTypeInfo<SessionSummary>)Options.GetTypeInfo(typeof(SessionSummary));

    public static JsonTypeInfo<SessionSummary[]> Summaries => (JsonTypeInfo<SessionSummary[]>)Options.GetTypeInfo(typeof(SessionSummary[]));

    public static JsonTypeInfo<QueuedMessage> Queued => (JsonTypeInfo<QueuedMessage>)Options.GetTypeInfo(typeof(QueuedMessage));

    public static JsonTypeInfo<ApprovalAnswer> Answer => (JsonTypeInfo<ApprovalAnswer>)Options.GetTypeInfo(typeof(ApprovalAnswer));

    public static JsonTypeInfo<SessionDraft> Draft => (JsonTypeInfo<SessionDraft>)Options.GetTypeInfo(typeof(SessionDraft));

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(AttachedJsonContext.Default.Options)
        {
            TypeInfoResolver = AttachedJsonContext.Default.WithAddedModifier(info =>
            {
                if (info.Type != typeof(SessionUpdate))
                    return;
                var polymorphism = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = KindProperty,
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var type in UpdateTypes)
                    polymorphism.DerivedTypes.Add(new JsonDerivedType(type, KindOf(type)));
                info.PolymorphismOptions = polymorphism;
            }),
        };
        options.MakeReadOnly();
        return options;
    }

    private static string SnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 64)]
[JsonSerializable(typeof(SessionUpdate))]
[JsonSerializable(typeof(SessionUpdate[]))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(SessionSummary[]))]
[JsonSerializable(typeof(QueuedMessage))]
[JsonSerializable(typeof(ApprovalAnswer))]
[JsonSerializable(typeof(SessionDraft))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AgentMessageChunk))]
[JsonSerializable(typeof(AgentThoughtChunk))]
[JsonSerializable(typeof(TurnPlanned))]
[JsonSerializable(typeof(TurnStarted))]
[JsonSerializable(typeof(TurnProgress))]
[JsonSerializable(typeof(TurnEnded))]
[JsonSerializable(typeof(UsageUpdate))]
[JsonSerializable(typeof(PlanUpdate))]
[JsonSerializable(typeof(TitleChanged))]
[JsonSerializable(typeof(GoalChanged))]
[JsonSerializable(typeof(ApprovalModeChanged))]
[JsonSerializable(typeof(SessionForked))]
[JsonSerializable(typeof(DriverChanged))]
[JsonSerializable(typeof(AvailableCommandsUpdate))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(QueueChanged))]
[JsonSerializable(typeof(ChildSessionChanged))]
[JsonSerializable(typeof(ChildOfSession))]
[JsonSerializable(typeof(CheckpointDecided))]
[JsonSerializable(typeof(SlotModelChanged))]
[JsonSerializable(typeof(ModelSwitched))]
[JsonSerializable(typeof(TaintChanged))]
[JsonSerializable(typeof(ChangesUpdate))]
[JsonSerializable(typeof(CheckpointRecorded))]
[JsonSerializable(typeof(SessionRewound))]
[JsonSerializable(typeof(EngineStatus))]
[JsonSerializable(typeof(QuestionAsked))]
[JsonSerializable(typeof(QuestionAnswered))]
[JsonSerializable(typeof(CompactionUpdate))]
[JsonSerializable(typeof(Notice))]
[JsonSerializable(typeof(ApprovalRequested))]
[JsonSerializable(typeof(ApprovalResolved))]
[JsonSerializable(typeof(DetectionMatched))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ToolCallUpdate))]
internal sealed partial class AttachedJsonContext : JsonSerializerContext;
