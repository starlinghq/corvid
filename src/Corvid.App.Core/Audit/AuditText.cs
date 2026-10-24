using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Corvid.App.Core.Audit;

/// <summary>
/// The wording the Audit page shares between its parts: summaries written with a small markup (<c>`mono`</c> and
/// <c>[[link]]</c>), the redaction markers in a request as sent, sizes, counts and the scan's line.
/// </summary>
public static class AuditText
{
    /// <summary>
    /// Splits text written with <c>`…`</c> for monospace and <c>[[…]]</c> for an in-app link into spans. Text from the
    /// model or a tool goes in as data: the markup is the demo's and the engine's, applied to cleaned strings.
    /// </summary>
    public static ImmutableArray<AuditSpan> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = ImmutableArray.CreateBuilder<AuditSpan>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var close and > 0)
            {
                Flush();
                spans.Add(new AuditSpan(SafeText.Clean(text[(i + 1)..close]), AuditSpanStyle.Mono));
                i = close + 1;
            }
            else if (text.AsSpan(i).StartsWith("[[") && text.IndexOf("]]", i + 2, StringComparison.Ordinal) is var end and > 0)
            {
                Flush();
                spans.Add(new AuditSpan(SafeText.Clean(text[(i + 2)..end]), AuditSpanStyle.Link));
                i = end + 2;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }

        Flush();
        return spans.ToImmutable();

        void Flush()
        {
            if (plain.Length == 0)
                return;
            spans.Add(new AuditSpan(SafeText.Clean(plain.ToString())));
            plain.Clear();
        }
    }

    /// <summary>The spans as one plain string, for a screen reader and for the "path or tool" filter.</summary>
    public static string Plain(IEnumerable<AuditSpan> spans) => string.Concat(spans.Select(span => span.Text));

    /// <summary>
    /// A line of a request as sent, cut where the gate put its markers (<c>[redacted:credential]</c>), so the view can mark
    /// each redacted value.
    /// </summary>
    public static ImmutableArray<RedactedSegment> Redactions(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var segments = ImmutableArray.CreateBuilder<RedactedSegment>();
        var at = 0;
        while (at < line.Length)
        {
            var start = line.IndexOf("[redacted:", at, StringComparison.Ordinal);
            var end = start < 0 ? -1 : line.IndexOf(']', start);
            if (start < 0 || end < 0)
            {
                segments.Add(new RedactedSegment(line[at..], false));
                break;
            }

            if (start > at)
                segments.Add(new RedactedSegment(line[at..start], false));
            segments.Add(new RedactedSegment(line[start..(end + 1)], true));
            at = end + 1;
        }

        return segments.ToImmutable();
    }

    /// <summary>"38.2 KB", "812 B", "1.4 MB": decimal kilobytes, as the mockups write sizes.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1000.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0.0} MB"),
    };

    /// <summary>
    /// The status bar's detections: "1 high this session", "2 high, 1 medium in the log", "none this session". Severities
    /// are counted most severe first, in the order high, medium, low, then any other the rules name.
    /// </summary>
    public static string DetectionSummary(IEnumerable<string> severities, bool session)
    {
        ArgumentNullException.ThrowIfNull(severities);
        var scope = session ? "this session" : "in the log";
        string[] order = ["high", "medium", "low"];
        var counts = severities.GroupBy(severity => severity, StringComparer.Ordinal)
            .OrderBy(group => Array.IndexOf(order, group.Key) is var index && index >= 0 ? index : order.Length)
            .Select(group => string.Create(CultureInfo.InvariantCulture, $"{group.Count()} {group.Key}"))
            .ToList();
        return counts.Count == 0 ? $"none {scope}" : $"{string.Join(", ", counts)} {scope}";
    }

    /// <summary>"1,214", "9,410": thousands separated by commas whatever the machine's culture.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"14:10 · 1 high, 0 medium · would exit 4 with --fail-on high", the time in the clock's zone.</summary>
    public static string Scan(ScanResult scan, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(zone);
        var at = TimeZoneInfo.ConvertTime(scan.At, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var outcome = scan.ExitCode == 0
            ? $"would exit 0 with --fail-on {scan.FailOn}"
            : string.Create(CultureInfo.InvariantCulture, $"would exit {scan.ExitCode} with --fail-on {scan.FailOn}");
        return string.Create(CultureInfo.InvariantCulture, $"{at} · {scan.High} high, {scan.Medium} medium · {outcome}");
    }

    /// <summary>"14:03:52", in the clock's zone.</summary>
    public static string Time(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The Filter panel's label for a group.</summary>
    public static string GroupLabel(AuditEventGroup group) => group switch
    {
        AuditEventGroup.Messages => "messages",
        AuditEventGroup.Tools => "tool calls and results",
        AuditEventGroup.Approvals => "approvals",
        AuditEventGroup.Decisions => "decisions",
        AuditEventGroup.ModelSwitches => "model switches",
        AuditEventGroup.Detections => "detections",
        AuditEventGroup.Checkpoints => "checkpoints",
        AuditEventGroup.MemoryAndSkills => "memory and skills",
        AuditEventGroup.Compaction => "compaction",
        _ => group.ToString(),
    };
}

/// <summary>A piece of a line as sent: plain text, or a value the gate redacted.</summary>
public sealed record RedactedSegment(string Text, bool IsRedaction);
