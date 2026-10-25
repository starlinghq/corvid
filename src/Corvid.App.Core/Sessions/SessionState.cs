using System.Collections.Immutable;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// One entry of a session's transcript as the fold builds it: immutable, with an id that stays the same while the entry
/// changes (a tool card running, then done), so the view model can update the block it drew in place. Entries of a turn
/// are contiguous and carry its number; <see cref="Turn"/> 0 is outside any turn (a command run between turns).
/// </summary>
public abstract record TranscriptEntry(string Id)
{
    public int Turn { get; init; }
}

/// <summary>A message the user sent, or a steer delivered into a running turn.</summary>
public sealed record UserEntry(string Id, string Text, DateTimeOffset At) : TranscriptEntry(Id)
{
    public long? Position { get; init; }

    public bool Steered { get; init; }

    /// <summary>Who wrote it when it is not the person (a gateway guest); null for their own.</summary>
    public string? From { get; init; }
}

/// <summary>A turn's header: its number, the generators it ran on in order, and its measures so far.</summary>
public sealed record TurnHeaderEntry(string Id, int Number, DateTimeOffset StartedAt) : TranscriptEntry(Id)
{
    /// <summary>The generators in the order the turn used them ("qwen3.5-0.8b", then "claude").</summary>
    public ImmutableArray<string> Models { get; init; } = [];

    public bool Running { get; init; } = true;

    public TurnOutcome? Outcome { get; init; }

    /// <summary>Set when the turn ends; a running turn's elapsed time is the clock's.</summary>
    public TimeSpan? Elapsed { get; init; }

    public int? Tokens { get; init; }

    public decimal? Cost { get; init; }

    public int? Step { get; init; }

    public int? MaxSteps { get; init; }

    public string? Error { get; init; }
}

/// <summary>One checkpoint's decisions, as a chips row and as a group in the Decisions panel.</summary>
/// <param name="Checkpoint">The fabric's checkpoint name (ingress, step, pre_execute…).</param>
public sealed record DecisionGroup(string Id, int Turn, string Checkpoint, string Decider, DateTimeOffset At, ImmutableArray<DecisionAnswer> Answers)
{
    public TimeSpan? Latency { get; init; }

    /// <summary>A question in it asked the user (<c>risk → ask</c>).</summary>
    public bool Asked => Answers.Any(answer => answer.Action == "ask");
}

/// <summary>
/// A chips row. Each checkpoint has one at its place in the turn, shown with the verbose toggle (Ctrl+Shift+D); and each
/// turn has a <see cref="Summary"/> row under its header, shown otherwise: the checkpoint that asked while its approval
/// is open, so the score sits beside the card, and the ingress checkpoint the rest of the time.
/// </summary>
public sealed record DecisionsEntry(string Id, DecisionGroup Group, bool Summary) : TranscriptEntry(Id);

/// <summary>An escalation, fallback or switch of the generator.</summary>
public sealed record SwitchEntry(string Id, ModelSwitched Switch) : TranscriptEntry(Id);

/// <summary>A tool call's card.</summary>
public sealed record ToolEntry(string Id, string CallId, string Tool, ToolKind Kind, DateTimeOffset StartedAt) : TranscriptEntry(Id)
{
    public ToolCallStatus Status { get; init; }

    public ToolOutcome? Outcome { get; init; }

    public string? Target { get; init; }

    public string? Summary { get; init; }

    /// <summary>The live tail while it runs.</summary>
    public string? Tail { get; init; }

    public string? Output { get; init; }

    public TimeSpan? Elapsed { get; init; }

    public ImmutableArray<FileEdit> Diffs { get; init; } = [];

    public ImmutableArray<string> Locations { get; init; } = [];

    public CallVerdict? Verdict { get; init; }

    public string? Input { get; init; }

    /// <summary>The approval it waits on, while one is open.</summary>
    public string? ApprovalId { get; init; }
}

/// <summary>
/// The open approval card: the first prompt waiting, placed after its tool card. The others wait behind it ("1 of 2"),
/// and the next one is named under the card.
/// </summary>
public sealed record ApprovalEntry(string Id, ApprovalRequested Request, int Position, int Count) : TranscriptEntry(Id)
{
    public ApprovalRequested? Next { get; init; }
}

/// <summary>Taint started or cleared.</summary>
public sealed record TaintEntry(string Id, bool Tainted, string? Source, DateTimeOffset At) : TranscriptEntry(Id);

/// <summary>Answer text: one block per stretch of generator text between other entries; markdown.</summary>
public sealed record AnswerEntry(string Id, string Text, bool Streaming) : TranscriptEntry(Id);

/// <summary>The turn's reasoning, at its foot: "thought for 4 s", expandable.</summary>
/// <param name="Thinking">The time spent thinking in phases that have ended.</param>
/// <param name="PhaseStartedAt">When the open thinking phase began; null when none is open.</param>
public sealed record ThoughtEntry(string Id, string Text, TimeSpan Thinking, DateTimeOffset? PhaseStartedAt) : TranscriptEntry(Id)
{
    public bool Streaming => PhaseStartedAt is not null;
}

public sealed record NoticeEntry(string Id, Notice Notice) : TranscriptEntry(Id);

public sealed record DetectionEntry(string Id, DetectionInfo Detection, DateTimeOffset At) : TranscriptEntry(Id);

public sealed record CompactionEntry(string Id, CompactionUpdate Compaction) : TranscriptEntry(Id);

public sealed record RewindEntry(string Id, SessionRewound Rewind) : TranscriptEntry(Id);

/// <summary>A clarifying question, pinned while open; <see cref="Answer"/> once answered.</summary>
public sealed record QuestionEntry(string Id, QuestionAsked Question) : TranscriptEntry(Id)
{
    public string? Answer { get; init; }
}

