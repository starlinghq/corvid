using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Corvid.Memory;

/// <summary>One rule a lesson draft broke.</summary>
/// <param name="Field">title, applicability, body, tags, kind, evidence, or the field a content rule matched in.</param>
public sealed record LessonViolation(string Field, string Rule, string Detail);

/// <summary>What a lesson says, before it has an id or a state.</summary>
public sealed record LessonContent(
    string Kind,
    string Title,
    string Applicability,
    string Body,
    ImmutableArray<string> Tags,
    ImmutableArray<LessonEvidence> Evidence);

/// <summary>
/// The quality bar for a lesson (C-025), after Beacon's lesson-quality reference: a known kind; an imperative, single-line
/// title under 80 characters; an applicability that starts with when, before or after; a 3–10 line body that cites at
/// least one evidence position; 2–4 lowercase tags; evidence. It refuses summaries of the session, one-off or
/// machine-specific content (home paths, addresses, dates, weekdays, UUIDs, PR or issue numbers), secret-looking strings
/// and instructions aimed at the agent. Every violation is returned, not only the first. The rules are heuristics: they
/// refuse a draft, never a turn.
/// </summary>
public static partial class LessonValidator
{
    public const int MaxTitleLength = 79;

    private static readonly ImmutableHashSet<string> NarrativeOpeners = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "the", "this", "that", "we", "i", "agent", "user", "it", "they", "a", "an");

    // Imperatives that end in "ed" (the "-eed" verbs are allowed by rule).
    private static readonly ImmutableHashSet<string> EdImperatives = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "embed", "shred", "wed", "bed", "shed", "red", "sled", "speed", "seed", "feed", "need", "heed", "bleed", "breed", "weed");

    private static readonly (string Rule, Regex Pattern)[] ContentRules =
    [
        ("summary", SummaryPattern()),
        ("injection", InjectionPattern()),
        ("home_path", HomePathPattern()),
        ("ip_address", IpPattern()),
        ("date", DatePattern()),
        ("relative_day", DayPattern()),
        ("uuid", UuidPattern()),
        ("issue_number", IssuePattern()),
        ("secret", SecretPattern()),
    ];

    public static ImmutableArray<LessonViolation> Validate(LessonContent lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var violations = ImmutableArray.CreateBuilder<LessonViolation>();
        void Add(string field, string rule, string detail) => violations.Add(new LessonViolation(field, rule, detail));

        if (!LessonKinds.IsKind(lesson.Kind))
            Add("kind", "kind", $"'{lesson.Kind}' is not one of {string.Join(", ", LessonKinds.All)}");

        var title = lesson.Title ?? "";
        if (string.IsNullOrWhiteSpace(title))
            Add("title", "title_empty", "the title is empty");
        else
        {
            if (title.Contains('\n', StringComparison.Ordinal) || title.Contains('\r', StringComparison.Ordinal))
                Add("title", "title_one_line", "the title is more than one line");
            if (title.Trim().Length > MaxTitleLength)
                Add("title", "title_length", $"the title is {title.Trim().Length} characters, the limit is {MaxTitleLength}");
            var first = FirstWord(title);
            if (NarrativeOpeners.Contains(first))
                Add("title", "title_imperative", $"the title opens with '{first}': say what to do");
            else if (first.Length > 3 && first.EndsWith("ed", StringComparison.OrdinalIgnoreCase)
                     && !first.EndsWith("eed", StringComparison.OrdinalIgnoreCase) && !EdImperatives.Contains(first))
                Add("title", "title_imperative", $"the title opens with '{first}', which reads as past tense: say what to do");
        }

        var applicability = (lesson.Applicability ?? "").TrimStart();
        if (!(applicability.StartsWith("when ", StringComparison.OrdinalIgnoreCase)
              || applicability.StartsWith("before ", StringComparison.OrdinalIgnoreCase)
              || applicability.StartsWith("after ", StringComparison.OrdinalIgnoreCase)))
            Add("applicability", "applicability_trigger", "the applicability starts with 'when', 'before' or 'after'");

        var body = lesson.Body ?? "";
        var lines = body.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));
        if (lines is < 3 or > 10)
            Add("body", "body_lines", $"the body has {lines} non-empty line(s); it needs 3 to 10");
        var evidence = lesson.Evidence.IsDefault ? [] : lesson.Evidence;
        var positions = evidence.SelectMany(item => item.Positions).ToHashSet();
        var cited = Citations(body).ToList();
        if (cited.Count == 0)
            Add("body", "body_citation", "the body cites no event (\"event 14\", \"events 14-19\")");
        else if (!cited.Any(range => positions.Any(position => position >= range.From && position <= range.Through)))
            Add("body", "body_citation", "the events the body cites are not in the evidence");

        var tags = lesson.Tags.IsDefault ? [] : lesson.Tags;
        if (tags.Distinct(StringComparer.Ordinal).Count() is < 2 or > 4 || tags.Length != tags.Distinct(StringComparer.Ordinal).Count())
            Add("tags", "tags_count", $"{tags.Length} tag(s); a lesson has 2 to 4 distinct tags");
        foreach (var tag in tags.Where(tag => !TagPattern().IsMatch(tag ?? "")))
            Add("tags", "tag_format", $"'{tag}' is not lowercase letters, digits and hyphens, at most 32");

        if (evidence.IsEmpty)
            Add("evidence", "evidence", "a lesson needs at least one evidence item");

        // Content rules, over every field a reader sees.
        var fields = new List<(string Field, string Text)>
        {
            ("title", title), ("applicability", lesson.Applicability ?? ""), ("body", body), ("tags", string.Join(' ', tags)),
        };
        fields.AddRange(evidence.Select(item => ("evidence", item.Summary ?? "")));
        foreach (var (field, text) in fields)
        {
            foreach (var (rule, pattern) in ContentRules)
                if (pattern.Match(text) is { Success: true } match)
                    Add(field, rule, $"'{Excerpt(match.Value)}'");
            if (EncodedSecret(text) is { } encoded)
                Add(field, "secret", $"'{Excerpt(encoded)}' looks like an encoded secret");
        }
        return violations.ToImmutable();
    }

    private static string FirstWord(string text)
    {
        var trimmed = text.TrimStart();
        var end = 0;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] is '\'' or '-'))
            end++;
        return trimmed[..end];
    }

    private static IEnumerable<(long From, long Through)> Citations(string body)
    {
        foreach (Match match in CitationPattern().Matches(body))
        {
            var from = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var through = match.Groups[2].Success ? long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : from;
            yield return (Math.Min(from, through), Math.Max(from, through));
        }
    }

    /// <summary>
    /// A long hex run, or a long base64 run that mixes digits and both cases and is not a path: the shape of a key or token.
    /// Checked in code because the patterns stay free of lookarounds (<see cref="RegexOptions.NonBacktracking"/>).
    /// </summary>
    private static string? EncodedSecret(string text)
    {
        foreach (Match match in LongHexPattern().Matches(text))
            return match.Value;
        foreach (Match match in LongBase64Pattern().Matches(text))
        {
            var value = match.Value;
            if (value.Count(c => c == '/') >= 2)
                continue;
            if (value.Any(char.IsAsciiDigit) && value.Any(char.IsAsciiLetterUpper) && value.Any(char.IsAsciiLetterLower))
                return value;
        }
        return null;
    }

    private static string Excerpt(string value) => value.Length > 24 ? value[..12] + "…" : value;

    [GeneratedRegex(@"^[a-z0-9-]{1,32}$", RegexOptions.NonBacktracking)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\bevents?\s+(\d{1,9})(?:\s*(?:-|–|to)\s*(\d{1,9}))?", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex CitationPattern();

    [GeneratedRegex(@"\bin this (session|conversation|chat)\b|\bthe (agent|assistant) (fixed|did|found|ran|tried|was|then)\b|\bsummary\s*:|\bwhat happened\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SummaryPattern();

    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\b.{0,30}\b(previous|prior|above|earlier|all|system|your)\b.{0,20}\b(instructions?|prompts?|rules|guidelines)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex InjectionPattern();

    [GeneratedRegex(@"/home/[A-Za-z0-9._-]+|/Users/[A-Za-z0-9._-]+|[A-Za-z]:\\Users\\", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex HomePathPattern();

    [GeneratedRegex(@"\b(25[0-5]|2[0-4]\d|1?\d?\d)\.(25[0-5]|2[0-4]\d|1?\d?\d)\.(25[0-5]|2[0-4]\d|1?\d?\d)\.(25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.NonBacktracking)]
    private static partial Regex IpPattern();

    [GeneratedRegex(@"\b(19|20)\d\d-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])\b", RegexOptions.NonBacktracking)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\b(today|yesterday|tomorrow|tonight|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UuidPattern();

    [GeneratedRegex(@"\b(pr|pull request|pull|issue|ticket|bug)\s*#?\s*\d+\b|(^|[\s(])#\d+\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex IssuePattern();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bsk-(ant-)?[A-Za-z0-9_-]{16,}|\bxox[abprs]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|\b(password|passwd|secret|token|api_key|apikey)\s*[:=]\s*[^\s`'""]{4,}", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.NonBacktracking)]
    private static partial Regex LongHexPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/_-]{40,}={0,2}", RegexOptions.NonBacktracking)]
    private static partial Regex LongBase64Pattern();
}
