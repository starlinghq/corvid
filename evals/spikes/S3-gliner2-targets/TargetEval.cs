using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Extraction;
using Corvid.Models.Starling;

namespace S3.Gliner2Targets;

/// <summary>A label phrasing: the labels sent to GLiNER2 and the target kind each one stands for (null: not scored).</summary>
public sealed record LabelSet(string Name, string Origin, ImmutableArray<(string Label, string? Kind)> Labels);

public sealed record Prediction(string Kind, string Label, string Text, int Start, int End, float Confidence);

public sealed record KindScore(int Gold, int ExactHits, int OverlapHits, int Predicted, int PredictedCorrect)
{
    public double Recall => Gold == 0 ? double.NaN : ExactHits / (double)Gold;
    public double OverlapRecall => Gold == 0 ? double.NaN : OverlapHits / (double)Gold;
    public double Precision => Predicted == 0 ? double.NaN : PredictedCorrect / (double)Predicted;
}

public static class TargetEval
{
    public static readonly string[] Scored = ["path", "pid", "host", "url"];
    public static readonly string[] Gate = ["path", "pid", "host"];
    public static readonly float[] Thresholds = [0.3f, 0.4f, 0.5f];

    public static readonly ImmutableArray<LabelSet> LabelSets =
    [
        // Plan §10.2 step 2 names these labels for GLiNER2 on command arguments; it has no URL label.
        new("plan", "plan §10.2", [("file path", "path"), ("process", "pid"), ("host", "host"), ("database table", null), ("credential", null)]),
        // The target kinds the analyzer emits on the wire (WireNames.Kind).
        new("wire", "WireNames.Kind", [("path", "path"), ("process", "pid"), ("host", "host"), ("url", "url")]),
        // Descriptive phrasings, which GLiNER2's label encoder may read better.
        new("descriptive", "this spike", [("file or directory path", "path"), ("process id", "pid"), ("hostname or IP address", "host"), ("URL", "url")]),
    ];

    public static async Task<int> RunAsync(string repo)
    {
        Model.RequireMallocSetting();
        var rows = File.ReadLines(Path.Combine(Corpus.Folder(repo), "fixtures", "targets-corpus.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CorpusRow>(line, Corpus.Json)!).ToList();
        using var extractor = Model.Load();

        var predictions = new Dictionary<(string Set, float Threshold), Dictionary<int, List<Prediction>>>();
        var raw = new StringBuilder();
        var timings = new List<double>();
        foreach (var set in LabelSets)
            foreach (var threshold in Thresholds)
            {
                var byRow = new Dictionary<int, List<Prediction>>();
                foreach (var row in rows)
                {
                    var watch = Stopwatch.StartNew();
                    var result = await extractor.ExtractAsync(new ExtractionRequest(row.Command, [.. set.Labels.Select(l => l.Label)], threshold));
                    timings.Add(watch.Elapsed.TotalMilliseconds);
                    var kinds = set.Labels.ToDictionary(l => l.Label, l => l.Kind);
                    byRow[row.Id] = [.. result.Spans.Where(s => kinds[s.Label] is not null)
                        .Select(s => new Prediction(kinds[s.Label]!, s.Label, s.Text, s.Start, s.End, s.Confidence))];
                    raw.AppendLine(JsonSerializer.Serialize(new { set = set.Name, threshold, id = row.Id, spans = result.Spans }, Corpus.Json));
                }

                predictions[(set.Name, threshold)] = byRow;
            }

        var results = Path.Combine(Corpus.Folder(repo), "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "targets-predictions.jsonl"), raw.ToString());

        // Choose on the tune half only: the configuration whose worst gate recall (path, pid, host) is highest, then the
        // best mean gate precision.
        var chosen = predictions.Keys
            .OrderByDescending(key => Gate.Min(kind => Score(rows, "tune", predictions[key], kind).Recall))
            .ThenByDescending(key => Gate.Average(kind => Nz(Score(rows, "tune", predictions[key], kind).Precision)))
            .First();

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"Commands: {rows.Count} (tune {rows.Count(r => r.Split == "tune")}, report {rows.Count(r => r.Split == "report")}). " +
            $"GLiNER2 calls: {timings.Count}, median {Percentile(timings, 0.5):0} ms, p95 {Percentile(timings, 0.95):0} ms on the CPU.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Chosen on the tune half: **{chosen.Set} @ {chosen.Threshold:0.0}**.");
        foreach (var split in new[] { "report", "tune", "all" })
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"### {split} half{(split == "all" ? " (both halves)" : "")}: recall exact / overlap, precision (gold count)");
            md.AppendLine();
            md.AppendLine("| Labels | Threshold | path | pid | host | url | min gate recall |");
            md.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var key in predictions.Keys)
            {
                var cells = Scored.Select(kind => Cell(Score(rows, split, predictions[key], kind)));
                var min = Gate.Min(kind => Score(rows, split, predictions[key], kind).Recall);
                var mark = key == chosen ? " **(chosen)**" : "";
                md.AppendLine(CultureInfo.InvariantCulture, $"| {key.Set}{mark} | {key.Threshold:0.0} | {string.Join(" | ", cells)} | {min:0.000} |");
            }
        }

