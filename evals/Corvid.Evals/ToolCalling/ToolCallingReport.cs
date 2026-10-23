using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;

namespace Corvid.Evals.ToolCalling;

/// <summary>What the report header records about the run, so numbers can be compared across runs and commits.</summary>
public sealed record ToolCallingHeader(
    string SetVersion,
    string CatalogueSha256,
    string CasesSha256,
    int Tools,
    string FloorsVersion,
    string Commit,
    ImmutableSortedDictionary<string, string> Models,
    ImmutableSortedDictionary<string, string> Options,
    ImmutableSortedDictionary<string, string> Host,
    DateTimeOffset Started,
    DateTimeOffset Finished);

/// <summary>Nearest-rank percentiles and the mean of a set of samples (S1's statistics).</summary>
public sealed record Distribution(int N, double P50, double P90, double Mean, double Max)
{
    public static Distribution Of(IEnumerable<double> samples)
    {
        var sorted = samples.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0)
            return new Distribution(0, double.NaN, double.NaN, double.NaN, double.NaN);
        double Rank(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new Distribution(sorted.Length, Rank(0.5), Rank(0.9), sorted.Average(), sorted[^1]);
    }
}

/// <summary>One configuration's totals. <c>ToolCorrect</c> and <c>ArgsCorrect</c> are strict (an over-call is a miss);
/// the <c>OrOverCalled</c> pair is the lenient rate that also counts a case whose steps all ran before an extra call.</summary>
public sealed record ConfigurationSummary(
    string Configuration,
    int Cases,
    ImmutableSortedDictionary<string, int> Outcomes,
    int ToolCorrect,
    int ArgsCorrect,
    int ToolCorrectOrOverCalled,
    int ArgsCorrectOrOverCalled,
    int NonEscalated,
    int ToolCorrectNonEscalated,
    int ArgsCorrectNonEscalated,
    int Escalated,
    ImmutableSortedDictionary<string, int> EscalationReasons,
    int Clarified,
    int ToolCases,
    int NoCall,
    int Nudged,
    int DescriptionsFlagged,
    int FirstResponseCorrect,
    int RepairedToCorrect,
    int ArgsValid,
    int ArgsValidOf,
    Distribution TurnMs,
    Distribution GeneratorMs,
    Distribution GeneratorCalls,
    Distribution DeciderMs,
    Distribution EmbedMs,
    Distribution PromptTokens)
{
    public double Rate(int count) => Cases == 0 ? double.NaN : 100.0 * count / Cases;
}

/// <summary>
/// S2's pass condition as restated by the maintainer (plan §18, C-037): strict scoring is primary, and (c) passes when its
/// strict correct-tool and strict argument-match rates each match or beat (a)'s and its turn p50 is no worse than (a)'s.
/// <paramref name="TurnP50DeltaMs"/> is (c)'s turn p50 minus (a)'s. The non-escalated deltas, the lenient rates and
/// (c)'s escalation rate are reported, not gating. Reports written before C-037 computed <c>pass</c> under the plan's
/// original rule (≥ 25 points on both, escalation under 30%).
/// </summary>
public sealed record Headline(double ToolPoints, double ArgsPoints, double ToolPointsNonEscalated, double ArgsPointsNonEscalated,
    double EscalationRate, double TurnP50DeltaMs, bool Pass)
{
    /// <summary>(c) against (a) under the restated condition.</summary>
    public static Headline For(ConfigurationSummary a, ConfigurationSummary c)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(c);
        var tool = c.Rate(c.ToolCorrect) - a.Rate(a.ToolCorrect);
        var args = c.Rate(c.ArgsCorrect) - a.Rate(a.ArgsCorrect);
        var latency = c.TurnMs.P50 - a.TurnMs.P50;
        // Compared on counts per case so equal rates are equal; a NaN (no cases, no timings) fails.
        var pass = c.Cases > 0 && a.Cases > 0
            && (long)c.ToolCorrect * a.Cases >= (long)a.ToolCorrect * c.Cases
            && (long)c.ArgsCorrect * a.Cases >= (long)a.ArgsCorrect * c.Cases
            && latency <= 0;
        return new Headline(tool, args, Pct(c.ToolCorrectNonEscalated, c.NonEscalated) - Pct(a.ToolCorrectNonEscalated, a.NonEscalated),
            Pct(c.ArgsCorrectNonEscalated, c.NonEscalated) - Pct(a.ArgsCorrectNonEscalated, a.NonEscalated), c.Rate(c.Escalated), latency, pass);
    }

    private static double Pct(int count, int of) => of == 0 ? double.NaN : 100.0 * count / of;
}

/// <summary>Outcomes appended to Kev's <c>tool</c> decisions (each turn's first, at ingress, and the later ones) and, since
/// C-038, its <c>done</c> decisions.</summary>
public sealed record KevOutcomes(int FirstAccepted, int FirstCorrected, int LaterAccepted, int LaterCorrected)
{
    public int DoneAccepted { get; init; }

    public int DoneCorrected { get; init; }

    /// <summary>The <c>tool</c> outcomes.</summary>
    public int Accepted => FirstAccepted + LaterAccepted;

    public int Corrected => FirstCorrected + LaterCorrected;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"tool: {Accepted} accepted, {Corrected} corrected (first tool decision of a turn: {FirstAccepted} accepted, {FirstCorrected} corrected; later: {LaterAccepted} accepted, {LaterCorrected} corrected); done: {DoneAccepted} accepted, {DoneCorrected} corrected");
}

/// <summary>
/// One S4 arm (C-038) over a set of cases: Kev's labelled <c>tool</c> and <c>done</c> decisions (<see cref="KevDecisionScore"/>),
/// counted over calibrated answers only. The ingress <c>tool</c> decision is one per case and is paired across arms; the
/// later <c>tool</c> and the <c>done</c> decisions follow each arm's own trajectory.
/// </summary>
public sealed record S4Arm(string Configuration, int Cases, int IngressTool, int IngressToolRight, int Tool, int ToolRight, int Done, int DoneRight)
{
    public int ToolActed { get; init; }

