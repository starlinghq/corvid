using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Corvid.App.Core.Audit;

/// <summary>
/// The wording the Audit page shares between its parts: summaries written with a small markup (<c>`mono`</c> and
/// <c>[[link]]</c>), the redaction markers in a request as sent, sizes, counts and the scan's line.
/// </summary>
public static partial class AuditText
{
    /// <summary>
    /// Splits text written with <c>`…`</c> for monospace and <c>[[…]]</c> for an in-app link into spans; a backslash
    /// makes the character after it plain (<see cref="Escape"/>). The markup is for text the app itself writes: anything
    /// from the model, a tool, a file or the person goes in through <see cref="Escape"/>, so it cannot style itself or
    /// pose as a link. The engine (Corvid.App.Engine) builds its spans structurally instead, one <see cref="AuditSpan"/>
    /// per piece, and never parses untrusted text. Every piece is cleaned (<see cref="SafeText.Clean"/>).
    /// </summary>
    public static ImmutableArray<AuditSpan> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = ImmutableArray.CreateBuilder<AuditSpan>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                plain.Append(text[i + 1]);
                i += 2;
            }
            else if (text[i] == '`' && IndexOfUnescaped(text, "`", i + 1) is var close and > 0)
            {
                Flush();
                spans.Add(new AuditSpan(SafeText.Clean(Unescape(text[(i + 1)..close])), AuditSpanStyle.Mono));
                i = close + 1;
            }
            else if (text.AsSpan(i).StartsWith("[[") && IndexOfUnescaped(text, "]]", i + 2) is var end and > 0)
            {
                Flush();
                spans.Add(new AuditSpan(SafeText.Clean(Unescape(text[(i + 2)..end])), AuditSpanStyle.Link));
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

    /// <summary>
    /// Untrusted text made safe to put into <see cref="Parse"/>'s markup: each backtick, bracket and backslash is escaped,
    /// so it reads as itself and starts no monospace span or link.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var escaped = new StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if (c is '`' or '[' or ']' or '\\')
                escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>Where <paramref name="value"/> next starts at or after <paramref name="from"/> outside an escape; -1 when it does not.</summary>
    private static int IndexOfUnescaped(string text, string value, int from)
    {
        for (var i = from; i <= text.Length - value.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(text, i, value, 0, value.Length) == 0)
                return i;
        }

        return -1;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
            return text;
        var plain = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
                i++;
            plain.Append(text[i]);
        }

        return plain.ToString();
    }

    /// <summary>The spans as one plain string, for a screen reader and for the "path or tool" filter.</summary>
    public static string Plain(IEnumerable<AuditSpan> spans) => string.Concat(spans.Select(span => span.Text));

    /// <summary>
    /// A line of a request as sent, cut where the gate put its markers, so the view can mark each redacted value. Two forms
    /// count: the privacy gate's placeholders as it sends them (<c>&lt;EMAIL_1&gt;</c>, an upper-case label, an underscore
    /// and a number), and a marker of the form <c>[redacted:credential]</c> (a lower-case kind); anything else that looks
    /// like one stays plain. In a redacted request the gate neutralizes placeholder-shaped text before it redacts, so every
    /// placeholder there is its own; markers are still read back out of text, which a tool's output or the model can
    /// imitate in a request sent as it was, so <see cref="CountRedactions"/> is checked against the gate's own count.
    /// </summary>
    public static ImmutableArray<RedactedSegment> Redactions(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var segments = ImmutableArray.CreateBuilder<RedactedSegment>();
        var at = 0;
        foreach (Match marker in RedactionMarker().Matches(line))
        {
            if (marker.Index > at)
                segments.Add(new RedactedSegment(line[at..marker.Index], false));
            segments.Add(new RedactedSegment(marker.Value, true));
            at = marker.Index + marker.Length;
        }

        if (at < line.Length || segments.Count == 0)
            segments.Add(new RedactedSegment(line[at..], false));
        return segments.ToImmutable();
    }

    /// <summary>
    /// How many redacted values the text holds, by <see cref="Redactions"/>'s rule: each <c>[redacted:kind]</c> marker, and
    /// each distinct placeholder once (a placeholder names one value, however often the request repeats it), which is how
    /// the gate counts what it redacted.
    /// </summary>
    public static int CountRedactions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        var markers = 0;
        foreach (Match marker in RedactionMarker().Matches(text))
        {
            if (marker.Value[0] == '<')
                placeholders.Add(marker.Value);
            else
                markers++;
        }
        return markers + placeholders.Count;
    }

    [GeneratedRegex(@"\[redacted:[a-z_]+\]|<[A-Z][A-Z0-9_]*_[0-9]+>", RegexOptions.CultureInvariant)]
    private static partial Regex RedactionMarker();

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
        var counts = severities.Select(SafeText.Clean).GroupBy(severity => severity, StringComparer.Ordinal)
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
