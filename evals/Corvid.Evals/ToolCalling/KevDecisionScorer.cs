using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;

namespace Corvid.Evals.ToolCalling;

/// <summary>
/// One <c>tool</c> or <c>done</c> decision of a turn, labelled against the case (spike S4, C-038).
/// </summary>
/// <param name="Question"><c>tool</c> or <c>done</c>.</param>
/// <param name="Ingress">The turn's first <c>tool</c> decision (asked at ingress); always false for <c>done</c>.</param>
/// <param name="Expected">For <c>tool</c> the expectation at that point (a tool, <c>any_of(…)</c> or <c>none</c>); for <c>done</c>
/// <c>yes</c> or <c>no</c>.</param>
/// <param name="Answer">Kev's answer: the argmax option for <c>tool</c>, <c>yes</c> when P(yes) ≥ 0.5 for <c>done</c>; null
/// without probabilities.</param>
/// <param name="Probability">The pick's probability for <c>tool</c>, P(yes) for <c>done</c>.</param>
/// <param name="Right">The answer is what the case expects; false when there is no answer.</param>
/// <param name="Acted">At or above the floor: the policy's act (<c>offer_tool:…</c>, <c>stop</c>).</param>
/// <param name="Calibrated">Answered by the calibrated decider (Kev); false or null on the log is a fallback.</param>
/// <param name="StateTokens">The state the decider saw, as its JSON's characters / 4; null when not logged.</param>
/// <param name="RecentItems">Items in the state's <c>recent</c>; null when not logged.</param>
/// <param name="Options">The options asked (the <c>tool</c> question's shortlist and none).</param>
public sealed record KevDecision(string DecisionId, string Question, bool Ingress, string Expected, string? Answer, double? Probability,
    bool Right, bool Acted, bool Calibrated, int? StateTokens, int? RecentItems, ImmutableArray<string> Options)
{
    /// <summary>A calibrated answer, so the decision counts toward accuracy.</summary>
    public bool Scored => Calibrated && Answer is not null;
}

/// <summary>A turn's <c>tool</c> and <c>done</c> decisions, labelled, with what S4 reads beside them.</summary>
/// <param name="ShortlistHit">The ingress <c>tool</c> question offered an expected tool of the first step; null on a no-tool
/// case or without an ingress <c>tool</c> question.</param>
/// <param name="DeciderMs">Each decider call in order. When Kev answers every checkpoint the first is ingress and the rest steps.</param>
/// <param name="Compacted">The turn compacted (C-016), so <c>recent</c> held a summary line.</param>
public sealed record KevDecisionScore(ImmutableArray<KevDecision> Decisions, bool? ShortlistHit, ImmutableArray<double> DeciderMs, bool Compacted)
{
    public KevDecision? IngressTool => Decisions.FirstOrDefault(decision => decision.Ingress);

    public int Tool => Decisions.Count(decision => decision.Question == "tool" && decision.Scored);

    public int ToolRight => Decisions.Count(decision => decision.Question == "tool" && decision.Scored && decision.Right);

    public int LaterTool => Decisions.Count(decision => decision.Question == "tool" && !decision.Ingress && decision.Scored);

    public int LaterToolRight => Decisions.Count(decision => decision.Question == "tool" && !decision.Ingress && decision.Scored && decision.Right);

    public int Done => Decisions.Count(decision => decision.Question == "done" && decision.Scored);

    public int DoneRight => Decisions.Count(decision => decision.Question == "done" && decision.Scored && decision.Right);

    public int ToolActed => Decisions.Count(decision => decision.Question == "tool" && decision.Scored && decision.Acted);

    public int ToolActedRight => Decisions.Count(decision => decision.Question == "tool" && decision.Scored && decision.Acted && decision.Right);

    public int DoneActed => Decisions.Count(decision => decision.Question == "done" && decision.Scored && decision.Acted);

    public int DoneActedRight => Decisions.Count(decision => decision.Question == "done" && decision.Scored && decision.Acted && decision.Right);

