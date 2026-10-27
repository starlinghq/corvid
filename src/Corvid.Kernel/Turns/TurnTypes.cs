using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Context;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Tools;

namespace Corvid.Kernel.Turns;

/// <summary>Per-session settings for the turn loop.</summary>
public sealed record TurnOptions
{
    /// <summary>Kept short for local models, which prefill the whole prompt on every call (plan §8).</summary>
    public const string DefaultSystemPrompt =
        "You are Corvid, a helpful assistant running on the user's machine. Use a tool when the request needs one; " +
        "call it rather than describing what it would return. Answer briefly.";

    public static TurnOptions Default { get; } = new();

    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    /// <summary>Generator responses with tool calls a turn may take before it must answer (plan §5.3 <c>done</c>).</summary>
    public int MaxSteps { get; init; } = 8;

    /// <summary>The completion the generator may write; the prompt budget leaves room for it.</summary>
    public int MaxOutputTokens { get; init; } = 1_024;

    /// <summary>Tokens kept free beyond the completion, for the template and estimation error.</summary>
    public int ContextReserve { get; init; } = 256;

    /// <summary>Results newer than this many are never asked about by <c>clear_results</c> (its below-floor rule: the last 2).</summary>
    public int KeepRecentResults { get; init; } = 2;

    /// <summary>At most this many <c>clear_results</c> rows per step checkpoint, oldest first.</summary>
    public int MaxClearQuestions { get; init; } = 8;

    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.Smart;

    /// <summary>
    /// False for scheduled and background turns, where nobody can answer a prompt: an approval that would ask is denied
    /// (C-007b's <see cref="ApprovalContext.Interactive"/>), and tools see it on their <see cref="ToolCallContext"/>.
    /// </summary>
    public bool Interactive { get; init; } = true;

    /// <summary>
    /// A5: false for a turn that may not put a question to the person (a child session's, plan §14: no <c>clarify</c>): the
    /// <c>clarify</c> question is not asked, <c>intent</c>'s <c>ask_clarifying_question</c> never diverts the turn, and
    /// <c>ask_user</c> is not offered, so the turn never ends <see cref="TurnStatus.Clarifying"/>. Its approvals still ask
    /// whoever answers them (<see cref="Interactive"/>).
    /// </summary>
    public bool AsksQuestions { get; init; } = true;

    /// <summary>Labels the extractor looks for in the user message, for the TurnState's <c>entities</c>.</summary>
    public ImmutableArray<string> EntityLabels { get; init; } = ["person", "organization", "place", "date", "time", "project", "file", "url"];

    /// <summary>
    /// Slot pre-fill (plan §7 step 4): an extracted span at least this confident is shown in the repair message beside an
    /// argument that was missing or failed validation, when the span itself would pass. It never rejects a valid call.
    /// </summary>
    public float SlotPrefillConfidence { get; init; } = 0.8f;

    /// <summary>The token budget for memory retrieved when <c>needs_recall</c> acts.</summary>
    public int RecallTokens { get; init; } = 400;

    /// <summary>How many log entries the TurnState's <c>recent</c> draws from.</summary>
    public int RecentEvents { get; init; } = 8;

    /// <summary>Ask <c>deliver</c> at egress; for gateway surfaces, which can reply in a thread or elsewhere.</summary>
    public bool AskDeliver { get; init; }

    /// <summary>Policy allows escalating the generator (privacy and profile ladder, plan §6); the slot ladder decides where.</summary>
    public bool EscalationAllowed { get; init; } = true;

    /// <summary>
    /// A7, architect/editor turns (plan §6): when a coding turn (one offered a tool that changes files)
    /// escalates for difficulty or repair, the escalated model writes one plan without tools and the session's own
    /// model (or <see cref="EditorModel"/>) makes the edits with it; an editor that gets stuck gets one re-plan. Off by
    /// default: escalation then moves the whole turn, as before. <c>/architect</c> asks for it per turn or session.
    /// </summary>
    public bool ArchitectEditor { get; init; }

    /// <summary>
    /// A7: the profile id of the model that applies an architect/editor turn's plan, when it is one of the generator slot's
    /// bindings (its default, a fallback or a rung of its ladder, the mix's for a session with one); null, or an id none of
    /// them has, uses the session's own generator.
    /// </summary>
    public string? EditorModel { get; init; }