/// <summary>A slash command's output.</summary>
public sealed record CommandEntry(string Id, CommandResult Result) : TranscriptEntry(Id);

/// <summary>A call's entry in the write ledger: the checkpoints before and after it and what it changed; or a restore point.</summary>
public sealed record LedgerRow(string Id, CheckpointKind Kind, DateTimeOffset At, int Turn)
{
    public string? CallId { get; init; }

    public string? Tool { get; init; }

    public string? Target { get; init; }

    public string? BeforeId { get; init; }

    public string? AfterId { get; init; }

    public ImmutableArray<FileChange> Changes { get; init; } = [];

    /// <summary>
    /// The paths of <see cref="Changes"/> a later restore put back (<see cref="SessionRewound"/>): the row stays in the
    /// ledger, as every entry does, but those changes are no longer in the files, so a plan does not put them back again
    /// and the session's change lists leave them out.
    /// </summary>
    public ImmutableArray<string> UndonePaths { get; init; } = [];

    /// <summary>The changes still in the files: <see cref="Changes"/> less those a restore put back.</summary>
    public IEnumerable<FileChange> LiveChanges => UndonePaths.IsDefaultOrEmpty ? Changes : Changes.Where(change => !UndonePaths.Contains(change.Path));
}

/// <summary>
/// A session's state for the views: the fold of its updates (<see cref="SessionFold"/>). Immutable; each update gives a
/// new state that shares everything it did not change.
/// </summary>
public sealed record SessionView
{
    public string SessionId { get; init; } = "";

    public string Title { get; init; } = "";

    public string? ForkOf { get; init; }

    public int? ForkTurn { get; init; }

    public string? Goal { get; init; }

    public ApprovalMode? Mode { get; init; }

    /// <summary>Another process drives the session (G-12); this window shows it read-only.</summary>
    public bool ReadOnly { get; init; }

    public string? Holder { get; init; }

    public ImmutableList<TranscriptEntry> Transcript { get; init; } = [];

    /// <summary>The latest turn's number (0 before the first).</summary>
    public int Turn { get; init; }

    /// <summary>Turns the stream has started, in this session's history as loaded.</summary>
    public int Turns { get; init; }

    public bool Running { get; init; }

    public DateTimeOffset? TurnStartedAt { get; init; }

    /// <summary>Since when the running turn has been doing what it does now (its step, its running call).</summary>
    public DateTimeOffset? ActivitySince { get; init; }

    public int? Step { get; init; }

    public int? MaxSteps { get; init; }

    /// <summary>The generator now.</summary>
    public string? Model { get; init; }

    /// <summary>A5: each slot's latest change in this session (any slot), by slot name; empty until one changes.</summary>
    public ImmutableDictionary<string, SlotModelChanged> SlotModels { get; init; } = ImmutableDictionary<string, SlotModelChanged>.Empty;

    /// <summary>The open approval prompts, in the order they will be asked.</summary>
    public ImmutableList<ApprovalRequested> Approvals { get; init; } = [];

    /// <summary>The open clarifying question.</summary>
    public QuestionAsked? Question { get; init; }

    public ImmutableArray<QueuedMessage> Queue { get; init; } = [];

    public bool Tainted { get; init; }

    public string? TaintSource { get; init; }

    public DateTimeOffset? TaintedAt { get; init; }

    public int? ContextTokens { get; init; }

    public int? ContextWindow { get; init; }

    public double? CompactAt { get; init; }

    public ImmutableArray<ContextPart> ContextBreakdown { get; init; } = [];

    public decimal? SessionCost { get; init; }

    public GeneratorStatus? Generator { get; init; }

    public string? Decider { get; init; }

    public string? Scheduler { get; init; }

    public string? Workspace { get; init; }

    public string? Branch { get; init; }

    public string? Project { get; init; }

    public LoadProgress? Loading { get; init; }

    public ImmutableArray<PlanEntry> Plan { get; init; } = [];

    /// <summary>Every checkpoint's decisions, oldest first.</summary>
    public ImmutableList<DecisionGroup> Decisions { get; init; } = [];

    /// <summary>The write ledger, oldest first.</summary>
    public ImmutableList<LedgerRow> Ledger { get; init; } = [];

    public ImmutableArray<FileChange> TurnChanges { get; init; } = [];

    public ImmutableArray<FileChange> SessionChanges { get; init; } = [];

    public int ChangesSinceTurn { get; init; } = 1;

    public ImmutableArray<SlashCommandInfo> Commands { get; init; } = [];

    /// <summary>The latest update's time.</summary>
    public DateTimeOffset LastActivity { get; init; }

    /// <summary>The source of the ids the fold gives entries that have none of their own.</summary>
    internal int NextId { get; init; }

    /// <summary>The id of the latest turn's header, which its chips and thought rows extend.</summary>
    internal string? TurnHeaderId { get; init; }

    /// <summary>A thinking phase is open: the next update that is not a thought chunk ends it.</summary>
    internal bool ThinkingOpen { get; init; }

    /// <summary>The prompt the user answers next, if any.</summary>
    public ApprovalRequested? CurrentApproval => Approvals.IsEmpty ? null : Approvals[0];

    /// <summary>
    /// The session waits on the user: an approval is open, or a question is and no turn runs (a clarifying turn ended on
    /// it). A question asked while the turn goes on waits beside it ("a question is queued for you").
    /// </summary>
    public bool WaitingOnUser => !Approvals.IsEmpty || Question is not null && !Running;

    /// <summary>The context gauge as a fraction; null until usage is reported.</summary>
    public double? ContextFraction => ContextTokens is { } tokens && ContextWindow is > 0 and var window
        ? Math.Clamp((double)tokens / window, 0, 1)
        : null;
}
