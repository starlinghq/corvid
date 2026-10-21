using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Extraction;
using Corvid.Models.Privacy;

namespace S3.Gliner2Targets;

public sealed record PiiEntity(string Label, string Text);

public sealed record PiiRow(int Id, string Text, ImmutableArray<PiiEntity> Entities);

/// <summary>
/// GLiNER2 on the privacy egress gate's labels (C-015, <see cref="EgressPolicy.DefaultLabels"/>), filtered as
/// <c>PrivacyEgress.ScanAsync</c> filters: below the threshold, shorter than two characters, or without a letter or digit
/// is dropped. The gate redacts a value whatever label found it, so the gate-relevant recall is any-label.
/// </summary>
public static class PiiEval
{
    private const int MinValueLength = 2; // PrivacyEgressOptions.MinValueLength's default

    public static async Task<int> RunAsync(string repo)
    {
        Model.RequireMallocSetting();
        var rows = File.ReadLines(Path.Combine(Corpus.Folder(repo), "fixtures", "pii.jsonl"))
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonSerializer.Deserialize<PiiRow>(line, Corpus.Json)!).ToList();
        foreach (var row in rows)
            foreach (var entity in row.Entities)
                if (!row.Text.Contains(entity.Text, StringComparison.Ordinal) || !EgressPolicy.DefaultLabels.Contains(entity.Label))
                    throw new InvalidDataException($"PII row {row.Id}: '{entity.Text}' ({entity.Label}) is not in its text or not a gate label.");

        using var extractor = Model.Load();
        var labels = EgressPolicy.Default.AllLabels;
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"Fixture: {rows.Count} texts, {rows.Sum(r => r.Entities.Length)} labelled values " +
            $"({string.Join(", ", rows.SelectMany(r => r.Entities).GroupBy(e => e.Label).Select(g => $"{g.Key} {g.Count()}"))}); " +
            $"{rows.Count(r => r.Entities.IsEmpty)} texts with no PII. Labels: {string.Join(", ", labels)}.");
        var raw = new StringBuilder();
        foreach (var threshold in new[] { 0.3f, 0.4f, 0.5f })
        {
            var kept = new Dictionary<int, List<ExtractedSpan>>();
            foreach (var row in rows)
            {
                var result = await extractor.ExtractAsync(new ExtractionRequest(row.Text, labels, threshold));
                raw.AppendLine(JsonSerializer.Serialize(new { threshold, id = row.Id, spans = result.Spans }, Corpus.Json));
                kept[row.Id] = [.. result.Spans.Where(s => s.Confidence >= threshold && s.Text.Trim().Length >= MinValueLength
                    && s.Text.Any(char.IsLetterOrDigit))];
            }

            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"### Threshold {threshold:0.0}{(Math.Abs(threshold - EgressPolicy.Default.Threshold) < 1e-6 ? " (the gate's default)" : "")}");
            md.AppendLine();
            md.AppendLine("| Label | Gold | Recall, same label | Recall, any label (exact) | Recall, any label (covered) | Precision, same label |");
            md.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var label in labels.Append("all"))
            {
                var gold = rows.SelectMany(r => r.Entities.Where(e => label == "all" || e.Label == label).Select(e => (Row: r, Entity: e))).ToList();
                var same = gold.Count(g => kept[g.Row.Id].Any(s => s.Text.Trim() == g.Entity.Text && (label == "all" ? s.Label == g.Entity.Label : s.Label == label)));
                var any = gold.Count(g => kept[g.Row.Id].Any(s => s.Text.Trim() == g.Entity.Text));
                var covered = gold.Count(g => Covered(g.Row.Text, g.Entity.Text, kept[g.Row.Id]));
                var predicted = rows.SelectMany(r => kept[r.Id].Where(s => label == "all" || s.Label == label).Select(s => (Row: r, Span: s))).ToList();
                // A detected value is correct when it is, contains or lies within a labelled value of the same label.
                var correct = predicted.Count(p => p.Row.Entities.Any(e => e.Label == p.Span.Label && Related(p.Span.Text.Trim(), e.Text)));
                md.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {gold.Count} | {Ratio(same, gold.Count)} | {Ratio(any, gold.Count)} | " +
                    $"{Ratio(covered, gold.Count)} | {Ratio(correct, predicted.Count)} ({predicted.Count} spans) |");
            }

            if (Math.Abs(threshold - EgressPolicy.Default.Threshold) < 1e-6)
            {
                md.AppendLine();
                md.AppendLine("Values the gate would send at its default threshold (not covered by any span):");
                md.AppendLine();
                foreach (var row in rows)
                    foreach (var entity in row.Entities.Where(e => !Covered(row.Text, e.Text, kept[row.Id])))
                        md.AppendLine(CultureInfo.InvariantCulture, $"- {entity.Label}: `{entity.Text}` (text {row.Id})");
                md.AppendLine();
                md.AppendLine("Spans with no labelled value behind them (redacted needlessly):");
                md.AppendLine();
                foreach (var row in rows)
                    foreach (var span in kept[row.Id].Where(s => !row.Entities.Any(e => Related(s.Text.Trim(), e.Text))))
                        md.AppendLine(CultureInfo.InvariantCulture, $"- {span.Label}: `{span.Text}` {span.Confidence:0.00} (text {row.Id})");
            }
        }

        var results = Path.Combine(Corpus.Folder(repo), "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "pii-predictions.jsonl"), raw.ToString());
        File.WriteAllText(Path.Combine(results, "pii.md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }

    // Every character of the value lies inside some kept span (redaction would replace all of it).
    private static bool Covered(string text, string value, List<ExtractedSpan> spans)
    {
        var at = text.IndexOf(value, StringComparison.Ordinal);
        return Enumerable.Range(at, value.Length).All(i => char.IsWhiteSpace(text[i]) || spans.Any(s => s.Start <= i && i < s.End));
    }

    private static bool Related(string span, string value) =>
        span.Length > 0 && (span.Contains(value, StringComparison.Ordinal) || value.Contains(span, StringComparison.Ordinal));

    private static string Ratio(int part, int whole) => whole == 0 ? "–" : string.Create(CultureInfo.InvariantCulture, $"{part / (double)whole:0.000} ({part}/{whole})");
}
