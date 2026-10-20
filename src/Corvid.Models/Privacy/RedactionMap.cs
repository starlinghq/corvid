using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Corvid.Models.Privacy;

/// <summary>
/// One session's placeholders (plan §10.4): each PII value gets a stable placeholder such as <c>&lt;PERSON_1&gt;</c>, so the
/// same person is the same placeholder in every request of the session (which also keeps a cloud prompt cache's prefix
/// stable), and placeholders in a response map back to the values. The map lives in memory only; it never leaves Corvid.
/// </summary>
public sealed partial class RedactionMap
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _byValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    internal long LastUsed { get; set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _byValue.Count;
        }
    }

    /// <summary>The value's placeholder, minting <c>&lt;LABEL_n&gt;</c> the first time the value is seen.</summary>
    public string PlaceholderFor(string label, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrEmpty(value);
        lock (_gate)
        {
            if (_byValue.TryGetValue(value, out var existing))
                return existing;
            var tag = Tag(label);
            var n = _counters[tag] = _counters.GetValueOrDefault(tag) + 1;
            var placeholder = string.Create(CultureInfo.InvariantCulture, $"<{tag}_{n}>");
            _byValue[value] = placeholder;
            _byPlaceholder[placeholder] = value;
            return placeholder;
        }
    }

    public bool TryResolve(string placeholder, out string value)
    {
        lock (_gate)
            return _byPlaceholder.TryGetValue(placeholder, out value!);
    }

    /// <summary>Replaces every known value in <paramref name="text"/>, longest first, without touching a placeholder it inserted.</summary>
    /// <remarks>
    /// Placeholder-shaped text that was already there (a page quoting <c>&lt;EMAIL_1&gt;</c>) is neutralized first, so outside
    /// content cannot name a placeholder and have the response restore a real value in its place.
    /// </remarks>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = Neutralize(text);
        KeyValuePair<string, string>[] values;
        lock (_gate)
            values = [.. _byValue];
        if (values.Length == 0 || text.Length == 0)
            return text;

        // One pass over the matches: the earliest start wins, then the longest value, so a shorter value inside a longer one
        // (a first name inside a full name) never splits it.
        var matches = new List<(int Start, int Length, string Placeholder)>();
        foreach (var (value, placeholder) in values)
            for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
                matches.Add((at, value.Length, placeholder));
        if (matches.Count == 0)
            return text;
        matches.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var (start, length, placeholder) in matches)
        {
            if (start < position)
                continue;
            builder.Append(text, position, start - position).Append(placeholder);
            position = start + length;
        }
        return builder.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>Maps this session's placeholders in <paramref name="text"/> back to their values; unknown ones stay as written.</summary>
    public string Restore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf('<', StringComparison.Ordinal) < 0)
            return text;
        return PlaceholderPattern().Replace(text, match => TryResolve(match.Value, out var value) ? value : match.Value);
    }

    /// <summary><c>account number</c> becomes <c>ACCOUNT_NUMBER</c>.</summary>
    internal static string Tag(string label)
    {
        var builder = new StringBuilder(label.Length);
        foreach (var c in label.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToUpperInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }
        var tag = builder.ToString().TrimEnd('_');
        return tag.Length == 0 || !char.IsAsciiLetter(tag[0]) ? "PII" + (tag.Length == 0 ? "" : "_" + tag) : tag;
    }

    internal static string Neutralize(string text) =>
        text.IndexOf('<', StringComparison.Ordinal) < 0 ? text : PlaceholderPattern().Replace(text, match => "‹" + match.Value[1..^1] + "›");

    [GeneratedRegex(@"<[A-Z][A-Z0-9_]*_[0-9]+>", RegexOptions.CultureInvariant)]
    internal static partial Regex PlaceholderPattern();
}
