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
[JsonDerivedType(typeof(DetectionMatched), "detection_matched")]
[JsonDerivedType(typeof(ProjectBound), "project_bound")]
[JsonDerivedType(typeof(McpToolHeld), "mcp_tool_held")]
[JsonDerivedType(typeof(McpToolAccepted), "mcp_tool_accepted")]
[JsonDerivedType(typeof(McpPinsForgotten), "mcp_pins_forgotten")]
[JsonDerivedType(typeof(UserSteered), "user_steered")]
[JsonDerivedType(typeof(ClarificationAsked), "clarification_asked")]
[JsonDerivedType(typeof(SessionTitled), "session_titled")]
[JsonDerivedType(typeof(SessionRecapped), "session_recapped")]
[JsonDerivedType(typeof(ApprovalAssessed), "approval_assessed")]
[JsonDerivedType(typeof(EgressRecorded), "egress_recorded")]
[JsonDerivedType(typeof(ChildSpawned), "child_spawned")]
[JsonDerivedType(typeof(ChildFinished), "child_finished")]
[JsonDerivedType(typeof(ChildOf), "child_of")]
public abstract record SessionEvent;

// Text is the initial message surface. Provider-specific message objects are never stored here.
// LocalOnly (A4: the composer's `!!` line, docs/plan/tui.md §4.1) marks the message of a turn that ran a shell command for the
// user with its output kept from the model: the prompt and the episodic index leave the message, its call and the call's
// result out, and the log keeps all of them for audit. Omitted when null, so events written before it keep their form.
// From (A4 review, security M2): who wrote it when it is not the session's own user (a gateway guest, "telegram guest dana
// (1001)"); null for the user's own message, and in every log written before it existed. Such a message is outside content:
// the prompt quotes it as someone else's words, and it answers no question asked of the owner.
public sealed record UserMessage(string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LocalOnly = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? From = null) : SessionEvent;
public sealed record AssistantMessage(string Text) : SessionEvent;
// K-3: a message the user sent into a running turn (steer now, Ctrl+Enter), taken at the turn's next step checkpoint. It is
// part of the conversation like a UserMessage (the generator reads it as the user's words, after the results before it),
// but it starts no turn: turn counting, undo and compaction, which look for UserMessage, never see it. It does not clear
// taint (plan §10.3): it joins a context that may already hold outside content.
public sealed record UserSteered(string Text) : SessionEvent;
// K-6: the clarifying question a Clarifying turn ended on, appended just after its AssistantMessage (whose text is the
// question too, so a reader that knows only messages loses nothing). Options are answers the person can pick; empty when
// the question is free text only. QuestionId is unique in the session. The person's answer is their next message.
public sealed record ClarificationAsked(string QuestionId, string Text, ImmutableArray<string> Options) : SessionEvent;
// Round (the turn's generator response that made the call, from 1) and ReadOnly (the tool's risk descriptor said read-only)
// were added by C-026, so the recovery scan can tell a later round from a call run one at a time after approval, and a
// read from a change, from the log alone. Both are omitted when null, so events written before them keep their persisted
// form; ReadOnly is null when the call named no offered tool. LocalOnly (A4) is a call the user ran from the composer's `!!`
// line, which the prompt and the episodic index leave out with its result; omitted when null too.
public sealed record ToolCallIssued(string CallId, string ToolName, JsonElement Arguments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Round = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReadOnly = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LocalOnly = null) : SessionEvent;
// LocalOnly (A4) marks the result of a `!!` call, so a reader of one event (the episodic index) leaves it out without
// finding its call; omitted when null.
public sealed record ToolResult(string CallId, JsonElement Result, bool IsError = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LocalOnly = null) : SessionEvent;
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
// Scope (the answer's scope as the pipeline applied it: once, session or always) and Note (the reason the person gave with
// a denial) were added in A3 so a replayed session shows an answer as the live one did. Both are omitted when null, so
// events written before them keep their persisted form.
public sealed record ApprovalResolved(string ApprovalId, bool Approved, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null) : SessionEvent;
// Profiles are snapshots, not mutable registry references; the slot implementation follows in C-006.
public sealed record ModelSwitched(string Slot, JsonElement OldProfile, JsonElement NewProfile, string Reason) : SessionEvent;
// Summarizer (the profile that wrote the record) and Chunks (how many map calls it took, 1 when the range fit one call) were
// added by C-016. Both are omitted when null, so events written before them keep their persisted form. Focus (C-041) is
// what the user asked `/compact <focus>` to keep, recorded for audit and trace and not rendered; omitted when null too.
public sealed record Compacted(long FromSequence, long ThroughSequence, CompactionSummary Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summarizer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Chunks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Focus = null) : SessionEvent;
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
// C-033: Held with Reason "injection" (Detail the decision id) for a tool withheld after a confident injection verdict over its
// description; Accepted with Flagged for one the user then allowed. C-037: no new injection holds come from the turn loop;
// persisted ones are still Held at listing until allowed. Forgotten: /mcp forget or corvid mcp forget dropped the
// server's entry (Tools pins), so its next listing is trusted on first use again.
public sealed record McpToolHeld(string Server, string Tool, string Reason, string Hash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PinnedHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null) : SessionEvent;

