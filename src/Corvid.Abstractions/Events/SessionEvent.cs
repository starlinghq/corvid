using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corvid.Abstractions.Events;

// Discriminators are persisted wire names, not CLR type names. Changing one requires a migration.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessage), "user_message")]
[JsonDerivedType(typeof(AssistantMessage), "assistant_message")]
[JsonDerivedType(typeof(ToolCallIssued), "tool_call_issued")]
[JsonDerivedType(typeof(ToolResult), "tool_result")]
[JsonDerivedType(typeof(Decision), "decision")]
[JsonDerivedType(typeof(DecisionOutcomeRecorded), "decision_outcome_recorded")]
[JsonDerivedType(typeof(ApprovalRequested), "approval_requested")]
[JsonDerivedType(typeof(ApprovalResolved), "approval_resolved")]
[JsonDerivedType(typeof(ModelSwitched), "model_switched")]
[JsonDerivedType(typeof(Compacted), "compacted")]
[JsonDerivedType(typeof(CompactionExpanded), "compaction_expanded")]
[JsonDerivedType(typeof(MemoryWritten), "memory_written")]
[JsonDerivedType(typeof(SkillChanged), "skill_changed")]
[JsonDerivedType(typeof(GoalSet), "goal_set")]
[JsonDerivedType(typeof(TodosSet), "todos_set")]
[JsonDerivedType(typeof(CheckpointTaken), "checkpoint_taken")]
[JsonDerivedType(typeof(SessionRewound), "session_rewound")]
[JsonDerivedType(typeof(DeliveryQueued), "delivery_queued")]
[JsonDerivedType(typeof(DeliveryResolved), "delivery_resolved")]
[JsonDerivedType(typeof(ProjectBound), "project_bound")]
[JsonDerivedType(typeof(McpToolHeld), "mcp_tool_held")]
[JsonDerivedType(typeof(McpToolAccepted), "mcp_tool_accepted")]
public abstract record SessionEvent;

// Text is the initial message surface. Provider-specific message objects are never stored here.
public sealed record UserMessage(string Text) : SessionEvent;
public sealed record AssistantMessage(string Text) : SessionEvent;
// Round (the turn's generator response that made the call, from 1) and ReadOnly (the tool's risk descriptor said read-only)
// were added by C-026, so the recovery scan can tell a later round from a call run one at a time after approval, and a
// read from a change, from the log alone. Both are omitted when null, so events written before them keep their persisted
// form; ReadOnly is null when the call named no offered tool.
public sealed record ToolCallIssued(string CallId, string ToolName, JsonElement Arguments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Round = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReadOnly = null) : SessionEvent;
public sealed record ToolResult(string CallId, JsonElement Result, bool IsError = false) : SessionEvent;
// CatalogueVersion (the floors configuration in force) and Decider (which tier answered) were added by C-004a; State (the
// TurnState the decider saw) and Question (instructions and options as asked) by C-021, so a decision can be exported as
// a Kev training record; Calibrated (whether the answer came from a calibrated model, DecisionAnswer.Calibrated), also by
// C-021, so calibration never infers it from the decider's name. All are omitted when null, so events written before
// them keep their persisted form. Calibrated is null when there was no answer.
// QuestionId is the request key: a catalogue id, possibly with an instance suffix (clear_results/call-7).
public sealed record Decision(string DecisionId, string QuestionId, ImmutableDictionary<string, double> Probabilities,
    double Confidence, double Threshold, string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogueVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Decider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AskedQuestion? Question = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Calibrated = null) : SessionEvent;
/// <summary>A question as the decider received it. Options keep their order, so score levels keep their index.</summary>
/// <param name="Kind">The wire name of the question kind: <c>noul</c>, <c>choice</c> or <c>score</c>.</param>
/// <param name="Instructions">A JSON string for text instructions, or the JSON object given.</param>
public sealed record AskedQuestion(string Kind, JsonElement Instructions, ImmutableArray<AskedOption> Options);
public sealed record AskedOption(string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);
// Outcomes arrive later; never update the original decision event.
public sealed record DecisionOutcomeRecorded(string DecisionId, string Outcome) : SessionEvent;
// CallId (C-011) links a prompt to the tool call waiting on it. Omitted when null, so events written before it keep their
// persisted form; approvals asked outside a tool call (a memory or skill write) have none.
public sealed record ApprovalRequested(string ApprovalId, string Action, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallId = null) : SessionEvent;
public sealed record ApprovalResolved(string ApprovalId, bool Approved, string Reason) : SessionEvent;
// Profiles are snapshots, not mutable registry references; the slot implementation follows in C-006.
public sealed record ModelSwitched(string Slot, JsonElement OldProfile, JsonElement NewProfile, string Reason) : SessionEvent;
// Summarizer (the profile that wrote the record) and Chunks (how many map calls it took, 1 when the range fit one call) were
// added by C-016. Both are omitted when null, so events written before them keep their persisted form.
public sealed record Compacted(long FromSequence, long ThroughSequence, CompactionSummary Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summarizer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Chunks = null) : SessionEvent;
// C-016: the compaction recorded at CompactedSequence is shown expanded from here on (a switch to a larger window, plan
// §4.2 step 4). Its raw range, or the compactions nested inside it, return to the projection; the summary stays in the log.
public sealed record CompactionExpanded(long CompactedSequence) : SessionEvent;
public sealed record CompactionSummary(string? Goal, ImmutableArray<string> Constraints, ImmutableArray<string> Decisions,
    ImmutableArray<string> Files, ImmutableArray<string> OpenQuestions, string? NextStep);