    /// <summary>Example requests per tool name, for multi-vector shortlisting (§5.4). C-005 has no examples file yet.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>>? ToolExamples { get; init; }

    /// <summary>Hard filters before the shortlist: toolset enabled, platform allowed, availability.</summary>
    public Func<ToolDescriptor, bool>? ToolFilter { get; init; }

    /// <summary>The Vault catalogue's secret names; tools with secret parameters are offered only with one (§10.5).</summary>
    public IReadOnlyCollection<string>? SecretNames { get; init; }

    /// <summary>Recipients the user has already messaged; in a tainted turn others ask (§10.3).</summary>
    public IReadOnlySet<string>? KnownRecipients { get; init; }

    public TurnStateBudget? StateBudget { get; init; }

    /// <summary>
    /// The TurnState budget for an ingress checkpoint that asks both <c>tool</c> and <c>skill</c> (C-029): six questions,
    /// with the two longest rows. Spike S1 measured that checkpoint on Kev-0.8B (RTX 3090) at 301 ms with a 600-token
    /// state, on the 300 ms line, and at 212 ms with this 300-token one (297 tokens by Corvid's counter, 307 by Kev's).
    /// Every other checkpoint, and the approval context, keep <see cref="StateBudget"/>. Null uses <see cref="StateBudget"/>
    /// here too.
    /// </summary>
    public TurnStateBudget? DualShortlistStateBudget { get; init; } = new(Total: DefaultDualShortlistStateTokens);

    /// <summary>S1's 300-token cell.</summary>
    public const int DefaultDualShortlistStateTokens = 300;

    /// <summary>Compaction (plan §8 lever 3, C-016): the summarizer's completion, the record's budget and expansion's fill.</summary>
    public CompactionOptions Compaction { get; init; } = CompactionOptions.Default;

    /// <summary>
    /// Load this skill's body without asking <c>skill</c>: a skill replay or eval (plan §11), where the skill under test
    /// must be in context whatever the decider would pick.
    /// </summary>
    public string? PinnedSkill { get; init; }

    /// <summary>
    /// K-2: the most of a running call's output (the shell's, web_fetch's) a surface is shown at once: the last lines of the
    /// last characters, replacing the previous tail. The result the model reads is the tool's own, capped by the tool.
    /// </summary>
    public int OutputTailChars { get; init; } = 2_000;

    /// <summary>K-2: lines in a running call's tail.</summary>
    public int OutputTailLines { get; init; } = 12;

    /// <summary>K-2: a running call's tail is reported at most this often; output between reports joins the next one.</summary>
    public TimeSpan OutputTailInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// K-8 (A6 review): the images of this many of the latest user messages go to the generator as images (or descriptions);
    /// an older message's images are a one-line stub naming each, so a picture is not read and sent again on every later
    /// model call. At least 1 (the message being answered).
    /// </summary>
    public int ImageTurns { get; init; } = DefaultImageTurns;

    /// <summary>The message being answered and the one before it, so "and what about the corner?" still sees the picture.</summary>
    public const int DefaultImageTurns = 2;

    public TurnOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(Compaction);
        Compaction.Validate();
        ArgumentNullException.ThrowIfNull(SystemPrompt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxOutputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(ContextReserve);
        ArgumentOutOfRangeException.ThrowIfNegative(KeepRecentResults);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxClearQuestions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RecentEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OutputTailChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OutputTailLines);
        ArgumentOutOfRangeException.ThrowIfLessThan(OutputTailInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ImageTurns);
        return this;
    }
}

/// <summary>What one session's turns run on. The services may be shared between sessions; the slots may not.</summary>
public sealed class TurnServices
{
    public required IEventLog Log { get; init; }

    public required SessionModelSlots Slots { get; init; }

    /// <summary>The shared fabric; with <see cref="Deciders"/> set, only the default (see <see cref="CurrentFabric"/>).</summary>
    public required DecisionFabric Fabric { get; init; }