public sealed record McpToolAccepted(string Server, string Tool, string Hash, bool Flagged = false) : SessionEvent;

public sealed record McpPinsForgotten(string Server, int Tools) : SessionEvent;

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

// A detection rule matched (C-023): RuleId and RuleVersion name the rule, Severity and Posture are as the rule declares them
// (Posture "detect" when enforcement is off), Reason is its explanation, and Sequences are the logged events that matched.
// Inline at pre_execute, CallId is the pending tool call that completed the match (it is not yet a matched sequence) and
// Enforced is "ask" when the rule forced an approval prompt. Both are omitted when null, as for post-hoc records. A rule
// records at most one per session. Rules never select this event, so a detection never feeds another detection.
public sealed record DetectionMatched(string RuleId, int RuleVersion, string Severity, string Posture, string Reason,
    ImmutableArray<long> Sequences,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Enforced = null) : SessionEvent;
// G-6 (extends K-9, docs/plan/ui.md §9): the session's title, as lists and headers show it. Source is a TitleSources name:
// "generated" when the local generator wrote it after the first exchange, "user" for a rename. The latest one is the title;
// a generated title is appended only while the session has none, so it never replaces a rename. Session metadata: never in
// the transcript or the prompt, and a rewind does not take it back.
public sealed record SessionTitled(string Title, string Source) : SessionEvent;

public static class TitleSources
{
    public const string Generated = "generated";
    public const string User = "user";
}

// G-6: a few lines the local generator wrote about the session's events in (FromSequence, ThroughSequence], for a person
// coming back to it (docs/plan/ui.md §4.5). Source is a RecapSources name: "requested" (/recap, the app's Recap) or
// "absence" (the app opened the session after the person had been away). Display only: never in the transcript or the
// prompt.
public sealed record SessionRecapped(string Text, long FromSequence, long ThroughSequence, string Source) : SessionEvent;

public static class RecapSources
{
    public const string Requested = "requested";
    public const string Absence = "absence";
}

// C-025: the project a session works in, so its lessons are recalled and filed under it. ProjectId is the 64-character hex
// hash of the normalized origin remote, main checkout path or workspace path; Source says which ("remote", "checkout",
// "path"). Appended before a turn's user message when the session has no project yet or a different one.
public sealed record ProjectBound(string ProjectId, string Source) : SessionEvent;

// G-2: the approval pipeline's verdict on a tool call that asked nobody (a prompt is ApprovalRequested and ApprovalResolved
// instead), appended when the call is authorized, so a surface shows why a call ran unasked (or was refused unasked), live
// and on replay. Resolution is an ApprovalResolutions name: "automatic" (read-only, or the mode and the risk answer allowed
// it), "rule_allowed", "rule_denied", "hardline" or "no_approver" (an ask with nobody to answer: a scheduled run). Mode is
// the approval mode in force ("manual", "smart", "permissive"). Reasons are the pipeline's, code and detail. Rule is the
// remembered rule that decided, in words (`verb sub` under paths), and RuleScope "session" or "always". Risk is the logged
// risk decision when one was asked. Effects (the pipeline's effect names, lower case) and Contained (every path target in
// the workspace) say what the action does, for "in workspace, no network". Optional members are omitted when null.
public sealed record ApprovalAssessed(string CallId, string Resolution, string Mode, ImmutableArray<AssessmentReason> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rule = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AssessedRisk? Risk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImmutableArray<string>? Effects = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Contained = null) : SessionEvent;

/// <summary>One of the pipeline's reasons: its code (<c>read_only</c>, <c>rule_allowed</c>, <c>risk</c>, a hardline rule…) and sentence.</summary>
public sealed record AssessmentReason(string Code, string Detail);