        // Where the chosen configuration misses, by the shape of the path as written.
        md.AppendLine();
        md.AppendLine("### Path recall by shape, chosen configuration, report half");
        md.AppendLine();
        md.AppendLine("| Shape | Gold | Exact hits | Recall |");
        md.AppendLine("| --- | --- | --- | --- |");
        var reportPaths = rows.Where(r => r.Split == "report").SelectMany(r => r.Targets.Where(t => t.Kind == "path" && t.Scored).Select(t => (Row: r, Target: t))).ToList();
        foreach (var group in reportPaths.GroupBy(p => Shape(p.Target.Written)).OrderByDescending(g => g.Count()))
        {
            var hits = group.Count(p => predictions[chosen][p.Row.Id].Any(pred => pred.Kind == "path" && Exact(pred.Text, p.Target)));
            md.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {hits} | {hits / (double)group.Count():0.000} |");
        }

        md.AppendLine();
        md.AppendLine("### Misses, chosen configuration, report half");
        md.AppendLine();
        md.AppendLine("| Kind | As written | Command | GLiNER2 spans of that kind |");
        md.AppendLine("| --- | --- | --- | --- |");
        foreach (var row in rows.Where(r => r.Split == "report"))
            foreach (var target in row.Targets.Where(t => Gate.Contains(t.Kind) && t.Scored))
            {
                var mine = predictions[chosen][row.Id].Where(p => p.Kind == target.Kind).ToList();
                if (mine.Any(p => Exact(p.Text, target)))
                    continue;
                md.AppendLine(CultureInfo.InvariantCulture, $"| {target.Kind} | {Code(target.Written)} | {Code(row.Command)} | " +
                    $"{(mine.Count == 0 ? "none" : string.Join(", ", mine.Select(p => $"{Code(p.Text)} {p.Confidence:0.00}")))} |");
            }

