using System.Collections.Immutable;

namespace Corvid.App.Core.Sessions;

/// <summary>One question a checkpoint asked the decider, and what it answered.</summary>
/// <param name="Question">The catalogue question ("intent", "risk", "clarify").</param>
/// <param name="Confidence">The calibrated probability of the acted answer.</param>
/// <param name="Action">What the answer does ("task", "escalate", "ask", "read_file").</param>
/// <param name="Acted">Whether it cleared its floor and acted; below it the answer is unsure and never acts on its own.</param>
public sealed record DecisionAnswer(string Question, double Confidence, string Action, bool Acted)
{
    /// <summary>The option chosen when it differs from the action, such as a <c>risk</c> level ("3" for "risk 3 → ask").</summary>
    public string? Option { get; init; }
}

/// <summary>
/// A checkpoint's decisions (<c>_corvid/decision</c>, from the fabric's <c>CheckpointDecisions</c> on the feed): the
/// transcript's chips and the Decisions panel. The feed carries the checkpoint, the decider and each question's
/// confidence, action and acted flag today; the chosen option and the latency need it to carry the span's option tag and
/// duration (K-10).
/// </summary>
/// <param name="Checkpoint">ingress, step, pre_execute, egress…</param>
/// <param name="Decider">The tier that answered ("Kev-0.8B").</param>
public sealed record CheckpointDecided(string Checkpoint, string Decider, ImmutableArray<DecisionAnswer> Answers) : SessionUpdate
{
    public TimeSpan? Latency { get; init; }
}

/// <summary>Why the generator changed.</summary>
public enum ModelSwitchKind
{
    /// <summary>A step escalated to a larger or cloud model ("escalated to claude: step budget").</summary>
    Escalated,
    /// <summary>The privacy gate refused the cloud model, so the step stayed local.</summary>
    KeptLocal,
    /// <summary>A model failed and its fallback took over.</summary>
    FellBack,
    /// <summary>The user switched it (<c>/model</c>, Alt+P).</summary>
    Switched,
    /// <summary>A5: the turn ended and it went back to the session's default (de-escalation, or a one-turn switch undone).</summary>
    Returned,
}

/// <summary>
/// A5: one slot's model for this session changed, or the person's choice of it did (<c>_corvid/slot_model</c>, from the
/// host's <c>ModelChanged</c>): any slot (generator, decider, extractor…), for the session's model strip and the Models
/// page's per-session column. The generator's change also comes as <see cref="ModelSwitched"/>, which is the transcript's
/// row; this one adds no row. Model ids come from configuration: shown through <see cref="SafeText"/>.
/// </summary>
/// <param name="Slot">The slot's name (<c>generator</c>, <c>decider</c>…).</param>
/// <param name="From">The profile id it left (<c>none</c> for a slot nothing bound: a decider chosen on the degraded tier).</param>
/// <param name="To">The profile id it uses now (<c>none</c> when let go of).</param>
/// <param name="Reason">The wire reason: <c>session</c>, <c>user</c>, <c>escalation</c>, <c>fallback</c>, <c>de_escalation</c> or <c>privacy</c>.</param>
public sealed record SlotModelChanged(string Slot, string From, string To, string Reason) : SessionUpdate
{
    /// <summary>The change made <see cref="To"/> the slot's default for this session (the person's "this session" choice).</summary>
    public bool SessionDefault { get; init; }

    /// <summary>The slot is pinned: a turn's end leaves it where it is.</summary>
    public bool Pinned { get; init; }

    /// <summary>The model is a cloud one: its requests leave the machine through the egress gate.</summary>
    public bool Cloud { get; init; }
}

/// <summary>
/// The generator changed (<c>_corvid/model_switched</c>, from <c>ModelSwitched</c> and the turn result's escalations): the
/// escalation row, with what left the machine.
/// </summary>
/// <param name="Reason">Why, in words ("step budget (difficulty 0.71)").</param>
public sealed record ModelSwitched(string Slot, string From, string To, ModelSwitchKind Kind, string Reason) : SessionUpdate
{
    /// <summary>Values the privacy gate redacted from the request that left the machine.</summary>
    public int? Redacted { get; init; }

    /// <summary>The model is a cloud one: requests leave the machine, and "what left the machine" opens them (G-9).</summary>
    public bool Cloud { get; init; }
}

/// <summary>
/// Outside content entered the context, or the kernel cleared it (<c>_corvid/taint</c>): the taint row and the badge
/// (docs/plan/ui.md F9). The turn loop knows when taint starts; reporting the change on the stream is part of K-10.
/// </summary>
public sealed record TaintChanged(bool Tainted) : SessionUpdate
{
    /// <summary>What brought it in ("web_fetch docs.example.org").</summary>
    public string? Source { get; init; }
}

