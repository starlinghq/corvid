using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S3.Gliner2Targets;

/// <summary>
/// A third scoring of the committed predictions (results/targets-predictions.jsonl), added in review: token containment,
/// which sits between exact match and overlap. It loads no model.
/// </summary>
/// <remarks>
/// A span hits a target when, split on whitespace and <c>=</c>, one of its tokens (quotes and a trailing <c>;</c> or
/// <c>,</c> stripped, <c>~</c>, <c>$HOME</c> or <c>${HOME}</c> mapped to the home directory, a trailing <c>/</c> dropped)
/// equals the target as written or its resolved value. A host also hits on the host name inside a URL or
/// <c>user@host</c> token. So <c>-rf /</c> hits <c>/</c> and <c>rm -rf /etc</c> hits <c>/etc</c>, but a span
/// <c>/etc/passwd</c> does not hit <c>/etc</c>. "Same label" counts spans of the target's kind; "any label" counts a span of any kind,
/// which is not usable as an approval target but shows whether the model put a span over the word at all.
/// </remarks>
public static partial class Rescore
{
    private sealed record Span(string Label, string Text, int Start, int End, float Confidence);

    private sealed record PredictionLine(string Set, float Threshold, int Id, Span[] Spans);

    public static int Run(string repo)
    {
        var folder = Corpus.Folder(repo);
        var rows = File.ReadLines(Path.Combine(folder, "fixtures", "targets-corpus.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CorpusRow>(line, Corpus.Json)!).ToDictionary(r => r.Id);
        var runs = File.ReadLines(Path.Combine(folder, "results", "targets-predictions.jsonl"))
            .Select(line => JsonSerializer.Deserialize<PredictionLine>(line, Corpus.Json)!)
            .GroupBy(r => (r.Set, r.Threshold)).ToList();
        var kinds = TargetEval.LabelSets.ToDictionary(s => s.Name, s => s.Labels.ToDictionary(l => l.Label, l => l.Kind));

        var md = new StringBuilder();
        md.AppendLine("Token-containment recall over the committed predictions (see `Rescore.cs` for the rule). " +
            "Each cell: same label / any label (gold count).");
        foreach (var split in new[] { "report", "tune", "all" })
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"### {split} half{(split == "all" ? " (both halves)" : "")}");
            md.AppendLine();
            md.AppendLine("| Labels | Threshold | path | pid | host | min gate recall, same label |");
            md.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var run in runs)
            {
                var cells = new List<string>();
                var min = double.MaxValue;
                foreach (var kind in TargetEval.Gate)
                {
                    int gold = 0, same = 0, any = 0;
                    foreach (var prediction in run)
                    {
                        var row = rows[prediction.Id];
                        if (split != "all" && row.Split != split)
                            continue;
                        foreach (var target in row.Targets.Where(t => t.Kind == kind && t.Scored))
                        {
                            gold++;
                            if (prediction.Spans.Any(s => kinds[run.Key.Set].GetValueOrDefault(s.Label) == kind && Contains(s.Text, target)))
                                same++;
                            if (prediction.Spans.Any(s => Contains(s.Text, target)))
                                any++;
                        }
                    }

                    min = Math.Min(min, same / (double)gold);
                    cells.Add(string.Create(CultureInfo.InvariantCulture, $"{same / (double)gold:0.000} / {any / (double)gold:0.000} ({gold})"));
                }

                md.AppendLine(CultureInfo.InvariantCulture, $"| {run.Key.Set} | {run.Key.Threshold:0.0} | {string.Join(" | ", cells)} | {min:0.000} |");
            }
        }

        File.WriteAllText(Path.Combine(folder, "results", "targets-contained.md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }

    public static bool Contains(string span, GoldTarget target)
    {
        if (target.Kind == "host")
        {
            var hosts = HostTokens(span);
            return hosts.Contains(target.Value.ToLowerInvariant()) || hosts.Contains(target.Written.ToLowerInvariant());
        }

        var tokens = Tokens(span).Select(Norm).ToHashSet(StringComparer.Ordinal);
        return tokens.Contains(Norm(target.Written)) || tokens.Contains(Norm(target.Value));
    }

    private static IEnumerable<string> Tokens(string span) => Separator().Split(span).Where(t => t.Length > 0);

    private static string Norm(string token)
    {
        var value = Home().Replace(token.Trim().TrimEnd(';', ',').Trim('\'', '"'), "/home/user");
        return value.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";
    }

    private static HashSet<string> HostTokens(string span)
    {
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Tokens(span))
        {
            var token = raw.Trim('\'', '"').ToLowerInvariant();
            hosts.Add(token);
            if (UrlHost().Match(token) is { Success: true } url)
                hosts.Add(url.Groups["host"].Value);
            if (UserHost().Match(token) is { Success: true } user)
                hosts.Add(user.Groups["host"].Value);
        }

        return hosts;
    }

    [GeneratedRegex(@"[\s=]+")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(\$\{HOME\}|\$HOME|~)(?=/|$)")]
    private static partial Regex Home();

    [GeneratedRegex(@"^[a-z]+://([^/:@]*@)?(?<host>[^/:]+)")]
    private static partial Regex UrlHost();

    [GeneratedRegex(@"^[^@/]+@(?<host>[^:/]+)")]
    private static partial Regex UserHost();
}
