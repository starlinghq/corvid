using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Telemetry;

namespace Corvid.Host.Sessions;

/// <summary>
/// One update in a session's stream (K-10, docs/plan/tui.md §3.2): what a surface shows of a session, the same whether it
/// comes live from a running turn (<see cref="SessionUpdateReporter"/>, over the <see cref="TurnFeed"/> and the log) or
/// from the log of a stored one (<see cref="SessionReplay"/>). The records are shaped like ACP's <c>session/update</c>
/// notifications and carry no surface's types: <c>corvid acp</c> maps them onto ACP, the windowed app onto its own update
/// contract.
/// </summary>
public abstract record SessionStreamUpdate
{
    /// <summary>When it happened: the event's record time when it comes from the log, the report's time otherwise.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>The user's message (ACP <c>user_message_chunk</c>, whole), from its <c>UserMessage</c> event.</summary>
/// <param name="Sequence">The event's position in the log: a fork point.</param>
public sealed record PromptSent(string Text, long Sequence) : SessionStreamUpdate
{
    /// <summary>
    /// The generator the turn ran on, as its first decision's TurnState names it ("qwen3.5-0.8b"): replay only, with
    /// <see cref="ReplayOptions.Generators"/>; null live (the surface knows its slot) and when the log has no decision for it.
    /// </summary>
    public string? Generator { get; init; }

    /// <summary>Who wrote it when it is not the session's own user (a gateway guest, security M2); null for the user's own.</summary>
    public string? From { get; init; }

    /// <summary>
    /// K-8 (A6): the images the message carried, as the log refers to them (the bytes are in <see cref="Composition.CorvidRuntime.Media"/>
    /// by digest); empty for a message without any.
    /// </summary>
    public ImmutableArray<Abstractions.Media.ImageRef> Images { get; init; } = [];
}

/// <summary>
/// A message the person steered into a running turn (K-3), from its <c>UserSteered</c> event: part of the turn it was taken
/// by, not the start of one.
/// </summary>
/// <param name="Sequence">The event's position in the log.</param>
public sealed record SteerSent(string Text, long Sequence) : SessionStreamUpdate;

/// <summary>
/// The clarifying question a turn ended on (K-6), from its <c>ClarificationAsked</c> event (after the answer that carries
/// the same text). Model text: untrusted. The person's next message answers it; A5: or, asked inside the turn where a
/// surface could answer, <see cref="QuestionClosed"/> follows with their answer.
/// </summary>
/// <param name="Options">Answers to pick from; empty for free text.</param>
public sealed record QuestionRaised(string QuestionId, string Text, ImmutableArray<string> Options) : SessionStreamUpdate;

/// <summary>
/// A5: the person answered a question inside the running turn (the log's <c>ClarificationAnswered</c>), so it is closed and
/// the turn went on with the answer; live and on replay (<see cref="ReplayOptions.Questions"/>). The answer is the person's.
/// </summary>
public sealed record QuestionClosed(string QuestionId, string Answer) : SessionStreamUpdate;

/// <summary>
/// The running turn began a step (a generator response with tool calls), live from the turn loop; on replay from the step
/// its calls record, with no budget.
/// </summary>
/// <param name="MaxSteps">The turn's step budget; null on replay.</param>
public sealed record TurnStepped(int Step, int? MaxSteps) : SessionStreamUpdate
{
    /// <summary>A5: the generator's profile id whose response began the step; on replay from its calls' log, live unknown (null).</summary>
    public string? Generator { get; init; }
}

/// <summary>Answer text (ACP <c>agent_message_chunk</c>): a streamed delta live, the whole message on replay.</summary>
public sealed record AnswerChunk(string Text) : SessionStreamUpdate;

/// <summary>
/// Reasoning the generator streamed (ACP <c>agent_thought_chunk</c>, K-1): live only, a delta; the log does not keep it,
/// so a replay has none.
/// </summary>
public sealed record ThoughtChunk(string Text) : SessionStreamUpdate;

/// <summary>
/// A generator call's tokens as its provider reported them (K-5): live, one call's; on replay (A5,
/// <see cref="ReplayOptions.Usage"/>) what the log records on the answer or call that followed, which can sum several calls.
/// The prompt's count is the rendered prompt the call sent, which is what a context gauge shows.
/// </summary>
public sealed record UsageReported(long? InputTokens, long? OutputTokens) : SessionStreamUpdate
{
    /// <summary>A5, replay: the latest call's prompt when this sums several calls (<see cref="Calls"/>); null live.</summary>
    public long? PromptTokens { get; init; }