    public int ToolActedRight { get; init; }

    public int DoneActed { get; init; }

    public int DoneActedRight { get; init; }

    /// <summary><c>tool</c> or <c>done</c> decisions not answered by Kev; S4 needs none.</summary>
    public int Fallbacks { get; init; }

    /// <summary>Tool cases whose ingress shortlist held an expected tool of the first step, of <see cref="ShortlistOf"/>.</summary>
    public int ShortlistHits { get; init; }

    public int ShortlistOf { get; init; }

    /// <summary>Ingress <c>tool</c> accuracy on the cases whose shortlist held the answer (Kev could pick it).</summary>
    public int IngressToolOnHits { get; init; }

    public int IngressToolRightOnHits { get; init; }

    /// <summary>Turns that compacted (C-016); S4 needs none, or <c>recent</c> held summary lines.</summary>
    public int Compacted { get; init; }

    public Distribution IngressMs { get; init; } = Distribution.Of([]);

    public Distribution StepMs { get; init; } = Distribution.Of([]);

    /// <summary>The <c>tool</c> and <c>done</c> decisions' state size, characters / 4.</summary>
    public Distribution StateTokens { get; init; } = Distribution.Of([]);

    /// <summary>Items in those states' <c>recent</c>.</summary>
    public Distribution RecentItems { get; init; } = Distribution.Of([]);

    public double IngressToolRate => Pct(IngressToolRight, IngressTool);

    public double ToolRate => Pct(ToolRight, Tool);

    public double DoneRate => Pct(DoneRight, Done);

    private static double Pct(int count, int of) => of == 0 ? double.NaN : 100.0 * count / of;

    public static S4Arm Of(string configuration, IReadOnlyCollection<CaseScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var kev = scores.Select(score => score.Kev).OfType<KevDecisionScore>().ToList();
        var ingress = kev.Select(score => score.IngressTool).OfType<KevDecision>().Where(decision => decision.Scored).ToList();
        var hits = kev.Where(score => score.ShortlistHit == true).Select(score => score.IngressTool).OfType<KevDecision>()
            .Where(decision => decision.Scored).ToList();
        var decisions = kev.SelectMany(score => score.Decisions).ToList();
        return new S4Arm(configuration, scores.Count, ingress.Count, ingress.Count(decision => decision.Right),
            kev.Sum(score => score.Tool), kev.Sum(score => score.ToolRight), kev.Sum(score => score.Done), kev.Sum(score => score.DoneRight))
        {
            ToolActed = kev.Sum(score => score.ToolActed),
            ToolActedRight = kev.Sum(score => score.ToolActedRight),
            DoneActed = kev.Sum(score => score.DoneActed),
            DoneActedRight = kev.Sum(score => score.DoneActedRight),
            Fallbacks = kev.Sum(score => score.Fallbacks),
            ShortlistHits = kev.Count(score => score.ShortlistHit == true),
            ShortlistOf = kev.Count(score => score.ShortlistHit is not null),
            IngressToolOnHits = hits.Count,
            IngressToolRightOnHits = hits.Count(decision => decision.Right),
            Compacted = kev.Count(score => score.Compacted),
            IngressMs = Distribution.Of(kev.Where(score => !score.DeciderMs.IsEmpty).Select(score => score.DeciderMs[0])),
            StepMs = Distribution.Of(kev.SelectMany(score => score.DeciderMs.Skip(1))),
            StateTokens = Distribution.Of(decisions.Select(decision => decision.StateTokens).OfType<int>().Select(tokens => (double)tokens)),
            RecentItems = Distribution.Of(decisions.Select(decision => decision.RecentItems).OfType<int>().Select(items => (double)items)),
        };
    }
}

/// <summary>One slice of the S4 comparison: the same cases in both arms.</summary>
public sealed record S4Slice(string Name, S4Arm Short, S4Arm Wide);

