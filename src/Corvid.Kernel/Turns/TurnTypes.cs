using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Fabric;
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

    /// <summary>Labels the extractor looks for in the user message, for the TurnState's <c>entities</c>.</summary>
    public ImmutableArray<string> EntityLabels { get; init; } = ["person", "organization", "place", "date", "time", "project", "file", "url"];

    /// <summary>
    /// Slot pre-fill (plan §7 step 4): an extracted span at least this confident that the generator's argument omits or
    /// contradicts is shown to it in the repair message.
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

    /// <summary>Example requests per tool name, for multi-vector shortlisting (§5.4). C-005 has no examples file yet.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>>? ToolExamples { get; init; }

    /// <summary>Hard filters before the shortlist: toolset enabled, platform allowed, availability.</summary>
    public Func<ToolDescriptor, bool>? ToolFilter { get; init; }

    /// <summary>The Vault catalogue's secret names; tools with secret parameters are offered only with one (§10.5).</summary>
    public IReadOnlyCollection<string>? SecretNames { get; init; }

    /// <summary>Recipients the user has already messaged; in a tainted turn others ask (§10.3).</summary>
    public IReadOnlySet<string>? KnownRecipients { get; init; }

    public TurnStateBudget? StateBudget { get; init; }

    /// <summary>Compaction (plan §8 lever 3, C-016): the summarizer's completion, the record's budget and expansion's fill.</summary>
    public CompactionOptions Compaction { get; init; } = CompactionOptions.Default;

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
        return this;
    }
}

/// <summary>What one session's turns run on. The services may be shared between sessions; the slots may not.</summary>
public sealed class TurnServices
{
    public required IEventLog Log { get; init; }

    public required SessionModelSlots Slots { get; init; }

    public required DecisionFabric Fabric { get; init; }

    public required ToolRegistry Tools { get; init; }

    /// <summary>Required for side-effecting tools: without it they are refused, never run unasked.</summary>
    public ApprovalPipeline? Approvals { get; init; }

    /// <summary>The write path <c>remember</c> starts at egress (§9.2); without it <c>remember</c> is not asked.</summary>
    public MemoryWriter? Memory { get; init; }

    /// <summary>Hybrid retrieval for <c>needs_recall</c> (§9.3); without it <c>needs_recall</c> is not asked.</summary>
    public HybridRetriever? Retriever { get; init; }

    /// <summary>The session's frozen core-block snapshot (§9.1), rendered into the system prompt.</summary>
    public CoreSnapshot? CoreBlocks { get; init; }

    public ToolShortlist Shortlist { get; init; } = new();

    /// <summary>Plan §8 lever 2, asked before compaction: drop the bodies of skills that have completed. Skills are C-017.</summary>
    public ISkillBodyLever? SkillBodies { get; init; }

    public TurnOptions Options { get; init; } = TurnOptions.Default;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

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

/// <summary>A high-confidence extracted span the generator's argument omitted or contradicted (plan §7 step 4).</summary>
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
}