    /// <summary>A5, replay: how many generator calls this covers.</summary>
    public int Calls { get; init; } = 1;
}

/// <summary>A tool call's state, as ACP names it.</summary>
public enum ToolCallState
{
    /// <summary>Announced, not running: waiting for its approval.</summary>
    Pending,
    InProgress,
    Completed,
    Failed,
}

/// <summary>
/// A tool call became known (ACP <c>tool_call</c>): live when the kernel starts it (titled by the tool's name, its input
/// not read yet) or when an approval prompt names it; on replay, finished, with its input.
/// </summary>
/// <param name="Kind">One of <see cref="ToolCallKinds"/>.</param>
public sealed record ToolCallStarted(string CallId, string Title, string Kind, ToolCallState Status) : SessionStreamUpdate
{
    /// <summary>The tool's name, when known (not for a prompt outside any call).</summary>
    public string? Tool { get; init; }

    /// <summary>The call's arguments as logged (credential-scrubbed, C-040).</summary>
    public JsonElement? Input { get; init; }

    /// <summary>Files it names, as absolute paths the tools may open.</summary>
    public ImmutableArray<string> Locations { get; init; } = [];

    /// <summary>A5: the profile id of the generator that asked for it, from the log (<c>ToolCallIssued.Generator</c>); null when unknown.</summary>
    public string? Generator { get; init; }
}

/// <summary>A tool call changed (ACP <c>tool_call_update</c>): it finished, with what it did. Null or empty leaves a part as it was.</summary>
public sealed record ToolCallChanged(string CallId) : SessionStreamUpdate
{
    /// <summary>A5: the profile id of the generator that asked for it, from the log, when the change brings the call's input.</summary>
    public string? Generator { get; init; }

    public string? Title { get; init; }

    public string? Tool { get; init; }

    /// <summary>One of <see cref="ToolCallKinds"/>, when <see cref="Tool"/> is known.</summary>
    public string? Kind { get; init; }

    public ToolCallState? Status { get; init; }

    /// <summary>
    /// How it ended, as the kernel's <c>execute_tool</c> span says: <c>succeeded</c>, <c>failed</c>,
    /// <c>invalid_arguments</c>, <c>denied</c> or <c>cancelled</c>. Live only; a replayed call has only <see cref="Status"/>.
    /// </summary>
    public string? Outcome { get; init; }

    public JsonElement? Input { get; init; }

    /// <summary>The result as logged.</summary>
    public JsonElement? Output { get; init; }

    /// <summary>The result as text, cut to a readable size (<see cref="ToolPresentation.ResultText"/>).</summary>
    public string? OutputText { get; init; }

    public ImmutableArray<string> Locations { get; init; } = [];

    /// <summary>What it changed, from the write ledger's checkpoints around it (C-014).</summary>
    public ImmutableArray<FileDiff> Diffs { get; init; } = [];

    /// <summary>
    /// K-2, live only: the running call's output so far as its last lines, replacing the previous tail (bounded and
    /// credential-scrubbed by the kernel; untrusted). An update with a tail carries nothing else but the call's state.
    /// </summary>
    public string? Tail { get; init; }

    public TimeSpan? Elapsed { get; init; }

    /// <summary>
    /// How the call's approval prompt was answered, from the log (replay only, with <see cref="ReplayOptions.Approvals"/>);
    /// null when nobody was asked. Live, the surface's own prompter knows the answer.
    /// </summary>
    public CallApproval? Approval { get; init; }

    /// <summary>
    /// Why the call ran, or was refused, without anyone being asked (G-2): the approval pipeline's assessment as the log
    /// records it, live and on replay (with <see cref="ReplayOptions.Approvals"/>); null when someone was asked or the call
    /// never reached the pipeline (a tool that declares itself read-only: see <see cref="ReadOnly"/>).
    /// </summary>
    public ApprovalAssessed? Assessment { get; init; }

    /// <summary>
    /// The tool declared itself read-only when it was called (<c>ToolCallIssued.ReadOnly</c>): with no
    /// <see cref="Assessment"/> and no <see cref="Approval"/>, it ran unasked because it only reads. Null when unknown, and
    /// for a call that was asked all the same (a network read in a tainted turn).
    /// </summary>
    public bool? ReadOnly { get; init; }

    /// <summary>The write ledger's checkpoint taken before the call, which makes what it changed reversible (C-014).</summary>
    public string? CheckpointBefore { get; init; }
}

/// <summary>A call's approval prompt and its answer as the log records them (<c>ApprovalRequested</c>, <c>ApprovalResolved</c>).</summary>
/// <param name="Reason">The pipeline's words for the resolution: "user: denied (once)", "timeout: …", "cancelled".</param>
public sealed record CallApproval(string ApprovalId, bool Approved, string Reason)
{
    /// <summary>The person answered (approved or denied), rather than a deadline, a cancellation or a failed prompt.</summary>
    public bool Answered => Reason.StartsWith("user:", StringComparison.Ordinal);

