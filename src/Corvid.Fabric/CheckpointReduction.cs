using System.Collections.Immutable;

namespace Corvid.Fabric;

/// <summary>
/// The reduced checkpoint profile (C-030, spike S1 decision 2): with Kev on the CPU each extra request or question costs
/// seconds, so a step checkpoint asks at most <see cref="MaxStepQuestions"/> questions, chosen in <see cref="StepPriority"/>
/// order; ingress, egress, pre_execute and tick are asked whole. A skipped question is not sent to any decider: the policy
/// gives it its catalogue below-floor action, the deterministic conservative default the degraded tier already uses, and
/// it is still logged as a <c>Decision</c>. A gate (<see cref="QuestionRole.Gate"/>) and <c>injection</c> at ingress are
/// never skipped on the fabric's initiative; a caller may skip a question it can prove does not change the outcome.
/// </summary>
/// <param name="Reason">Why the profile is reduced, for spans and surfaces: <c>kev-0.8b on cpu</c> or <c>configured</c>.</param>
public sealed record CheckpointReduction(string Reason)
{
    /// <summary>The questions a reduced step checkpoint asks at most (S1: done and stuck keep loop control).</summary>
    public const int MaxStepQuestions = 2;

    /// <summary>The step questions kept, in order, up to <see cref="MaxStepQuestions"/>; every other step key is skipped.</summary>
    public static ImmutableArray<string> StepPriority { get; } = ["done", "stuck", "tool"];

    /// <summary>The profile's name in spans and <c>/status</c>.</summary>
    public const string ReducedProfile = "reduced";

    public const string FullProfile = "full";

    /// <summary>
    /// Splits a request into the keys to ask and the keys skipped, with why. <paramref name="reduction"/> null is the full
    /// profile: only the caller's skips apply. A caller's skip must name a key of the request.
    /// </summary>
    public static (ImmutableArray<string> Asked, ImmutableSortedDictionary<string, string> Skipped) Partition(QuestionCatalogue catalogue,
        Checkpoint checkpoint, IEnumerable<string> keys, CheckpointReduction? reduction, IReadOnlyDictionary<string, string>? callerSkips)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(keys);
        var all = keys.ToArray();
        var skipped = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (callerSkips is not null)
            foreach (var (key, reason) in callerSkips)
            {
                if (!all.Contains(key, StringComparer.Ordinal))
                    throw new ArgumentException($"'{key}' is skipped but not asked.", nameof(callerSkips));
                ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(callerSkips));
                skipped[key] = reason;
            }

        if (reduction is not null && checkpoint == Checkpoint.Step)
        {
            var kept = all.Where(key => !skipped.ContainsKey(key) && StepPriority.Contains(key, StringComparer.Ordinal))
                .OrderBy(key => StepPriority.IndexOf(key, StringComparer.Ordinal))
                .Take(MaxStepQuestions)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in all.Where(key => !skipped.ContainsKey(key) && !kept.Contains(key)))
                if (Skippable(catalogue.Resolve(key), checkpoint))
                    skipped[key] = $"step capped at {MaxStepQuestions} ({reduction.Reason})";
        }

        return ([.. all.Where(key => !skipped.ContainsKey(key))], skipped.ToImmutable());
    }

    /// <summary>
    /// Whether the reducer may skip a question on its own initiative: never a gate, never <c>injection</c> at ingress (C-033;
    /// since C-037 its verdict over an outside MCP description withholds nothing, but it is logged as the data a dedicated,
    /// fitted description question would be built from, and a hub skill's ingress question still acts, so it is still asked). Only step questions are trimmed.
    /// </summary>
    public static bool Skippable(CatalogueQuestion question, Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(question);
        return checkpoint == Checkpoint.Step && question.Role != QuestionRole.Gate
            && !(question.Id == "injection" && checkpoint == Checkpoint.Ingress);
    }
}