/// <summary>
/// Spike S4's headline (plan §18, C-038): Kev's <c>tool</c> and <c>done</c> accuracy with the 600-token TurnState
/// (<c>shortlist-kev</c>) against a transcript-sized one (<c>shortlist-kev-transcript</c>), on the <c>history</c> cases. The
/// plan's "within 3 points" is read one-sided: <see cref="Pass"/> when the 600 arm is no more than 3 points below the wide
/// arm on all calibrated <c>tool</c> decisions and on <c>done</c>. The deltas are 600 − transcript. The paired ingress
/// <c>tool</c> figures (discordant cases, Wilson 95% intervals) say how much one case moves a rate; they do not gate.
/// </summary>
public sealed record S4Comparison(S4Arm Short, S4Arm Wide, double ToolPoints, double DonePoints, double IngressToolPoints,
    int PairedCases, int BothRight, int ShortOnlyRight, int WideOnlyRight, int BothWrong,
    (double Low, double High) ShortIngressInterval, (double Low, double High) WideIngressInterval, ImmutableArray<S4Slice> Slices, bool Pass)
{
    public const string ShortConfiguration = "shortlist-kev";

    public const string WideConfiguration = "shortlist-kev-transcript";

    public const string GatingTag = "history";

    /// <summary>The plan's tolerance, in points.</summary>
    public const double Tolerance = 3.0;

    /// <summary>The 600 arm is at most <see cref="Tolerance"/> points below the wide arm on <c>tool</c> and on <c>done</c>; no
    /// decisions (a NaN rate) fails.</summary>
    public static bool Passes(S4Arm shortArm, S4Arm wideArm)
    {
        ArgumentNullException.ThrowIfNull(shortArm);
        ArgumentNullException.ThrowIfNull(wideArm);
        // A small epsilon so a delta of exactly −3 points passes despite rounding.
        return shortArm.ToolRate >= wideArm.ToolRate - Tolerance - 1e-9 && shortArm.DoneRate >= wideArm.DoneRate - Tolerance - 1e-9;
    }

    /// <summary>Null unless both arms ran on some <c>history</c> case.</summary>
    public static S4Comparison? For(IReadOnlyList<CaseScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        List<CaseScore> Of(string configuration, Func<CaseScore, bool> filter) =>
            [.. scores.Where(score => score.Configuration == configuration && filter(score))];
        static bool History(CaseScore score) => score.Tags.Contains(GatingTag);
        static bool Far(CaseScore score) => score.Tags.Contains("history_far");
        var shortHistory = Of(ShortConfiguration, History);
        var wideHistory = Of(WideConfiguration, History);
        if (shortHistory.Count == 0 || wideHistory.Count == 0)
            return null;

        var slices = new (string Name, Func<CaseScore, bool> Filter)[]
        {
            ("history", History),
            ("history_far", Far),
            ("history, cue near", score => History(score) && !Far(score) && score.Cue is not null),
            ("history, no cue (controls)", score => History(score) && score.Cue is null),
            ("history, multi_step", score => History(score) && score.Tags.Contains("multi_step")),
            ("history, no_tool", score => History(score) && score.Tags.Contains("no_tool")),
            ("all cases", _ => true),
        }.Select(slice => new S4Slice(slice.Name, S4Arm.Of(ShortConfiguration, Of(ShortConfiguration, slice.Filter)),
            S4Arm.Of(WideConfiguration, Of(WideConfiguration, slice.Filter)))).ToImmutableArray();

        var shortArm = slices[0].Short;
        var wideArm = slices[0].Wide;
        var wideById = wideHistory.ToDictionary(score => score.Id, StringComparer.Ordinal);
        int both = 0, shortOnly = 0, wideOnly = 0, neither = 0;
        foreach (var score in shortHistory)
        {
            if (score.Kev?.IngressTool is not { Scored: true } left || !wideById.TryGetValue(score.Id, out var other)
                || other.Kev?.IngressTool is not { Scored: true } right)
                continue;
            if (left.Right && right.Right)
                both++;
            else if (left.Right)
                shortOnly++;
            else if (right.Right)
                wideOnly++;
            else
                neither++;
        }
        return new S4Comparison(shortArm, wideArm, shortArm.ToolRate - wideArm.ToolRate, shortArm.DoneRate - wideArm.DoneRate,
            shortArm.IngressToolRate - wideArm.IngressToolRate, both + shortOnly + wideOnly + neither, both, shortOnly, wideOnly, neither,
            Wilson(shortArm.IngressToolRight, shortArm.IngressTool), Wilson(wideArm.IngressToolRight, wideArm.IngressTool), slices,
            Passes(shortArm, wideArm));
    }

    /// <summary>The Wilson score interval at 95%, in percent; NaN without trials.</summary>
    public static (double Low, double High) Wilson(int successes, int trials)
    {
        if (trials == 0)
            return (double.NaN, double.NaN);
        const double z = 1.96;
        var p = (double)successes / trials;
        var denominator = 1 + z * z / trials;
        var centre = (p + z * z / (2 * trials)) / denominator;
        var half = z * Math.Sqrt(p * (1 - p) / trials + z * z / (4.0 * trials * trials)) / denominator;
        return (100 * Math.Max(0, centre - half), 100 * Math.Min(1, centre + half));
    }
}