    /// <summary>The answer's scope as applied: <c>once</c>, <c>session</c> or <c>always</c>; null when nobody answered.</summary>
    public string? Scope { get; init; }

    /// <summary>The reason the person gave with a denial.</summary>
    public string? Note { get; init; }
}

/// <summary>The model's task list, replaced whole (ACP <c>plan</c>), from a <c>TodosSet</c> event.</summary>
public sealed record PlanChanged(ImmutableArray<TodoItem> Items) : SessionStreamUpdate;

/// <summary>
/// A checkpoint's decisions: live from the feed; on replay (<see cref="ReplayOptions.Decisions"/>) from the
/// <c>Decision</c> events one checkpoint appended together, its name read from the catalogue's questions.
/// </summary>
public sealed record DecisionsMade(CheckpointDecisions Decisions) : SessionStreamUpdate;

/// <summary>
/// Outside content entered the conversation, or the user's next message cleared it (plan §10.3). In a replay
/// (<see cref="ReplayOptions.Taint"/>), from a network tool's result in the log and the next user message; the log does not
/// record taint from an untrusted description or a hub skill, so a replay shows the network reads only. Live, from a
/// <see cref="SessionUpdateReporter"/> given the session's taint, which it reads in order with the turn (the first report
/// says the taint the turn starts with).
/// </summary>
/// <param name="Source">What brought it in: the tool and what it read ("web_fetch https://example.org").</param>
public sealed record TaintNoted(bool Tainted, string? Source) : SessionStreamUpdate;

/// <summary>
/// A compaction recorded in the log (<c>Compacted</c>), replay only (<see cref="ReplayOptions.Compactions"/>): live, the
/// surface that compacted reports its own result. The replay shows the conversation expanded, so the marker heads its
/// record over the range it summarized.
/// </summary>
/// <param name="Sequence">The event's position; <see cref="Compacted.FromSequence"/> and <c>ThroughSequence</c> the range.</param>
public sealed record CompactionLogged(Compacted Compaction, long Sequence) : SessionStreamUpdate
{
    /// <summary>A later <c>CompactionExpanded</c> shows the range expanded again.</summary>
    public bool Expanded { get; init; }
}

/// <summary>A write-ledger checkpoint was recorded (<c>CheckpointTaken</c>), for a rewind timeline.</summary>
public sealed record CheckpointLogged(CheckpointTaken Checkpoint, long Sequence) : SessionStreamUpdate;

/// <summary>
/// A5: a slot's model changed, or the person's choice of it for the session did (any slot: generator, decider, extractor…).
/// Live from <see cref="Composition.CorvidRuntime.ModelChanged"/> for every switch made in this process, inside a turn
/// (escalation, fallback, a privacy retreat, de-escalation) or between turns (<c>/model</c>,
/// <see cref="Composition.CorvidSession.SetModelAsync"/>); on replay (<see cref="ReplayOptions.Models"/>) from the log's
/// <c>ModelSwitched</c> events, which have no <see cref="SessionDefault"/>, <see cref="Pinned"/> or <see cref="Cloud"/>
/// beyond what the reason says. Profile ids come from configuration: show them through the surface's sanitizer.
/// </summary>
/// <param name="From">The profile id it switched from (<c>none</c> for a slot nothing bound).</param>
/// <param name="To">The profile id it uses now (<c>none</c> when let go of).</param>
/// <param name="Reason">The wire reason: <c>session</c>, <c>user</c>, <c>escalation</c>, <c>fallback</c>, <c>de_escalation</c> or <c>privacy</c>.</param>
/// <param name="Sequence">The <c>ModelSwitched</c> event's position; null when nothing was logged (only the session default changed).</param>
public sealed record ModelChanged(string Slot, string From, string To, string Reason, long? Sequence) : SessionStreamUpdate
{
    /// <summary>The change made <see cref="To"/> the slot's default for the session (the person's "this session" choice).</summary>
    public bool SessionDefault { get; init; }

    /// <summary>The slot is pinned after it: a turn's end leaves it where it is.</summary>
    public bool Pinned { get; init; }

    /// <summary>The model it uses now is a cloud one: its requests leave the machine through the egress gate.</summary>
    public bool Cloud { get; init; }
}

/// <summary>
/// A5: a child session was started from this one (<c>ChildSpawned</c>), live from <see cref="Composition.CorvidRuntime.ChildChanged"/>
/// and on replay (<see cref="ReplayOptions.Children"/>). The goal is untrusted text; the mix's model names are catalogue names.
/// </summary>
public sealed record ChildStarted(ChildSpawned Child, long Sequence) : SessionStreamUpdate;

/// <summary>A5: a child session ended (<c>ChildFinished</c>), live and on replay as <see cref="ChildStarted"/>; its text is untrusted.</summary>
public sealed record ChildEnded(ChildFinished Child, long Sequence) : SessionStreamUpdate;
