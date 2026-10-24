namespace Corvid.App.Core.Palette;

/// <summary>
/// The palette's matcher: every character of the query must appear in the text in order, ignoring case. Matches score
/// higher when they start the text or a word, and when matched characters run together; letters strewn across a long
/// text are no match (the span stays within three times the query and six). So "gs" finds "Go to Settings"
/// above "Toggle the dock's status" and "set" ranks "Settings" above "Reset". An empty query matches everything at 0.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>The score of a match, or null when the query's characters are not all in the text in order.</summary>
    public static int? Score(string query, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var needle = (query ?? "").Trim();
        if (needle.Length == 0)
            return 0;

        // A plain substring is the strongest match; at a word's start stronger still.
        var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
            return 1000 - at + (at == 0 || !char.IsLetterOrDigit(text[at - 1]) ? 200 : 0) + needle.Length * 4;

        var score = 0;
        var run = 0;
        var position = 0;
        var first = -1;
        foreach (var c in needle)
        {
            if (char.IsWhiteSpace(c))
            {
                run = 0;
                continue;
            }

            var found = -1;
            for (var i = position; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(c))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
                return null;
            if (first < 0)
                first = found;
            // Letters strewn across a long sentence are not a match: the matched span stays near the query's length.
            if (found - first + 1 > needle.Length * 3 + 6)
                return null;
            run = found == position && position > 0 ? run + 1 : 0;
            var wordStart = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            score += 10 + run * 8 + (wordStart ? 15 : 0) - Math.Min(found - position, 10);
            position = found + 1;
        }

        return score;
    }

    /// <summary>The best of several fields' scores (a title and its detail), the first field weighted double.</summary>
    public static int? Best(string query, string first, params string[] rest)
    {
        var best = Score(query, first) is { } head ? head * 2 : (int?)null;
        foreach (var field in rest)
        {
            if (Score(query, field) is { } score && (best is null || score > best))
                best = score;
        }

        return best;
    }
}