/// <summary>The scored run: header, per-case records and per-configuration summaries; JSON and markdown (S5's tables).</summary>
public sealed class ToolCallingReport
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ToolCallingReport(ToolCallingHeader header, ImmutableArray<CaseScore> scores, ImmutableArray<ConfigurationSummary> summaries,
        Headline? headline, S4Comparison? s4)
    {
        Header = header;
        Scores = scores;
        Summaries = summaries;
        Headline = headline;
        S4 = s4;
    }

    /// <summary>Spike S4 (C-038); null unless both <c>shortlist-kev</c> and <c>shortlist-kev-transcript</c> ran on <c>history</c> cases.</summary>
    public S4Comparison? S4 { get; }

    public ToolCallingHeader Header { get; }

    public ImmutableArray<CaseScore> Scores { get; }

    public ImmutableArray<ConfigurationSummary> Summaries { get; }

    /// <summary>Null unless both (a) and (c) ran.</summary>
    public Headline? Headline { get; }

    public static ToolCallingReport Build(ToolCallingHeader header, IEnumerable<CaseScore> scores)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(scores);
        var all = scores.ToImmutableArray();
        var order = all.Select(score => score.Configuration).Distinct(StringComparer.Ordinal).ToList();
        var summaries = order.Select(name => Summarize(name, all.Where(score => score.Configuration == name).ToList())).ToImmutableArray();
        var a = summaries.FirstOrDefault(summary => summary.Configuration == "qwen-all");
        var c = summaries.FirstOrDefault(summary => summary.Configuration == "shortlist-kev");
        var headline = a is not null && c is not null ? Headline.For(a, c) : null;
        return new ToolCallingReport(header, all, summaries, headline, S4Comparison.For(all));
    }

    private static ConfigurationSummary Summarize(string name, List<CaseScore> scores)
    {
        var outcomes = Enum.GetValues<CaseOutcome>().ToImmutableSortedDictionary(ToolCallingScorer.Name,
            outcome => scores.Count(score => score.Outcome == outcome), StringComparer.Ordinal);
        var kept = scores.Where(score => score.Outcome != CaseOutcome.Escalated).ToList();
        var toolCases = scores.Where(score => !score.Tags.Contains("no_tool")).ToList();
        var offeredCalls = scores.Where(score => score.ArgsValid is not null).ToList();
        return new ConfigurationSummary(name, scores.Count, outcomes,
            scores.Count(score => score.ToolCorrect), scores.Count(score => score.ArgumentsCorrect),
            scores.Count(score => score.ToolCorrectOrOverCalled), scores.Count(score => score.ArgumentsCorrectOrOverCalled),
            kept.Count, kept.Count(score => score.ToolCorrect), kept.Count(score => score.ArgumentsCorrect),
            scores.Count(score => score.Outcome == CaseOutcome.Escalated),
            scores.Where(score => score.EscalationReason is not null).GroupBy(score => score.EscalationReason!, StringComparer.Ordinal)
                .ToImmutableSortedDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            scores.Count(score => score.Outcome == CaseOutcome.Clarified),
            toolCases.Count, toolCases.Count(score => score.Outcome == CaseOutcome.NoCall),
            scores.Count(score => score.Nudged), scores.Count(score => score.DescriptionFlagged),
            scores.Count(score => score.FirstResponseCorrect), scores.Count(score => score.RepairedToCorrect),
            offeredCalls.Count(score => score.ArgsValid == true), offeredCalls.Count,
            Distribution.Of(scores.Select(score => score.TurnMs)), Distribution.Of(scores.Select(score => score.GeneratorMs)),
            Distribution.Of(scores.Select(score => (double)score.GeneratorCalls)),
            Distribution.Of(scores.Where(score => score.DeciderCalls > 0).Select(score => score.DeciderMs)),
            Distribution.Of(scores.Where(score => score.EmbedMs > 0).Select(score => score.EmbedMs)),
            Distribution.Of(scores.Where(score => score.PromptTokens is not null).Select(score => (double)score.PromptTokens!)));
    }

    // ---- floor fitting ----

    /// <summary>
    /// For floor fitting (C-021): one <c>DecisionOutcomeRecorded</c> per calibrated <c>tool</c> and <c>done</c> decision in a
    /// Kev configuration's runs, <c>accepted</c> when Kev's answer is what the case expects at that point
    /// (<see cref="KevDecisionScorer"/>: the first step not yet matched by a call that ran, or none once every step has or on a
    /// no-tool case; <c>done</c> yes exactly then), <c>corrected</c> otherwise. A call that ran but that the next step does not
    /// accept (a wrong tool, a repeat) does not move the expectation, and a seeded history call (S4) is never counted. Appended
    /// after the loop's own <c>tool_succeeded</c>, so it is the decision's last outcome and labels it. The log is then valid
    /// input to <c>corvid-evals calibrate --log</c>.
    /// </summary>
    /// <returns>The outcomes appended: <c>tool</c> split into each turn's first decision (ingress) and later ones (the no-op
    /// check and step checkpoints, where a case whose calls have all run expects none), and <c>done</c>.</returns>
    public static KevOutcomes AppendKevOutcomes(IEventLog log, IEnumerable<CaseRun> runs)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runs);
        int firstAccepted = 0, firstCorrected = 0, laterAccepted = 0, laterCorrected = 0, doneAccepted = 0, doneCorrected = 0;
        foreach (var run in runs)
        {
            var events = new List<EventData>();
            foreach (var decision in KevDecisionScorer.Score(run).Decisions.Where(decision => decision.Scored))
            {
                events.Add(EventData.Create(new DecisionOutcomeRecorded(decision.DecisionId, decision.Right ? "accepted" : "corrected")));
                if (decision.Question == KevDecisionScorer.DoneQuestion)
                {
                    if (decision.Right)
                        doneAccepted++;
                    else
                        doneCorrected++;
                }
                else if (decision.Ingress)
                {
                    if (decision.Right)
                        firstAccepted++;
                    else
                        firstCorrected++;
                }
                else if (decision.Right)
                {
                    laterAccepted++;
                }
                else
                {
                    laterCorrected++;
                }
            }
            if (events.Count > 0)
                log.Append(run.SessionId, log.GetHead(run.SessionId), events);
        }
        return new KevOutcomes(firstAccepted, firstCorrected, laterAccepted, laterCorrected)
        {
            DoneAccepted = doneAccepted,
            DoneCorrected = doneCorrected,
        };
    }

    // ---- JSON ----

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Indented))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "corvid-tool-calling-report/4");
            WriteHeader(writer);
            writer.WritePropertyName("headline");
            if (Headline is { } headline)
            {
                writer.WriteStartObject();
                Number(writer, "tool_points", headline.ToolPoints);
                Number(writer, "args_points", headline.ArgsPoints);
                Number(writer, "tool_points_non_escalated", headline.ToolPointsNonEscalated);
                Number(writer, "args_points_non_escalated", headline.ArgsPointsNonEscalated);
                Number(writer, "escalation_rate", headline.EscalationRate);
                Number(writer, "turn_p50_delta_ms", headline.TurnP50DeltaMs);
                writer.WriteString("pass_condition", "c-037");
                writer.WriteBoolean("pass", headline.Pass);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }
            writer.WritePropertyName("s4");
            if (S4 is { } s4)
                WriteS4(writer, s4);
            else
                writer.WriteNullValue();
            writer.WriteStartArray("summaries");
            foreach (var summary in Summaries)
                WriteSummary(writer, summary);
            writer.WriteEndArray();
            writer.WriteStartArray("cases");
            foreach (var score in Scores)
                WriteScore(writer, score);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteS4(Utf8JsonWriter writer, S4Comparison s4)
    {
        writer.WriteStartObject();
        writer.WriteString("short", S4Comparison.ShortConfiguration);
        writer.WriteString("wide", S4Comparison.WideConfiguration);
        writer.WriteString("gating_tag", S4Comparison.GatingTag);
        Number(writer, "tool_points", s4.ToolPoints);
        Number(writer, "done_points", s4.DonePoints);
        Number(writer, "ingress_tool_points", s4.IngressToolPoints);
        writer.WriteStartObject("ingress_tool_paired");
        writer.WriteNumber("cases", s4.PairedCases);
        writer.WriteNumber("both_right", s4.BothRight);
        writer.WriteNumber("short_only_right", s4.ShortOnlyRight);
        writer.WriteNumber("wide_only_right", s4.WideOnlyRight);
        writer.WriteNumber("both_wrong", s4.BothWrong);
        Interval(writer, "short_wilson95", s4.ShortIngressInterval);
        Interval(writer, "wide_wilson95", s4.WideIngressInterval);
        writer.WriteEndObject();
        writer.WriteString("pass_condition", "c-038: 600 tool >= transcript tool - 3 AND 600 done >= transcript done - 3, history cases");
        writer.WriteBoolean("pass", s4.Pass);
        writer.WriteStartArray("slices");
        foreach (var slice in s4.Slices)
        {
            writer.WriteStartObject();
            writer.WriteString("slice", slice.Name);
            WriteArm(writer, "short", slice.Short);
            WriteArm(writer, "wide", slice.Wide);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteArm(Utf8JsonWriter writer, string name, S4Arm arm)
    {
        writer.WriteStartObject(name);
        writer.WriteString("configuration", arm.Configuration);
        writer.WriteNumber("cases", arm.Cases);
        writer.WriteNumber("ingress_tool", arm.IngressTool);
        writer.WriteNumber("ingress_tool_right", arm.IngressToolRight);
        writer.WriteNumber("tool", arm.Tool);
        writer.WriteNumber("tool_right", arm.ToolRight);
        writer.WriteNumber("done", arm.Done);
        writer.WriteNumber("done_right", arm.DoneRight);
        Number(writer, "ingress_tool_rate", arm.IngressToolRate);
        Number(writer, "tool_rate", arm.ToolRate);
        Number(writer, "done_rate", arm.DoneRate);
        writer.WriteNumber("tool_acted", arm.ToolActed);
        writer.WriteNumber("tool_acted_right", arm.ToolActedRight);
        writer.WriteNumber("done_acted", arm.DoneActed);
        writer.WriteNumber("done_acted_right", arm.DoneActedRight);
        writer.WriteNumber("fallbacks", arm.Fallbacks);
        writer.WriteNumber("shortlist_hits", arm.ShortlistHits);
        writer.WriteNumber("shortlist_of", arm.ShortlistOf);
        writer.WriteNumber("ingress_tool_on_hits", arm.IngressToolOnHits);
        writer.WriteNumber("ingress_tool_right_on_hits", arm.IngressToolRightOnHits);
        writer.WriteNumber("compacted", arm.Compacted);
        Dist(writer, "decider_ingress_ms", arm.IngressMs);
        Dist(writer, "decider_step_ms", arm.StepMs);
        Dist(writer, "state_tokens_chars_over_4", arm.StateTokens);
        Dist(writer, "recent_items", arm.RecentItems);
        writer.WriteEndObject();
    }

    private static void Interval(Utf8JsonWriter writer, string name, (double Low, double High) interval)
    {
        writer.WriteStartArray(name);
        foreach (var bound in new[] { interval.Low, interval.High })
            if (double.IsFinite(bound))
                writer.WriteNumberValue(Math.Round(bound, 1));
            else
                writer.WriteNullValue();
        writer.WriteEndArray();
    }

    private static void WriteKev(Utf8JsonWriter writer, KevDecisionScore kev)
    {
        writer.WriteStartObject("kev");
        if (kev.ShortlistHit is { } hit)
            writer.WriteBoolean("shortlist_hit", hit);
        writer.WriteNumber("fallbacks", kev.Fallbacks);
        writer.WriteBoolean("compacted", kev.Compacted);
        writer.WriteStartArray("decisions");
        foreach (var decision in kev.Decisions)
        {
            writer.WriteStartObject();
            writer.WriteString("question", decision.Question);
            if (decision.Ingress)
                writer.WriteBoolean("ingress", true);
            writer.WriteString("expected", decision.Expected);
            writer.WriteString("answer", decision.Answer);
            if (decision.Probability is { } probability)
                writer.WriteNumber("p", Math.Round(probability, 4));
            writer.WriteBoolean("right", decision.Right);
            writer.WriteBoolean("acted", decision.Acted);
            writer.WriteBoolean("calibrated", decision.Calibrated);
            if (decision.StateTokens is { } tokens)
                writer.WriteNumber("state_tokens", tokens);
            if (decision.RecentItems is { } recent)
                writer.WriteNumber("recent_items", recent);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void WriteHeader(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("header");
        writer.WriteString("set_version", Header.SetVersion);
        writer.WriteString("catalogue_sha256", Header.CatalogueSha256);
        writer.WriteString("cases_sha256", Header.CasesSha256);
        writer.WriteNumber("tools", Header.Tools);
        writer.WriteString("floors_version", Header.FloorsVersion);
        writer.WriteString("corvid_commit", Header.Commit);
        writer.WriteString("started", Header.Started.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("finished", Header.Finished.ToString("O", CultureInfo.InvariantCulture));
        Map(writer, "models", Header.Models);
        Map(writer, "options", Header.Options);
        Map(writer, "host", Header.Host);
        writer.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter writer, ConfigurationSummary summary)
    {
        writer.WriteStartObject();
        writer.WriteString("configuration", summary.Configuration);
        writer.WriteNumber("cases", summary.Cases);
        writer.WriteStartObject("outcomes");
        foreach (var (name, count) in summary.Outcomes)
            writer.WriteNumber(name, count);
        writer.WriteEndObject();
        writer.WriteNumber("tool_correct", summary.ToolCorrect);
        writer.WriteNumber("args_correct", summary.ArgsCorrect);
        writer.WriteNumber("tool_correct_or_over_called", summary.ToolCorrectOrOverCalled);
        writer.WriteNumber("args_correct_or_over_called", summary.ArgsCorrectOrOverCalled);
        writer.WriteNumber("non_escalated", summary.NonEscalated);
        writer.WriteNumber("tool_correct_non_escalated", summary.ToolCorrectNonEscalated);
        writer.WriteNumber("args_correct_non_escalated", summary.ArgsCorrectNonEscalated);
        writer.WriteNumber("escalated", summary.Escalated);
        writer.WriteStartObject("escalation_reasons");
        foreach (var (reason, count) in summary.EscalationReasons)
            writer.WriteNumber(reason, count);
        writer.WriteEndObject();
        writer.WriteNumber("clarified", summary.Clarified);
        writer.WriteNumber("tool_cases", summary.ToolCases);
        writer.WriteNumber("no_call", summary.NoCall);
        writer.WriteNumber("nudged", summary.Nudged);
        writer.WriteNumber("descriptions_flagged", summary.DescriptionsFlagged);
        writer.WriteNumber("first_response_correct", summary.FirstResponseCorrect);
        writer.WriteNumber("repaired_to_correct", summary.RepairedToCorrect);
        writer.WriteNumber("args_valid", summary.ArgsValid);
        writer.WriteNumber("args_valid_of", summary.ArgsValidOf);
        Dist(writer, "turn_ms", summary.TurnMs);
        Dist(writer, "generator_ms", summary.GeneratorMs);
        Dist(writer, "generator_calls", summary.GeneratorCalls);
        Dist(writer, "decider_ms", summary.DeciderMs);
        Dist(writer, "embed_ms", summary.EmbedMs);
        Dist(writer, "prompt_tokens", summary.PromptTokens);
        writer.WriteEndObject();
    }

    private static void WriteScore(Utf8JsonWriter writer, CaseScore score)
    {
        writer.WriteStartObject();
        writer.WriteString("id", score.Id);
        writer.WriteString("configuration", score.Configuration);
        writer.WriteStartArray("tags");
        foreach (var tag in score.Tags)
            writer.WriteStringValue(tag);
        writer.WriteEndArray();
        writer.WriteString("expected", score.Expected);
        writer.WriteString("outcome", ToolCallingScorer.Name(score.Outcome));
        writer.WriteString("local_outcome", ToolCallingScorer.Name(score.LocalOutcome));
        writer.WriteString("escalation", score.EscalationReason);
        writer.WriteString("status", score.Status);
        writer.WriteStartArray("calls");
        foreach (var call in score.Calls)
        {
            writer.WriteStartObject();
            writer.WriteString("tool", call.Tool);
            writer.WritePropertyName("arguments");
            call.Arguments.WriteTo(writer);
            if (call.Round is { } round)
                writer.WriteNumber("round", round);
            writer.WriteBoolean("rejected", call.Rejected);
            writer.WriteBoolean("invalid_name", call.InvalidName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Bool(writer, "args_valid", score.ArgsValid);
        Bool(writer, "args_match", score.ArgsMatch);
        writer.WriteStartArray("argument_misses");
        foreach (var miss in score.ArgumentMisses)
            writer.WriteStringValue(miss);
        writer.WriteEndArray();
        writer.WriteBoolean("first_response_correct", score.FirstResponseCorrect);
        writer.WriteBoolean("repaired_to_correct", score.RepairedToCorrect);
        writer.WriteBoolean("nudged", score.Nudged);
        writer.WriteNumber("steps", score.Steps);
        writer.WriteNumber("steps_matched", score.StepsMatched);
        writer.WriteNumber("steps_expected", score.StepsExpected);
        writer.WriteString("kev_tool_pick", score.KevToolPick);
        if (score.KevToolConfidence is { } confidence)
            writer.WriteNumber("kev_tool_confidence", Math.Round(confidence, 4));
        writer.WriteBoolean("kev_tool_acted", score.KevToolActed);
        writer.WriteStartArray("acted");
        foreach (var acted in score.Acted)
            writer.WriteStringValue(acted);
        writer.WriteEndArray();
        Number(writer, "turn_ms", score.TurnMs);
        Number(writer, "generator_ms", score.GeneratorMs);
        writer.WriteNumber("generator_calls", score.GeneratorCalls);
        Number(writer, "decider_ms", score.DeciderMs);
        writer.WriteNumber("decider_calls", score.DeciderCalls);
        Number(writer, "embed_ms", score.EmbedMs);
        if (score.PromptTokens is { } tokens)
            writer.WriteNumber("prompt_tokens", tokens);
        writer.WriteStartArray("offered");
        foreach (var tool in score.Offered)
            writer.WriteStringValue(tool);
        writer.WriteEndArray();
        writer.WriteString("text", score.Text);
        writer.WriteString("error", score.Error);
        if (score.Cue is { } cue)
            writer.WriteNumber("cue", cue);
        if (score.Kev is { } kev)
            WriteKev(writer, kev);
        writer.WriteEndObject();
    }

    private static void Map(Utf8JsonWriter writer, string name, ImmutableSortedDictionary<string, string> map)
    {
        writer.WriteStartObject(name);
        foreach (var (key, value) in map)
            writer.WriteString(key, value);
        writer.WriteEndObject();
    }

    private static void Dist(Utf8JsonWriter writer, string name, Distribution distribution)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("n", distribution.N);
        Number(writer, "p50", distribution.P50);
        Number(writer, "p90", distribution.P90);
        Number(writer, "mean", distribution.Mean);
        Number(writer, "max", distribution.Max);
        writer.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter writer, string name, double value)
    {
        if (double.IsFinite(value))
            writer.WriteNumber(name, Math.Round(value, 1));
        else
            writer.WriteNull(name);
    }

    private static void Bool(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag)
            writer.WriteBoolean(name, flag);
        else
            writer.WriteNull(name);
    }

    // ---- markdown ----

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# S2 tool-calling report");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"Set `{Header.SetVersion}` ({Header.Tools} tools, catalogue `{Header.CatalogueSha256[..12]}`, cases `{Header.CasesSha256[..12]}`), floors `{Header.FloorsVersion}`, Corvid `{Header.Commit}`, {Header.Started:yyyy-MM-dd HH:mm}–{Header.Finished:HH:mm} UTC.");
        md.AppendLine();
        foreach (var (key, value) in Header.Models)
            md.AppendLine(CultureInfo.InvariantCulture, $"- {key}: {value}");
        foreach (var (key, value) in Header.Options)
            md.AppendLine(CultureInfo.InvariantCulture, $"- {key}: {value}");
        foreach (var (key, value) in Header.Host)
            md.AppendLine(CultureInfo.InvariantCulture, $"- {key}: {value}");
        md.AppendLine();

        if (Headline is { } headline)
        {
            md.AppendLine("## Headline");
            md.AppendLine();
            md.AppendLine("Plan §18, restated by the maintainer (C-037): strict scoring is primary; (c) passes if it matches or beats (a) on strict tool AND strict argument accuracy and its turn p50 is no worse than (a)'s. Lenient rates and escalation are reported, not gating. An escalation counts as a miss.");
            md.AppendLine();
            md.AppendLine("| Measure | (c) − (a), all cases | (c) − (a), non-escalated cases |");
            md.AppendLine("| --- | --- | --- |");
            md.AppendLine(CultureInfo.InvariantCulture, $"| Tool choice | {Points(headline.ToolPoints)} | {Points(headline.ToolPointsNonEscalated)} |");
            md.AppendLine(CultureInfo.InvariantCulture, $"| Argument match | {Points(headline.ArgsPoints)} | {Points(headline.ArgsPointsNonEscalated)} |");
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"Turn p50, (c) − (a): {headline.TurnP50DeltaMs:+0;-0;0} ms. Escalation rate of (c): {headline.EscalationRate:0.0}% (not gating). **{(headline.Pass ? "Pass" : "Fail")}.**");
            md.AppendLine();
        }

        if (S4 is { } s4)
            AppendS4(md, s4);

        md.AppendLine("## Outcomes");
        md.AppendLine();
        md.AppendLine("Correct tool and args match are strict: a case whose expected steps all ran and then another tool ran is over-called, a miss. The two lenient columns count it as correct.");
        md.AppendLine();
        md.AppendLine("| Configuration | n | Correct tool | Args match | Over-called | Correct tool, lenient | Args match, lenient | Wrong tool | Invalid name | Malformed | No call | Called on no-tool | Clarified | Escalated | Failed | Correct, non-escalated | Args valid (first offered call) | First response correct | Repaired | Nudged | MCP descriptions flagged |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var s in Summaries)
        {
            int O(CaseOutcome outcome) => s.Outcomes[ToolCallingScorer.Name(outcome)];
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Configuration} | {s.Cases} | {Share(s.ToolCorrect, s.Cases)} | {Share(s.ArgsCorrect, s.Cases)} | {O(CaseOutcome.OverCalled)} | {Share(s.ToolCorrectOrOverCalled, s.Cases)} | {Share(s.ArgsCorrectOrOverCalled, s.Cases)} | {O(CaseOutcome.WrongTool)} | {O(CaseOutcome.InvalidName)} | {O(CaseOutcome.Malformed)} | {O(CaseOutcome.NoCall)} of {s.ToolCases} | {O(CaseOutcome.CalledOnNoTool)} | {s.Clarified} | {s.Escalated}{Reasons(s)} | {O(CaseOutcome.Failed)} | {Share(s.ToolCorrectNonEscalated, s.NonEscalated)} | {s.ArgsValid}/{s.ArgsValidOf} | {s.FirstResponseCorrect} | {s.RepairedToCorrect} | {s.Nudged} | {s.DescriptionsFlagged} |");
        }
        md.AppendLine();

        md.AppendLine("## By tag");
        md.AppendLine();
        md.AppendLine("Correct tool / argument match, per tag (a case has several tags).");
        md.AppendLine();
        md.AppendLine("| Tag | n | " + string.Join(" | ", Summaries.Select(s => s.Configuration)) + " |");
        md.AppendLine("| --- | --- | " + string.Join(" | ", Summaries.Select(_ => "---")) + " |");
        foreach (var tag in ToolCallingSet.KnownTags)
        {
            var cells = Summaries.Select(s =>
            {
                var tagged = Scores.Where(score => score.Configuration == s.Configuration && score.Tags.Contains(tag)).ToList();
                return tagged.Count == 0 ? "–" : $"{tagged.Count(score => score.ToolCorrect)} / {tagged.Count(score => score.ArgumentsCorrect)}";
            }).ToList();
            var n = Scores.Where(score => score.Configuration == Summaries[0].Configuration && score.Tags.Contains(tag)).Count();
            if (n > 0)
                md.AppendLine(CultureInfo.InvariantCulture, $"| {tag} | {n} | {string.Join(" | ", cells)} |");
        }
        md.AppendLine();

        md.AppendLine("## Latency");
        md.AppendLine();
        md.AppendLine("Milliseconds per case, nearest-rank p50 (p90). Generator and decider are the turn's totals; prompt tokens are the first generator call's input.");
        md.AppendLine();
        md.AppendLine("| Configuration | Turn | Generator | Generator calls (mean) | Decider | Embed | Prompt tokens |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var s in Summaries)
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Configuration} | {D(s.TurnMs)} | {D(s.GeneratorMs)} | {s.GeneratorCalls.Mean:0.00} | {D(s.DeciderMs)} | {D(s.EmbedMs)} | {D(s.PromptTokens)} |");
        md.AppendLine();

        md.AppendLine("## Disagreements");
        md.AppendLine();
        md.AppendLine("Every case some configuration did not get right, with what each called (`!` rejected, `?` invalid name) and the argument misses.");
        md.AppendLine();
        md.AppendLine("| Case | Expected | " + string.Join(" | ", Summaries.Select(s => s.Configuration)) + " |");
        md.AppendLine("| --- | --- | " + string.Join(" | ", Summaries.Select(_ => "---")) + " |");
        foreach (var group in Scores.GroupBy(score => score.Id, StringComparer.Ordinal))
        {
            var byConfig = group.ToDictionary(score => score.Configuration, StringComparer.Ordinal);
            if (byConfig.Values.All(score => score.ArgumentsCorrect))
                continue;
            var first = group.First();
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {first.Id} | {Cell(first.Expected)} | {string.Join(" | ", Summaries.Select(s => byConfig.TryGetValue(s.Configuration, out var score) ? Cell(Describe(score)) : "–"))} |");
        }
        return md.ToString();
    }

    private static void AppendS4(StringBuilder md, S4Comparison s4)
    {
        md.AppendLine("## S4: 600-token state against a transcript-sized state");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"Plan §18, read by C-038: on the `history` cases, `{S4Comparison.ShortConfiguration}` (TurnState 600 tokens, 8 recent events) is at most 3 points below `{S4Comparison.WideConfiguration}` (2,000 tokens, every event one-liner) on Kev's calibrated `tool` decisions AND on its `done` decisions. Kev's answer is the argmax for `tool` and P(yes) ≥ 0.5 for `done`; acted means at or above the floor. Only ingress `tool` is paired (one per case in each arm); later `tool` and `done` decisions follow each arm's own trajectory.");
        md.AppendLine();
        md.AppendLine("| Measure (history cases) | 600 | Transcript | 600 − transcript |");
        md.AppendLine("| --- | --- | --- | --- |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| `tool`, all decisions | {Share(s4.Short.ToolRight, s4.Short.Tool)} | {Share(s4.Wide.ToolRight, s4.Wide.Tool)} | {Points(s4.ToolPoints)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| `done` | {Share(s4.Short.DoneRight, s4.Short.Done)} | {Share(s4.Wide.DoneRight, s4.Wide.Done)} | {Points(s4.DonePoints)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| `tool` at ingress (paired) | {Share(s4.Short.IngressToolRight, s4.Short.IngressTool)} {Range(s4.ShortIngressInterval)} | {Share(s4.Wide.IngressToolRight, s4.Wide.IngressTool)} {Range(s4.WideIngressInterval)} | {Points(s4.IngressToolPoints)} |");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"Ingress `tool` on {s4.PairedCases} paired cases: both right {s4.BothRight}, only 600 right {s4.ShortOnlyRight}, only transcript right {s4.WideOnlyRight}, both wrong {s4.BothWrong} (Wilson 95% intervals in brackets). **{(s4.Pass ? "Pass" : "Fail")}.**");
        md.AppendLine();
        md.AppendLine("| Slice | Arm | n | `tool` ingress | `tool` all | `done` | `tool` acted (right) | `done` acted (right) | Fallbacks | Shortlist hits | Ingress on hits | Compacted | Decider ingress ms p50 (p90) | Decider step ms p50 (p90) | State chars/4 p50 (max) | `recent` items p50 (max) |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var slice in s4.Slices)
            foreach (var arm in new[] { slice.Short, slice.Wide })
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {slice.Name} | {(arm == slice.Short ? "600" : "transcript")} | {arm.Cases} | {Share(arm.IngressToolRight, arm.IngressTool)} | {Share(arm.ToolRight, arm.Tool)} | {Share(arm.DoneRight, arm.Done)} | {arm.ToolActed} ({arm.ToolActedRight}) | {arm.DoneActed} ({arm.DoneActedRight}) | {arm.Fallbacks} | {arm.ShortlistHits}/{arm.ShortlistOf} | {Share(arm.IngressToolRightOnHits, arm.IngressToolOnHits)} | {arm.Compacted} | {D(arm.IngressMs)} | {D(arm.StepMs)} | {Max(arm.StateTokens)} | {Max(arm.RecentItems)} |");
        md.AppendLine();
    }

    private static string Range((double Low, double High) interval) =>
        double.IsFinite(interval.Low) ? string.Create(CultureInfo.InvariantCulture, $"[{interval.Low:0}–{interval.High:0}%]") : "";

    private static string Max(Distribution distribution) =>
        distribution.N == 0 ? "–" : string.Create(CultureInfo.InvariantCulture, $"{distribution.P50:0} ({distribution.Max:0})");

    private static string Describe(CaseScore score)
    {
        var text = ToolCallingScorer.Name(score.Outcome);
        if (score.Outcome is CaseOutcome.Correct or CaseOutcome.OverCalled && score.ArgsMatch == false)
            text += ", args miss";
        if (score.EscalationReason is not null)
            text += $" ({score.EscalationReason})";
        if (!score.Calls.IsEmpty)
            text += ": " + string.Join(", ", score.Calls.Select(call => (call.InvalidName ? "?" : call.Rejected ? "!" : "") + call.Tool));
        if (!score.ArgumentMisses.IsEmpty)
            text += "; " + string.Join("; ", score.ArgumentMisses);
        return text;
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Reasons(ConfigurationSummary summary) =>
        summary.EscalationReasons.IsEmpty ? "" : " (" + string.Join(", ", summary.EscalationReasons.Select(pair => $"{pair.Key} {pair.Value}")) + ")";

    private static string Share(int count, int of) =>
        of == 0 ? "–" : string.Create(CultureInfo.InvariantCulture, $"{count} ({100.0 * count / of:0.0}%)");

    private static string Points(double value) =>
        double.IsFinite(value) ? string.Create(CultureInfo.InvariantCulture, $"{value:+0.0;−0.0;0.0} points") : "–";

    private static string D(Distribution distribution) =>
        distribution.N == 0 ? "–" : string.Create(CultureInfo.InvariantCulture, $"{distribution.P50:0} ({distribution.P90:0})");
}