public sealed record MemoryWritten(string MemoryId, string Operation) : SessionEvent;
public sealed record SkillChanged(string SkillId, string Revision, string Author) : SessionEvent;
// Null clears the standing goal. TodosSet replaces the todo list without changing earlier events.
public sealed record GoalSet(string? Goal) : SessionEvent;
public sealed record TodosSet(ImmutableArray<TodoItem> Items) : SessionEvent;
public sealed record TodoItem(string Id, string Text, bool Completed);
// Revision is the shadow store's commit. CallId, Kind and Paths were added by C-014 and are omitted when null, so events
// written before them keep their persisted form. Kind is a CheckpointKinds name: "before" is the workspace before a tool
// call changed files (Paths: target files that existed but could not be captured, so they are never put back) and "after"
// once it had (Paths: what the call changed); each pair is a write-ledger entry that an undo reverts. "restore" is the
// workspace just before an undo or rollback changed it (Paths: what the restore put back).
public sealed record CheckpointTaken(string CheckpointId, string Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImmutableArray<string>? Paths = null) : SessionEvent;

public static class CheckpointKinds
{
    public const string Before = "before";
    public const string After = "after";
    public const string Restore = "restore";
}

// /undo and /rollback (C-014): the transcript is shown as it was at ThroughSequence, while every event stays in the log.
// Restored lists the workspace paths put back; CheckpointId names the "restore" checkpoint taken just before. Reason is
// "undo" or "rollback".
public sealed record SessionRewound(long ThroughSequence, string Reason, ImmutableArray<string> Restored,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckpointId = null) : SessionEvent;

// The gateway's delivery ledger (C-020, plan §12): an append-only table in the event log, kept in its own session so no
// chat's transcript carries it. A message is queued before it is sent and resolved once the platform has it, so one queued
// and never resolved is sent again after a restart (at least once). Adapter and ChatId say where it goes; ReplyTo is the
// platform message it answers and ThreadId the thread it belongs in. Kind is "reply", "thread", "home", "job" or "notice".
// SourceSession and SourceSequence name the answer it carries. Tainted: the text came from a turn that held outside content
// (plan §10.3), which the platform request tells Vault.
public sealed record DeliveryQueued(string DeliveryId, string Adapter, string ChatId, string Text, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThreadId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyTo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceSession = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SourceSequence = null,
    bool Tainted = false) : SessionEvent;

// Status is a DeliveryStatus name: "delivered" (with the platform's message id when it gave one) or "failed" (given up;
// Detail says why). Readers keep the first resolution of a delivery and ignore any later one.
public sealed record DeliveryResolved(string DeliveryId, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PlatformMessageId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null) : SessionEvent;

// MCP tool pinning (C-022): kept in their own session (McpSessions.Pins), so no chat's transcript carries them. Held: a tool
// of a configured server was not offered because its definition is new since the server was first pinned, changed from its
// pin (Hash against PinnedHash), or its description matched a poisoning pattern (Reason "flagged", Detail the matched text).
// Accepted: the user pinned the definition the process held (/mcp accept), or allowed a flagged one (/mcp allow, Flagged), so
// it is registered; an allowed flagged tool is treated as an untrusted server's, whatever the server's trust.
public sealed record McpToolHeld(string Server, string Tool, string Reason, string Hash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PinnedHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null) : SessionEvent;

public sealed record McpToolAccepted(string Server, string Tool, string Hash, bool Flagged = false) : SessionEvent;

public static class McpSessions
{
    /// <summary>The event-log session that records held and accepted MCP tool definitions (C-022).</summary>
    public const string Pins = "mcp-pins";
}

public static class DeliveryStatus
{
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

// C-025: the project a session works in, so its lessons are recalled and filed under it. ProjectId is the 64-character hex
// hash of the normalized origin remote, main checkout path or workspace path; Source says which ("remote", "checkout",
// "path"). Appended before a turn's user message when the session has no project yet or a different one.
public sealed record ProjectBound(string ProjectId, string Source) : SessionEvent;
