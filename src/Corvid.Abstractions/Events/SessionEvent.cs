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
[JsonDerivedType(typeof(MemoryWritten), "memory_written")]
[JsonDerivedType(typeof(SkillChanged), "skill_changed")]
[JsonDerivedType(typeof(GoalSet), "goal_set")]
[JsonDerivedType(typeof(TodosSet), "todos_set")]
[JsonDerivedType(typeof(CheckpointTaken), "checkpoint_taken")]
public abstract record SessionEvent;

// Text is the initial message surface. Provider-specific message objects are never stored here.
public sealed record UserMessage(string Text) : SessionEvent;
public sealed record AssistantMessage(string Text) : SessionEvent;
public sealed record ToolCallIssued(string CallId, string ToolName, JsonElement Arguments) : SessionEvent;
public sealed record ToolResult(string CallId, JsonElement Result, bool IsError = false) : SessionEvent;
public sealed record Decision(string DecisionId, string QuestionId, ImmutableDictionary<string, double> Probabilities,
    double Confidence, double Threshold, string Action) : SessionEvent;
// Outcomes arrive later; never update the original decision event.
public sealed record DecisionOutcomeRecorded(string DecisionId, string Outcome) : SessionEvent;
public sealed record ApprovalRequested(string ApprovalId, string Action, string Reason) : SessionEvent;
public sealed record ApprovalResolved(string ApprovalId, bool Approved, string Reason) : SessionEvent;
// Profiles are snapshots, not mutable registry references; the slot implementation follows in C-006.
public sealed record ModelSwitched(string Slot, JsonElement OldProfile, JsonElement NewProfile, string Reason) : SessionEvent;
public sealed record Compacted(long FromSequence, long ThroughSequence, CompactionSummary Summary) : SessionEvent;
public sealed record CompactionSummary(string? Goal, ImmutableArray<string> Constraints, ImmutableArray<string> Decisions,
    ImmutableArray<string> Files, ImmutableArray<string> OpenQuestions, string? NextStep);
public sealed record MemoryWritten(string MemoryId, string Operation) : SessionEvent;
public sealed record SkillChanged(string SkillId, string Revision, string Author) : SessionEvent;
// Null clears the standing goal. TodosSet replaces the todo list without changing earlier events.
public sealed record GoalSet(string? Goal) : SessionEvent;
public sealed record TodosSet(ImmutableArray<TodoItem> Items) : SessionEvent;
public sealed record TodoItem(string Id, string Text, bool Completed);
public sealed record CheckpointTaken(string CheckpointId, string Revision) : SessionEvent;
