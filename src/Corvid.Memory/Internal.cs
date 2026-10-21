using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Tokens;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

internal static class Ticks
{
    public static long From(DateTimeOffset value) => value.UtcTicks;

    public static DateTimeOffset To(long ticks) => new(ticks, TimeSpan.Zero);

    public static DateTimeOffset? ToNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : To(reader.GetInt64(ordinal));

    public static object Db(DateTimeOffset? value) => value is { } v ? From(v) : DBNull.Value;
}

/// <summary>
/// Turns free text into an FTS5 query for the trigram tokenizer: every term of three or more characters becomes a quoted
/// phrase, OR-ed so BM25 ranks rows by how many terms they share. Shorter terms (a two-character Japanese word, "Al")
/// cannot match a trigram index and are returned for a LIKE scan instead.
/// </summary>
internal static class FtsQuery
{
    public const int MinimumTermLength = 3;

    public static (string? Match, IReadOnlyList<string> Short) Build(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = Terms(query).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var longTerms = terms.Where(t => new System.Globalization.StringInfo(t).LengthInTextElements >= MinimumTermLength).ToList();
        var shortTerms = terms.Except(longTerms, StringComparer.Ordinal).ToList();
        var match = longTerms.Count == 0 ? null : string.Join(" OR ", longTerms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        return (match, shortTerms);
    }

    /// <summary>LIKE pattern for a short term, with the escape character <c>\</c>.</summary>
    public static string LikePattern(string term) =>
        "%" + term.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal) + "%";

    // Letters, digits and marks form terms; everything else (spaces, punctuation, symbols) separates them.
    private static IEnumerable<string> Terms(string query)
    {
        var current = new StringBuilder();
        foreach (var rune in query.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark)
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }
}

/// <summary>Cutting text to a token budget: recall's per-item cap, and the MCP memory tools' caps (C-028).</summary>
public static class TextBudget
{
    private const string Ellipsis = "…";

    /// <summary>The longest prefix within <paramref name="budget"/> tokens, ending in an ellipsis; the same instance when it fits.</summary>
    public static string Truncate(ITokenCounter counter, string text, int budget)
    {
        if (counter.CountTokens(text) <= budget)
            return text;
        if (budget <= 0 || counter.CountTokens(Ellipsis) > budget)
            return "";
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (counter.CountTokens(string.Concat(Prefix(text, mid), Ellipsis)) <= budget)
                low = mid;
            else
                high = mid - 1;
        }
        return string.Concat(Prefix(text, low), Ellipsis);
    }

    /// <summary>At most <paramref name="length"/> UTF-16 units, never splitting a surrogate pair.</summary>
    public static string Prefix(string text, int length)
    {
        if (length >= text.Length)
            return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            length--;
        return text[..length];
    }
}

internal static class EventAppend
{
    private const int MaxAttempts = 8;

    /// <summary>
    /// Appends one event at the session's head. <c>MemoryWritten</c> does not depend on the head, so a concurrent writer only
    /// means retrying at the new head (as the fabric does). It is not cancellable: the memory row it records is already written.
    /// </summary>
    public static void Append(IEventLog log, string sessionId, SessionEvent payload)
    {
        for (var attempt = 1; ; attempt++)
        {
            var head = log.GetHead(sessionId);
            try
            {
                log.Append(sessionId, head, [EventData.Create(payload)]);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAttempts)
            {
            }
        }
    }
}