    /// <summary>
    /// A5: the decider per session. When set, each checkpoint and each approval's <c>risk</c> question runs on the fabric for
    /// the session's current <c>decider</c> slot (its mix's, or the one the person switched to); null keeps <see cref="Fabric"/>.
    /// </summary>
    public SessionDeciders? Deciders { get; init; }

    /// <summary>The fabric the session's decisions run on now (A5): <see cref="Deciders"/>' for <see cref="Slots"/>, else <see cref="Fabric"/>.</summary>
    public DecisionFabric CurrentFabric() => Deciders?.For(Slots) ?? Fabric;

    public required ToolRegistry Tools { get; init; }

    /// <summary>Required for side-effecting tools: without it they are refused, never run unasked.</summary>
    public ApprovalPipeline? Approvals { get; init; }

    /// <summary>
    /// Checkpoints (C-014): a tool call that changes files runs only after a checkpoint, and the approval's
    /// <c>reversible</c> comes from the store. Without it nothing is checkpointed and reversibility is unknown.
    /// </summary>
    public CheckpointService? Checkpoints { get; init; }

    /// <summary>The write path <c>remember</c> starts at egress (§9.2); without it <c>remember</c> is not asked.</summary>
    public MemoryWriter? Memory { get; init; }

    /// <summary>Hybrid retrieval for <c>needs_recall</c> (§9.3); without it <c>needs_recall</c> is not asked.</summary>
    public HybridRetriever? Retriever { get; init; }

    /// <summary>The session's frozen core-block snapshot (§9.1), rendered into the system prompt.</summary>
    public CoreSnapshot? CoreBlocks { get; init; }

    public ToolShortlist Shortlist { get; init; } = new();

    /// <summary>Plan §8 lever 2, asked before compaction: drop the bodies of skills that have completed. Skills are C-017.</summary>
    public ISkillBodyLever? SkillBodies { get; init; }

    /// <summary>Skills to shortlist and load (plan §11); without it <c>skill</c> is not asked.</summary>
    public ISkillSource? Skills { get; init; }

    /// <summary>Where <c>skill_opportunity</c> queues a turn; without it the question is not asked.</summary>
    public ISkillLearning? SkillLearning { get; init; }

    /// <summary>
    /// The project the session works in (C-025): bound with <c>ProjectBound</c> before the turn's message, its approved
    /// lessons join full recall, and <c>lesson_opportunity</c> files lessons under it. Null for none.
    /// </summary>
    public ProjectIdentity? Project { get; init; }

    /// <summary>Where <c>lesson_opportunity</c> queues a turn (C-025); without it, or without a project, the question is not asked.</summary>
    public ILessonLearning? LessonLearning { get; init; }

    /// <summary>The skill shortlist: a few candidates, since each becomes an option of <c>skill</c>.</summary>
    public ToolShortlist SkillShortlist { get; init; } = new(new ShortlistOptions(Size: 3));

    public TurnOptions Options { get; init; } = TurnOptions.Default;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Told what a running turn does that the log and the spans do not carry while it happens: each step as it starts
    /// (turn progress) and the bounded tail of a running call's output (K-2). Null tells nobody, and tools get no output sink.
    /// </summary>
    public ITurnListener? Listener { get; init; }

    /// <summary>
    /// A5: where an <c>ask_user</c> question is asked inside the turn when a surface can answer it now; null (or nobody
    /// attached) ends the turn on the question, as K-6 does.
    /// </summary>
    public IQuestionAsker? Questions { get; init; }

    /// <summary>
    /// K-8 (A6): where a message's images are stored and read back for the generator. Null takes no images: a message that
    /// carries any is refused with why.
    /// </summary>
    public Abstractions.Media.IMediaStore? Media { get; init; }

    /// <summary>
    /// K-8 (A6 review): the egress gate's policy for a model (null for one it does not gate), so the turn sends an image
    /// only to a model whose policy allows images, and refuses a message when no model that could read its images may be
    /// sent them. Null treats every model as ungated, as a host without a gate is.
    /// </summary>
    public Func<ModelProfile, Models.Privacy.EgressPolicy?>? EgressPolicyFor { get; init; }
}