        File.WriteAllText(Path.Combine(results, "targets.md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }

    public static KindScore Score(List<CorpusRow> rows, string split, Dictionary<int, List<Prediction>> predictions, string kind)
    {
        int gold = 0, exact = 0, overlap = 0, predicted = 0, correct = 0;
        foreach (var row in rows.Where(r => split == "all" || r.Split == split))
        {
            var mine = predictions[row.Id].Where(p => p.Kind == kind).ToList();
            foreach (var target in row.Targets.Where(t => t.Kind == kind && t.Scored))
            {
                gold++;
                if (mine.Any(p => Exact(p.Text, target)))
                    exact++;
                if (mine.Any(p => Overlaps(p, target, row.Command)))
                    overlap++;
            }

            foreach (var text in mine.Select(p => Norm(p.Text)).Distinct(StringComparer.Ordinal))
            {
                var targets = row.Targets.Where(t => t.Kind == kind && t.Scored).ToList();
                // A "process" span naming the program pkill or killall signals is not a PID, but not a false target either;
                // nor is a span on an audited over-approximation.
                if (kind == "pid" && row.Targets.Any(t => t.Kind == "process_name" && Exact(text, t))
                    || row.Targets.Any(t => !t.Scored && Exact(text, t with { Kind = kind })))
                    continue;
                predicted++;
                if (targets.Any(t => Exact(text, t)))
                    correct++;
            }
        }

        return new KindScore(gold, exact, overlap, predicted, correct);
    }

    /// <summary>
    /// The span is the target as written (quotes and a trailing separator aside); for a host, the bare host name the
    /// analyzer resolved also counts (<c>example.com</c> out of <c>https://example.com/x</c> or <c>user@example.com:path</c>).
    /// </summary>
    public static bool Exact(string predicted, GoldTarget target)
    {
        var text = Norm(predicted);
        return text == Norm(target.Written)
            || target.Kind is "host" or "pid" && string.Equals(text, target.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The span overlaps an occurrence of the target (or of its host name) in the command: an upper bound.</summary>
    public static bool Overlaps(Prediction prediction, GoldTarget target, string command)
    {
        foreach (var needle in target.Kind == "host" ? [target.Written, target.Value] : new[] { target.Written })
        {
            if (needle.Length == 0)
                continue;
            for (var at = command.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = command.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
                if (prediction.Start < at + needle.Length && at < prediction.End)
                    return true;
        }

        return false;
    }

    public static string Norm(string text)
    {
        var value = text.Trim().TrimEnd(';', ',');
        while (value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"'))
            value = value[1..^1];
        return value.Trim('\'', '"');
    }

    public static string Shape(string written) => written switch
    {
        "/" or "." or ".." or "~" or "*" => "one character or dots (/, ., .., ~, *)",
        _ when written.Replace("../", "", StringComparison.Ordinal) is ".." or "" => "one character or dots (/, ., .., ~, *)",
        _ when written.AsSpan().IndexOfAny("*?[") >= 0 => "glob",
        _ when written.StartsWith("/dev/", StringComparison.Ordinal) => "device (/dev/...)",
        _ when written.StartsWith('~') => "home (~/...)",
        _ when written.StartsWith('/') => "absolute",
        _ when written.Contains('/', StringComparison.Ordinal) => "relative with a directory",
        _ => "bare file or directory name",
    };

    private static string Cell(KindScore score) => score.Gold == 0
        ? "–"
        : string.Create(CultureInfo.InvariantCulture, $"{score.Recall:0.000} / {score.OverlapRecall:0.000}, P {Nz(score.Precision):0.000} ({score.Gold})");

    private static double Nz(double value) => double.IsNaN(value) ? 0 : value;

    private static string Code(string text) => "`" + text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "⏎", StringComparison.Ordinal)
        .Replace("`", "ˋ", StringComparison.Ordinal) + "`";

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
    }
}

internal static class Model
{
    public static void RequireMallocSetting()
    {
        if (OperatingSystem.IsLinux() && System.Environment.GetEnvironmentVariable("MALLOC_MMAP_THRESHOLD_") != "65536")
            throw new InvalidOperationException("Start the process with MALLOC_MMAP_THRESHOLD_=65536 (Starling's requirement for GLiNER2 on Linux).");
    }

    /// <summary>GLiNER2 through Corvid's adapter, from the Starling cache Corvid's acquisition code resolves (CPU, never downloads).</summary>
    public static Gliner2Extractor Load()
    {
        var store = new StarlingModelStore(System.Environment.GetEnvironmentVariable("CORVID_STARLING_MODELS"));
        var status = store.Check(StarlingModel.Gliner2, Starlings.Acquisition.CacheVerification.Size);
        Console.Error.WriteLine(status.Describe());
        return Gliner2Extractor.Create(store.OpenGliner2());
    }
}