/// <summary>
/// The <c>risk</c> decision an assessment used: its <c>Decision</c> event and action, the level (0 to 4, the chosen option's
/// index) and option, the confidence, whether the answer was calibrated and which decider gave it.
/// </summary>
public sealed record AssessedRisk(string DecisionId, string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Level = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Option = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Calibrated = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Decider = null);

/// <summary>The wire names of <see cref="ApprovalAssessed.Resolution"/>.</summary>
public static class ApprovalResolutions
{
    public const string Automatic = "automatic";
    public const string RuleAllowed = "rule_allowed";
    public const string RuleDenied = "rule_denied";
    public const string Hardline = "hardline";
    public const string NoApprover = "no_approver";
}

// G-9: a request to a gated model (every cloud model, by default) passed the privacy egress gate (plan §10.4). RequestId names
// it in the egress store (egress.db beside the log), which keeps the request as sent, after redaction, so the log stays small;
// Model is the target profile's id and Slot the binding's slot. Outcome is an EgressOutcomes name: "sent" (nothing to redact,
// or the policy sends it), "redacted", or "refused" (nothing left the machine and nothing is kept). Labels counts the distinct
// values of each label the scan found, Placeholders the gate's placeholders in the request as sent, Bytes its UTF-8 size as
// rendered, Reason the gate's words (labels and counts, never a value) and Tainted whether the gate treated it as tainted.
// Host is where it went and Secret the Vault secret it authenticated with (its name, never its value). Unkept says why the
// request as sent could not be stored, when it could not. Optional members are omitted when null.
public sealed record EgressRecorded(string RequestId, string Model, string Outcome, ImmutableDictionary<string, int> Labels,
    int Placeholders, long Bytes, string Reason, bool Tainted = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Slot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unkept = null) : SessionEvent;

/// <summary>The wire names of <see cref="EgressRecorded.Outcome"/>.</summary>
public static class EgressOutcomes
{
    public const string Sent = "sent";
    public const string Redacted = "redacted";
    public const string Refused = "refused";
}

// A5 (plan §14, delegate_task): a child session's limits as it was started with them, after the parent's ceilings: the most
// generator steps, tokens (prompt and completion as providers report them) and seconds it may use.
public sealed record ChildBudget(int MaxSteps, int MaxTokens, int MaxSeconds);

// A5: in the parent's log, a child session was started from it. ChildId is the child's session (whose log starts with ChildOf);
// Goal what it was asked (the parent model's or the person's words: untrusted); Tools the toolset it was given, by name, after
// the forbidden ones were taken out; Mix the slots it runs on, by catalogue name ({"generator":"qwen3.5-9b",...}, the
// ModelMix shape), omitted when it runs on the parent's; OutputSchema the JSON Schema its answer must meet, when one was
// given; By "agent" (a delegate_task call, CallId naming it) or "user" (/delegate, the app); Tainted whether it starts with
// the parent's outside content. Optional members are omitted when null.
public sealed record ChildSpawned(string ChildId, string Goal, ImmutableArray<string> Tools, ChildBudget Budget, string By,
    bool Tainted = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Mix = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? OutputSchema = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallId = null) : SessionEvent;

// A5: in the parent's log, a child session ended. Status is a ChildStatuses name; Text its answer (model text: untrusted), cut
// to a readable size; Steps and Tokens what it used; Detail why it ended other than answered (the budget it ran out of, the
// schema error, the failure); OutputValid whether the answer met the output schema (omitted when none was given); Tainted
// whether it ended holding outside content.
public sealed record ChildFinished(string ChildId, string Status, int Steps, long Tokens, bool Tainted = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OutputValid = null) : SessionEvent;

// A5: the first event of a child session's log: whose child it is (ParentId), how deep (1: the parent is a top-level session),
// and what it was started with, as on the parent's ChildSpawned.
public sealed record ChildOf(string ParentId, int Depth, string Goal, ImmutableArray<string> Tools, ChildBudget Budget,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Mix = null) : SessionEvent;

/// <summary>The wire names of <see cref="ChildFinished.Status"/>.</summary>
public static class ChildStatuses
{
    public const string Answered = "answered";
    public const string Failed = "failed";
    /// <summary>The kernel's <c>stuck</c> question acted with nowhere to escalate: the stall monitor.</summary>
    public const string Stuck = "stuck";
    public const string StepBudgetExhausted = "step_budget_exhausted";
    public const string TokenBudgetExhausted = "token_budget_exhausted";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
    /// <summary>It answered, but not in the output schema's shape.</summary>
    public const string InvalidOutput = "invalid_output";
}