/// <summary>
/// What a surface hears from the turn loop beyond the log and the spans (turn progress, K-2). Called on the turn's threads,
/// a running call's output from the tool's own; an implementation must be quick and must not throw (a throw is swallowed).
/// </summary>
public interface ITurnListener
{
    /// <summary>A generator response with tool calls began step <paramref name="step"/> of at most <paramref name="maxSteps"/>.</summary>
    void StepStarted(string sessionId, int step, int maxSteps) { }

    /// <summary>
    /// A running call's output so far, as its last lines (bounded by <see cref="TurnOptions.OutputTailChars"/> and
    /// <see cref="TurnOptions.OutputTailLines"/>, credentials scrubbed as the log scrubs results), replacing the previous
    /// tail. Untrusted: whatever the command or the page printed.
    /// </summary>
    void ToolOutput(string sessionId, string callId, string tool, string tail) { }

    /// <summary>
    /// A7: an architect/editor turn's plan was logged (<see cref="AssistantMessage"/> with its <see cref="TurnPlan"/>), as
    /// written: a model's text, untrusted. The editor's calls follow.
    /// </summary>
    void Planned(string sessionId, TurnPlan plan, string text) { }
}

/// <summary>
/// The clarifying question a <see cref="TurnStatus.Clarifying"/> turn ended on (K-6), as the log's <c>ClarificationAsked</c>
/// records it. Nothing answers it but the person, with their next message.
/// </summary>
/// <param name="Id">Unique in the session.</param>
/// <param name="Options">Answers offered as choices (the <c>ask_user</c> tool's); empty for a free-text question.</param>
public sealed record ClarifyingQuestion(string Id, string Text, ImmutableArray<string> Options);

public enum TurnStatus
{
    Answered,
    /// <summary><c>clarify</c> (or <c>intent</c>) acted: the answer is one question for the user.</summary>
    Clarifying,
    /// <summary><c>stuck</c> acted and escalation went nowhere; the generator answered with what it had.</summary>
    Stuck,
    /// <summary>The step budget ran out; the generator answered without tools.</summary>
    StepBudgetExhausted,
    /// <summary>A second failed repair or a context that does not fit, with nowhere to escalate.</summary>
    Failed,
    Cancelled,
}

/// <summary>
/// A high-confidence extracted span shown as a hint beside an argument that was missing or failed validation (plan §7 step
/// 4). A call whose arguments validate runs as written, whatever the spans say.
/// </summary>
public sealed record SlotDisagreement(string CallId, string Tool, string Parameter, string? Argument, string Extracted, float Confidence);

/// <param name="Text">The answer delivered, or null when the turn was cancelled.</param>
/// <param name="Steps">Generator responses that carried tool calls.</param>
/// <param name="Deliver">Where the answer goes: the <c>deliver</c> option chosen, or <c>reply</c>.</param>
/// <param name="Escalations">Profiles the generator escalated to during the turn, with the reason.</param>
/// <param name="Compactions">What compaction did during the turn, one line per range tried (C-016).</param>
public sealed record TurnResult(
    TurnStatus Status,
    string? Text,
    int Steps,
    int ToolCalls,
    bool Tainted,
    string Deliver,
    ImmutableArray<string> Escalations,
    ImmutableArray<SlotDisagreement> Disagreements,
    string? MemoryWrite = null,
    ImmutableArray<string> Compactions = default)
{
    public ImmutableArray<string> Compactions { get; init; } = Compactions.IsDefault ? [] : Compactions;

    /// <summary>The skill whose body was in context (L1), if any.</summary>
    public string? Skill { get; init; }

    /// <summary>True when <c>skill_opportunity</c> queued this turn for distillation.</summary>
    public bool SkillQueued { get; init; }

    /// <summary>True when <c>lesson_opportunity</c> queued this turn for a lesson draft (C-025).</summary>
    public bool LessonQueued { get; init; }

    /// <summary>The structured question, when the turn is <see cref="TurnStatus.Clarifying"/> (K-6).</summary>
    public ClarifyingQuestion? Question { get; init; }

    /// <summary>Messages the user steered into the turn (K-3) that it took, in order.</summary>
    public int Steers { get; init; }

    /// <summary>A7: the plans an architect/editor turn made (the plan and at most one re-plan), in order; empty for any other turn.</summary>
    public ImmutableArray<TurnPlan> Plans { get; init; } = [];
}