/// <summary>A file a call or the session changed, with its line counts; one Corvid may not read has no counts.</summary>
public sealed record FileChange(string Path, int Added, int Removed)
{
    /// <summary>False for a path the file tools' rules keep from Corvid ("not readable by Corvid").</summary>
    public bool Readable { get; init; } = true;
}

/// <summary>
/// The files changed this turn and this session (<c>_corvid/changes</c>), for the dock's Changes and the Review badge.
/// The write ledger has the paths and the checkpoints the counts come from (C-014); "since the last review" needs the
/// review points of G-7.
/// </summary>
public sealed record ChangesUpdate : SessionUpdate
{
    public ImmutableArray<FileChange> Turn { get; init; } = [];

    public ImmutableArray<FileChange> Session { get; init; } = [];

    /// <summary>The turn the session's list counts from ("since turn 1").</summary>
    public int SinceTurn { get; init; } = 1;
}

/// <summary>A checkpoint's place in the write ledger: before a call changed files, after it, or before a restore.</summary>
public enum CheckpointKind
{
    Before,
    After,
    Restore,
}

/// <summary>
/// A checkpoint was taken (<c>_corvid/checkpoint</c>, from <c>CheckpointTaken</c>): a ledger entry for Rewind. An
/// <see cref="CheckpointKind.After"/> checkpoint carries what the call changed.
/// </summary>
public sealed record CheckpointRecorded(string CheckpointId, CheckpointKind Kind) : SessionUpdate
{
    public string? CallId { get; init; }

    /// <summary>The call's tool ("patch_file").</summary>
    public string? Tool { get; init; }

    /// <summary>The call's target, for a ledger row ("rm -rf build/ &amp;&amp; dotnet test").</summary>
    public string? Target { get; init; }

    /// <summary>For an after checkpoint: the before checkpoint of the same call.</summary>
    public string? Before { get; init; }

    /// <summary>The turn the call belongs to, which a rewind to before it removes.</summary>
    public int? Turn { get; init; }

    public ImmutableArray<FileChange> Changes { get; init; } = [];
}

/// <summary>What a restore puts back.</summary>
public enum RewindScope
{
    FilesAndConversation,
    /// <summary>The transcript goes back; the files stay. Needs host work: today's undo and rollback restore both.</summary>
    ConversationOnly,
    /// <summary>The files go back; the transcript stays. Needs host work too (G-3 restores one file).</summary>
    FilesOnly,
}

/// <summary>A path a restore would not change, and why: the user's own edit conflicts.</summary>
public sealed record RestoreConflict(string Path, string Reason);

/// <summary>
/// The session was rewound (<c>_corvid/rewind</c>, from <c>SessionRewound</c> and the <c>RestoreResult</c>): the transcript
/// shows the session as it was before <see cref="Turn"/>, and a marker lists what was restored, merged and refused.
/// Earlier events stay in the log and in Audit.
/// </summary>
/// <param name="Turn">The first turn that leaves the transcript.</param>
/// <param name="Reason"><c>undo</c> or <c>rollback</c>.</param>
public sealed record SessionRewound(int Turn, RewindScope Scope, string Reason) : SessionUpdate
{
    public ImmutableArray<string> Restored { get; init; } = [];

    public ImmutableArray<string> Merged { get; init; } = [];

    public ImmutableArray<RestoreConflict> Conflicts { get; init; } = [];

    /// <summary>The restore checkpoint taken just before the files changed.</summary>
    public string? CheckpointId { get; init; }

    /// <summary>
    /// The checkpoint the files go back to: a call's before checkpoint or a restore point. The calls from it on are the
    /// ones whose changes were put back, which may start part-way through <see cref="Turn"/>; null means every call of
    /// that turn and after.
    /// </summary>
    public string? RestorePoint { get; init; }

    /// <summary>
    /// The call the conversation went back to before, when it was inside the turn before <see cref="Turn"/> (the engine's
    /// rollback keeps the turn's prompt in the log): that turn keeps its prompt and what came before the call, and the call's
    /// card and everything after it leave. Null when only whole turns leave.
    /// </summary>
    public string? FromCallId { get; init; }

    /// <summary>
    /// With <see cref="FromCallId"/>: what the kept turn's header says when nothing of its answer is left, as a replay of the
    /// log marks such a turn interrupted. Null leaves the header as it was.
    /// </summary>
    public string? UnansweredError { get; init; }

    /// <summary>Nothing changed: a conflict, or the plan could not be applied (<see cref="Detail"/> says why).</summary>
    public bool Refused { get; init; }

    public string? Detail { get; init; }
}

/// <summary>The generator slot as the status bar shows it: "claude (escalated) · cuda:0 · q0 · 410 ms".</summary>
public sealed record GeneratorStatus(string Model, string Device)
{
    public bool Escalated { get; init; }

    /// <summary>Requests waiting on the device.</summary>
    public int Queue { get; init; }

