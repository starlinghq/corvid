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

/// <summary>One configuration's totals.</summary>
public sealed record ConfigurationSummary(
    string Configuration,
    int Cases,
    ImmutableSortedDictionary<string, int> Outcomes,
    int ToolCorrect,
    int ArgsCorrect,
    int NonEscalated,
    int ToolCorrectNonEscalated,
    int ArgsCorrectNonEscalated,
    int Escalated,
    ImmutableSortedDictionary<string, int> EscalationReasons,
    int Clarified,
    int ToolCases,
    int NoCall,
    int Nudged,
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

/// <summary>Plan §18's pass condition: (c) beats (a) by 25 points on tool choice and on argument match, (c) escalates under 30%.</summary>
public sealed record Headline(double ToolPoints, double ArgsPoints, double ToolPointsNonEscalated, double ArgsPointsNonEscalated,
    double EscalationRate, bool Pass);

/// <summary>Outcomes appended to Kev's <c>tool</c> decisions: each turn's first (ingress) and the later ones.</summary>
public sealed record KevOutcomes(int FirstAccepted, int FirstCorrected, int LaterAccepted, int LaterCorrected)
{
    public int Accepted => FirstAccepted + LaterAccepted;

    public int Corrected => FirstCorrected + LaterCorrected;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Accepted} accepted, {Corrected} corrected (first tool decision of a turn: {FirstAccepted} accepted, {FirstCorrected} corrected; later: {LaterAccepted} accepted, {LaterCorrected} corrected)");
}

