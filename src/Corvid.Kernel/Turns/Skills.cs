using System.Collections.Immutable;

namespace Corvid.Kernel.Turns;

/// <summary>A skill the turn may shortlist: its L0 entry (plan §11), name and description, with optional example requests.</summary>
/// <param name="Trusted">Written by the user or the agent. A skill from a hub is outside content: loading its body taints the turn (§10.3).</param>
public sealed record SkillOption(string Name, string Description, ImmutableArray<string> Examples, bool Trusted);

/// <summary>A skill's L1 body, as the generator sees it.</summary>
/// <param name="Resources">L2 files the body may refer to, relative to the skill folder; read with the skill tools.</param>
public sealed record SkillBody(string Name, string Text, bool Trusted, ImmutableArray<string> Resources);

/// <summary>
/// The skills the turn loop can offer (plan §11). Corvid.Skills implements it; the kernel only shortlists, asks
/// <c>skill</c> and renders, so it never references the skill store.
/// </summary>
public interface ISkillSource
{
    /// <summary>The L0 index after the hard filters (platform, required and fallback tools): what may be shortlisted.</summary>
    /// <param name="tools">The names of the tools registered for this turn, for <c>requires_*</c> and <c>fallback_for_*</c>.</param>
    IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools);

    /// <summary>The L1 body, or null when the skill no longer exists. Loading counts as a use for the curator.</summary>
    SkillBody? Load(string name);
}

/// <summary>A turn <c>skill_opportunity</c> said was a reusable procedure: the events from its user message to its answer.</summary>
/// <param name="Tainted">The turn held outside content (§10.3). A skill drafted from it would carry that content into a trusted,
/// agent-authored skill, so the learning loop never applies such a draft without review.</param>
/// <param name="SuccessDecisionId">The <c>task_succeeded</c> decision that granted the precondition (C-026), for its outcome.</param>
/// <param name="SuccessCalibrated">A calibrated decider confirmed the task's success. Without it the draft always waits for review.</param>
public sealed record SkillOpportunity(string SessionId, long FromSequence, long ThroughSequence, string DecisionId, string Request,
    bool Tainted = false, string? SuccessDecisionId = null, bool SuccessCalibrated = false);

/// <summary>Where the turn loop queues skill opportunities (plan §11 learning loop, step 1). Distillation runs later, in the background.</summary>
public interface ISkillLearning
{
    void Queue(SkillOpportunity opportunity);
}

/// <summary>Tool names the turn loop knows by convention, to tell the generator how to load a skill it only sees in L0.</summary>
public static class SkillTools
{
    /// <summary>Reads a skill's body or one of its files (L2).</summary>
    public const string Read = "skill_read";
}