    /// <summary><c>tool</c> or <c>done</c> decisions not answered by Kev (the fabric's fallback, or no answer).</summary>
    public int Fallbacks => Decisions.Count(decision => !decision.Calibrated);
}

/// <summary>
/// Labels Kev's <c>tool</c> and <c>done</c> decisions in a run (S4's metric, C-038; also the outcomes C-021 fits floors on).
/// Expected at each decision: the first step not yet matched by a call that ran (a call that ran but that the next step does
/// not accept, a wrong tool or a repeat, does not move it), or none once every step has run or on a no-tool case. <c>done</c>
/// expects yes when every step has run, or on a no-tool case, and no otherwise. Only the turn's own events are read
/// (<see cref="CaseRun.Events"/> starts after the seeded history), so a seeded call never counts as a step that ran.
/// </summary>
public static class KevDecisionScorer
{
    public const string ToolQuestion = "tool";

    public const string DoneQuestion = "done";

    public static KevDecisionScore Score(CaseRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var item = run.Case;
        var tools = run.Events.OfType<ToolCallIssued>().GroupBy(call => call.CallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ToolName, StringComparer.Ordinal);
        var done = 0;
        var ingressSeen = false;
        bool? hit = null;
        var decisions = ImmutableArray.CreateBuilder<KevDecision>();
        foreach (var payload in run.Events)
        {
            if (payload is ToolResult { IsError: false } result && !item.ExpectsNoTool && done < item.Steps.Length
                && tools.TryGetValue(result.CallId, out var ranTool) && item.Steps[done].Expect.Accepts(ranTool))
                done++;
            if (payload is not Decision { QuestionId: ToolQuestion or DoneQuestion } decision)
                continue;
            var allRan = item.ExpectsNoTool || done >= item.Steps.Length;
            var calibrated = decision.Calibrated == true;
            var options = decision.Question?.Options.Select(option => option.Name).ToImmutableArray() ?? [];
            var (tokens, recent) = StateSize(decision.State);
            if (decision.QuestionId == ToolQuestion)
            {
                var ingress = !ingressSeen;
                ingressSeen = true;
                var expect = allRan ? Expectation.NoTool : item.Steps[done].Expect;
                if (ingress && !item.ExpectsNoTool)
                    hit = item.Steps[0].Expect.Tools.Any(tool => options.Contains(tool, StringComparer.Ordinal));
                KeyValuePair<string, double>? pick = decision.Probabilities.IsEmpty ? null
                    : decision.Probabilities.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
                var answer = pick?.Key;
                var right = answer is not null && (expect.None ? answer == "none" : expect.Accepts(answer));
                decisions.Add(new KevDecision(decision.DecisionId, ToolQuestion, ingress, expect.ToString(), answer, pick?.Value, right,
                    decision.Action.StartsWith("offer_tool:", StringComparison.Ordinal), calibrated, tokens, recent, options));
            }
            else
            {
                double? yes = decision.Probabilities.TryGetValue("yes", out var p) ? p : null;
                var answer = yes is { } value ? value >= 0.5 ? "yes" : "no" : null;
                var expected = allRan ? "yes" : "no";
                decisions.Add(new KevDecision(decision.DecisionId, DoneQuestion, false, expected, answer, yes, answer == expected,
                    decision.Action == "stop", calibrated, tokens, recent, options));
            }
        }
        return new KevDecisionScore(decisions.ToImmutable(), hit, run.DeciderMs, run.Events.OfType<Compacted>().Any());
    }

    /// <summary>The state's size as characters / 4 (the plan's rough token), and its <c>recent</c> item count.</summary>
    public static (int? Tokens, int? Recent) StateSize(JsonElement? state)
    {
        if (state is not { ValueKind: JsonValueKind.Object } json)
            return (null, null);
        var recent = json.TryGetProperty("recent", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
        return ((json.GetRawText().Length + 3) / 4, recent);
    }
}