/// <summary>The scored run: header, per-case records and per-configuration summaries; JSON and markdown (S5's tables).</summary>
public sealed class ToolCallingReport
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ToolCallingReport(ToolCallingHeader header, ImmutableArray<CaseScore> scores, ImmutableArray<ConfigurationSummary> summaries,
        Headline? headline)
    {
        Header = header;
        Scores = scores;
        Summaries = summaries;
        Headline = headline;
    }

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
        Headline? headline = null;
        if (a is not null && c is not null)
        {
            var tool = c.Rate(c.ToolCorrect) - a.Rate(a.ToolCorrect);
            var args = c.Rate(c.ArgsCorrect) - a.Rate(a.ArgsCorrect);
            var escalation = c.Rate(c.Escalated);
            headline = new Headline(tool, args, Pct(c.ToolCorrectNonEscalated, c.NonEscalated) - Pct(a.ToolCorrectNonEscalated, a.NonEscalated),
                Pct(c.ArgsCorrectNonEscalated, c.NonEscalated) - Pct(a.ArgsCorrectNonEscalated, a.NonEscalated), escalation,
                tool >= 25 && args >= 25 && escalation < 30);
        }
        return new ToolCallingReport(header, all, summaries, headline);
    }

    private static double Pct(int count, int of) => of == 0 ? double.NaN : 100.0 * count / of;

    private static ConfigurationSummary Summarize(string name, List<CaseScore> scores)
    {
        var outcomes = Enum.GetValues<CaseOutcome>().ToImmutableSortedDictionary(ToolCallingScorer.Name,
            outcome => scores.Count(score => score.Outcome == outcome), StringComparer.Ordinal);
        var kept = scores.Where(score => score.Outcome != CaseOutcome.Escalated).ToList();
        var toolCases = scores.Where(score => !score.Tags.Contains("no_tool")).ToList();
        var offeredCalls = scores.Where(score => score.ArgsValid is not null).ToList();
        return new ConfigurationSummary(name, scores.Count, outcomes,
            scores.Count(score => score.ToolCorrect), scores.Count(score => score.ArgumentsCorrect),
            kept.Count, kept.Count(score => score.ToolCorrect), kept.Count(score => score.ArgumentsCorrect),
            scores.Count(score => score.Outcome == CaseOutcome.Escalated),
            scores.Where(score => score.EscalationReason is not null).GroupBy(score => score.EscalationReason!, StringComparer.Ordinal)
                .ToImmutableSortedDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            scores.Count(score => score.Outcome == CaseOutcome.Clarified),
            toolCases.Count, toolCases.Count(score => score.Outcome == CaseOutcome.NoCall),
            scores.Count(score => score.Nudged), scores.Count(score => score.FirstResponseCorrect), scores.Count(score => score.RepairedToCorrect),
            offeredCalls.Count(score => score.ArgsValid == true), offeredCalls.Count,
            Distribution.Of(scores.Select(score => score.TurnMs)), Distribution.Of(scores.Select(score => score.GeneratorMs)),
            Distribution.Of(scores.Select(score => (double)score.GeneratorCalls)),
            Distribution.Of(scores.Where(score => score.DeciderCalls > 0).Select(score => score.DeciderMs)),
            Distribution.Of(scores.Where(score => score.EmbedMs > 0).Select(score => score.EmbedMs)),
            Distribution.Of(scores.Where(score => score.PromptTokens is not null).Select(score => (double)score.PromptTokens!)));
    }

    // ---- floor fitting ----

    /// <summary>
    /// For floor fitting (C-021): one <c>DecisionOutcomeRecorded</c> per calibrated <c>tool</c> decision in (c)'s runs,
    /// <c>accepted</c> when Kev's pick is what the case expects at that point (the step after the calls that ran so far, or
    /// none once they are done or on a no-tool case), <c>corrected</c> otherwise. Appended after the loop's own
    /// <c>tool_succeeded</c>, so it is the decision's last outcome and labels it. The log is then valid input to
    /// <c>corvid-evals calibrate --log</c>.
    /// </summary>
    /// <returns>The outcomes appended, split into each turn's first <c>tool</c> decision (ingress) and later ones (the no-op
    /// check and step checkpoints, where a case whose calls have all run expects none).</returns>
    public static KevOutcomes AppendKevOutcomes(IEventLog log, IEnumerable<CaseRun> runs)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runs);
        int firstAccepted = 0, firstCorrected = 0, laterAccepted = 0, laterCorrected = 0;
        foreach (var run in runs)
        {
            var events = new List<EventData>();
            var done = 0;
            var first = true;
            foreach (var payload in run.Events)
            {
                if (payload is ToolResult { IsError: false })
                    done++;
                if (payload is not Decision { QuestionId: "tool", Calibrated: true } decision || decision.Probabilities.IsEmpty)
                    continue;
                var pick = decision.Probabilities.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
                var expect = run.Case.ExpectsNoTool || done >= run.Case.Steps.Length ? Expectation.NoTool : run.Case.Steps[done].Expect;
                var right = expect.None ? pick == "none" : expect.Accepts(pick);
                events.Add(EventData.Create(new DecisionOutcomeRecorded(decision.DecisionId, right ? "accepted" : "corrected")));
                if (first)
                {
                    if (right)
                        firstAccepted++;
                    else
                        firstCorrected++;
                }
                else if (right)
                {
                    laterAccepted++;
                }
                else
                {
                    laterCorrected++;
                }
                first = false;
            }
            if (events.Count > 0)
                log.Append(run.SessionId, log.GetHead(run.SessionId), events);
        }
        return new KevOutcomes(firstAccepted, firstCorrected, laterAccepted, laterCorrected);
    }

    // ---- JSON ----

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Indented))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "corvid-tool-calling-report/1");
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
                writer.WriteBoolean("pass", headline.Pass);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }
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
            md.AppendLine("Plan §18: (c) beats (a) by ≥ 25 points on tool choice and argument match; (c)'s escalation rate below 30%. An escalation counts as a miss.");
            md.AppendLine();
            md.AppendLine("| Measure | (c) − (a), all cases | (c) − (a), non-escalated cases |");
            md.AppendLine("| --- | --- | --- |");
            md.AppendLine(CultureInfo.InvariantCulture, $"| Tool choice | {Points(headline.ToolPoints)} | {Points(headline.ToolPointsNonEscalated)} |");
            md.AppendLine(CultureInfo.InvariantCulture, $"| Argument match | {Points(headline.ArgsPoints)} | {Points(headline.ArgsPointsNonEscalated)} |");
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"Escalation rate of (c): {headline.EscalationRate:0.0}%. **{(headline.Pass ? "Pass" : "Fail")}.**");
            md.AppendLine();
        }

        md.AppendLine("## Outcomes");
        md.AppendLine();
        md.AppendLine("| Configuration | n | Correct tool | Args match | Wrong tool | Invalid name | Malformed | No call | Called on no-tool | Clarified | Escalated | Failed | Correct, non-escalated | Args valid (first offered call) | First response correct | Repaired | Nudged |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var s in Summaries)
        {
            int O(CaseOutcome outcome) => s.Outcomes[ToolCallingScorer.Name(outcome)];
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Configuration} | {s.Cases} | {Share(s.ToolCorrect, s.Cases)} | {Share(s.ArgsCorrect, s.Cases)} | {O(CaseOutcome.WrongTool)} | {O(CaseOutcome.InvalidName)} | {O(CaseOutcome.Malformed)} | {O(CaseOutcome.NoCall)} of {s.ToolCases} | {O(CaseOutcome.CalledOnNoTool)} | {s.Clarified} | {s.Escalated}{Reasons(s)} | {O(CaseOutcome.Failed)} | {Share(s.ToolCorrectNonEscalated, s.NonEscalated)} | {s.ArgsValid}/{s.ArgsValidOf} | {s.FirstResponseCorrect} | {s.RepairedToCorrect} | {s.Nudged} |");
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

    private static string Describe(CaseScore score)
    {
        var text = ToolCallingScorer.Name(score.Outcome);
        if (score.Outcome == CaseOutcome.Correct && score.ArgsMatch == false)
            text = "correct, args miss";
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