    /// <summary>The median latency of recent calls (<c>ModelMetrics</c>).</summary>
    public TimeSpan? Latency { get; init; }

    public bool Cloud { get; init; }
}

/// <summary>Model loading at start ("loading qwen3.5-0.8b 2 of 4"). Needs K-4.</summary>
public sealed record LoadProgress(string Model, int Index, int Count);

/// <summary>
/// The engine's state for the session (<c>_corvid/engine</c>): the generator slot, the decider tier, the scheduler, the
/// workspace and branch, and model loading. A null member leaves that part as it was. The workspace and project come from
/// the runtime (<c>ProjectBound</c>); the git branch is read by the host.
/// </summary>
public sealed record EngineStatus : SessionUpdate
{
    public GeneratorStatus? Generator { get; init; }

    /// <summary>
    /// The generator the session's next turn starts on ("qwen3.5-0.8b"), when the engine knows no more of it than the name:
    /// the status bar's model before a turn runs. <see cref="Generator"/> wins when both are set.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>The decider tier ("Kev-0.8B, calibrated"; "degraded tier").</summary>
    public string? Decider { get; init; }

    /// <summary>The scheduler ("here · 2 jobs", "held by another process", "off").</summary>
    public string? Scheduler { get; init; }

    public string? Workspace { get; init; }

    public string? Branch { get; init; }

    /// <summary>The project the session is bound to, whose lessons apply.</summary>
    public string? Project { get; init; }

    /// <summary>Models loading; null when nothing is loading (K-4).</summary>
    public LoadProgress? Loading { get; init; }
}

/// <summary>
/// A clarifying question (<c>_corvid/question</c>): options as buttons and a free-text answer, pinned until answered.
/// K-6: the log's <c>ClarificationAsked</c>, the structured question a <c>Clarifying</c> turn ended on (the decider's
/// clarify, or the generator's <c>ask_user</c> with its options). Nothing answers it but the person: their next message, or (A5) their answer inside the turn where a surface can take it.
/// </summary>
public sealed record QuestionAsked(string QuestionId, string Text) : SessionUpdate
{
    /// <summary>The question in a few words, for the attention tray ("which version number?").</summary>
    public string? Title { get; init; }

    public ImmutableArray<string> Options { get; init; } = [];

    public bool AllowsFreeText { get; init; } = true;
}

/// <summary>The question was answered: sent as the user's reply, or (A5) taken inside the turn as its call's result; an empty answer withdraws it.</summary>
public sealed record QuestionAnswered(string QuestionId, string Answer) : SessionUpdate;

/// <summary>
/// Compaction replaced turns with a structured record (ACP draft <c>compaction_update</c>, from <c>Compacted</c> and
/// <c>CompactionExpanded</c>): a marker that expands into the record.
/// </summary>
public sealed record CompactionUpdate(int FromTurn, int ThroughTurn) : SessionUpdate
{
    public string? Goal { get; init; }

    public ImmutableArray<string> Constraints { get; init; } = [];

    public ImmutableArray<string> Decisions { get; init; } = [];

    public ImmutableArray<string> Files { get; init; } = [];

    public ImmutableArray<string> OpenQuestions { get; init; } = [];

    public string? NextStep { get; init; }

    /// <summary>The model that wrote the record.</summary>
    public string? Summarizer { get; init; }

    /// <summary>What <c>/compact &lt;focus&gt;</c> asked to keep.</summary>
    public string? Focus { get; init; }

    /// <summary>The range is shown expanded again, on a larger window (<c>CompactionExpanded</c>).</summary>
    public bool Expanded { get; init; }
}

/// <summary>What a notice is about, which picks its row's look and its button.</summary>
public enum NoticeKind
{
    Info,
    Warning,
    /// <summary>"memory staged: 2 facts" with a review button.</summary>
    MemoryStaged,
    /// <summary>"tool held: notes__search changed" with a diff button (<c>McpToolHeld</c>).</summary>
    McpToolHeld,
    McpToolAccepted,
    /// <summary>"job nightly-tidy ran" with "open the run" (<c>JobCompleted</c>).</summary>
    JobFinished,
    /// <summary>The decider tier changed or is degraded.</summary>
    Decider,
    /// <summary>The approval mode changed (the confirmation row).</summary>
    Mode,
    /// <summary>A recap the local generator wrote (G-6): the text is its heading, the detail its lines.</summary>
    Recap,
}

/// <summary>
/// A notice row (ACP draft <c>notice</c>): job completions, held MCP tools, staged memory, decider notices.
/// </summary>
public sealed record Notice(NoticeKind Kind, string Text) : SessionUpdate
{
    /// <summary>A second line, or the hover text.</summary>
    public string? Detail { get; init; }

    /// <summary>What the row's button opens: a session id, an MCP server, a memory id.</summary>
    public string? Target { get; init; }
}
