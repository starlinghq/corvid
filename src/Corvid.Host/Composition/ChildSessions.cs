using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Models;

namespace Corvid.Host.Composition;

/// <summary>
/// A5 (plan §14, <c>delegate_task</c>): what a parent asks a child session for. The agent fills one from its tool call, the
/// person from <c>/delegate</c> or the app. Everything here is a request: <see cref="CorvidRuntime.PlanChildAsync"/> clamps
/// it to the parent's ceilings (<c>Corvid:Agents</c>, the parent's toolset and approval mode) and refuses what it cannot
/// grant, so a model never raises its own limits.
/// </summary>
/// <param name="Goal">What the child is to do: its one message. Untrusted when the agent wrote it.</param>
public sealed record ChildRequest(string Goal)
{
    /// <summary>
    /// The child's toolset, by tool name: explicit, never the parent's wholesale. Empty gives it no tools. Names the parent
    /// session does not have, and the ones a child may never use (asking the person, memory writes, messages, schedules,
    /// delegating again), are left out, each with why (<see cref="ChildPlan.Dropped"/>).
    /// </summary>
    public ImmutableArray<string> Tools { get; init; } = [];

    /// <summary>
    /// The tools the calling turn was offered (a guest's scope, a filtered shortlist), when narrower than the parent session's
    /// registry: the child's toolset is cut to them too. Null: the parent session's registry is the ceiling.
    /// </summary>
    public IReadOnlySet<string>? OfferedTools { get; init; }

    /// <summary>A configured mix by name (<c>Corvid:Agents:Mixes</c>), under <see cref="Mix"/>.</summary>
    public string? MixName { get; init; }

    /// <summary>The slots the child runs on (any slot); slots it leaves out run on the parent's session defaults.</summary>
    public ModelMix? Mix { get; init; }

    /// <summary>The most generator steps, at most <c>Corvid:Agents:MaxSteps</c> and the parent's <c>Corvid:Turn:MaxSteps</c>.</summary>
    public int? MaxSteps { get; init; }

    /// <summary>The most tokens the child's generator calls may use (as providers report them), at most <c>Corvid:Agents:MaxTokens</c>.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The longest it may run, in seconds, at most <c>Corvid:Agents:MaxSeconds</c>.</summary>
    public int? MaxSeconds { get; init; }

    /// <summary>A JSON Schema (an object's) the answer must meet; the answer is then read as JSON and checked on return.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>The approval mode for the child's calls, never laxer than the parent's (null: the parent's).</summary>
    public ApprovalMode? ApprovalMode { get; init; }

    /// <summary><see cref="ChildSessions.ByAgent"/> (a <c>delegate_task</c> call) or <see cref="ChildSessions.ByUser"/>.</summary>
    public string By { get; init; } = ChildSessions.ByAgent;

    /// <summary>The parent's <c>delegate_task</c> call, recorded on <c>ChildSpawned</c>.</summary>
    public string? CallId { get; init; }
}

/// <summary>A tool asked for and left out of a child's toolset, and why.</summary>
public sealed record DroppedTool(string Name, string Reason);

/// <summary>
/// One slot of a child's resolved mix, for its approval card and the Board: the model by catalogue name, its privacy class,
/// the ladder and whether it may escalate.
/// </summary>
public sealed record ChildSlot(string Slot, string Model, PrivacyClass Privacy, ImmutableArray<string> Ladder, bool Escalate);

/// <summary>
/// A child as it would start (A5): the clamped budget, the toolset with what was left out, the resolved mix, the approval mode
/// and taint. What a spawn's approval card shows (goal, toolset, budget and model mix), and what
/// <see cref="CorvidRuntime.OpenChildSession"/> starts. Names are catalogue names; the goal is untrusted text.
/// </summary>
public sealed record ChildPlan(string ParentId, string Goal, ImmutableArray<string> Tools, ImmutableArray<DroppedTool> Dropped,
    ChildBudget Budget, ImmutableArray<ChildSlot> Slots, ApprovalMode ApprovalMode, bool Tainted, int Depth)
{
    /// <summary>The mix in its JSON shape with catalogue names (as <c>ChildSpawned.Mix</c> records it), or null when the child runs on the parent's.</summary>
    public ModelMix? Mix { get; init; }

    public JsonElement? OutputSchema { get; init; }

    public string By { get; init; } = ChildSessions.ByAgent;

    public string? CallId { get; init; }

    /// <summary>The child's slots, bound at subagent priority: the mix's, and the parent's choices for the rest.</summary>
    internal IReadOnlyDictionary<string, SlotConfiguration> Configurations { get; init; } = ImmutableDictionary<string, SlotConfiguration>.Empty;

    /// <summary>The tools of <see cref="Tools"/>, as the parent session had them.</summary>
    internal ImmutableArray<Corvid.Tools.ToolFunction> Functions { get; init; } = [];
}

/// <summary>A child's end, as <c>ChildFinished</c> records it in the parent's log.</summary>
/// <param name="Status">A <see cref="ChildStatuses"/> name.</param>
/// <param name="Text">Its answer (model text: untrusted), or null when it gave none.</param>
/// <param name="Output">The answer read as JSON, when an output schema was given and the answer held an object.</param>
public sealed record ChildResult(string ChildId, string Status, string? Text, JsonElement? Output, int Steps, long Tokens, bool Tainted,
    string? Detail)
{
    public bool Answered => Status == ChildStatuses.Answered;
}

/// <summary>A child that cannot be started: too deep, delegation off, a model refused, an output schema not understood.</summary>
public sealed class ChildRefusedException : InvalidOperationException
{
    public ChildRefusedException(string message) : base(message)
    {
    }

    public ChildRefusedException()
    {
    }

    public ChildRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A5's names for child sessions.</summary>
public static class ChildSessions
{
    /// <summary>The tool a builder registers for the agent (plan §14); a child is never given it (depth 1).</summary>
    public const string DelegateToolName = Corvid.Kernel.Turns.DelegateTaskTool.ToolName;

    /// <summary>The surface a child session's id starts with (<c>child-…</c>).</summary>
    public const string Surface = "child";

    public const string ByAgent = "agent";

    public const string ByUser = "user";

    /// <summary>The stricter of two approval modes: <see cref="ApprovalMode.Manual"/> over Smart over Permissive.</summary>
    public static ApprovalMode Stricter(ApprovalMode a, ApprovalMode b) => (ApprovalMode)Math.Min((int)a, (int)b);
}
