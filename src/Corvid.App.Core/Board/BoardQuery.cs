using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Board;

/// <summary>
/// The Board's filter box (C-044, docs/plan/ui.md §4.13): what is typed narrows the cards at once by a fuzzy match over
/// what a card shows and is known by (its title, latest answer, id, workspace, branch and flock), with no engine involved;
/// from <see cref="MeaningFrom"/> characters, sessions the engine's session search found (by meaning, or by something said
/// in them) join them, tagged with how they were found. One the embedder's vectors alone found joins only from
/// <see cref="MeaningWords"/> words: nearest-neighbour search finds something for any filter, and one word (an id fragment,
/// a workspace name) is a lookup, not a description.
/// </summary>
public static class BoardQuery
{
    /// <summary>The shortest filter the engine's session search is asked about; shorter ones match locally only.</summary>
    public const int MeaningFrom = 3;

    /// <summary>The most sessions the engine's search adds to what the filter matched locally.</summary>
    public const int MaxFound = 12;

    /// <summary>The fewest words a filter needs before a session only its meaning found joins the cards.</summary>
    public const int MeaningWords = 2;

    /// <summary>Whether the filter matches the session locally; an empty filter matches every session.</summary>
    public static bool Matches(SessionSummary summary, string filter)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return FuzzyMatch.Best(filter, SafeText.Clean(summary.Title), SafeText.Clean(summary.Preview ?? ""), summary.Id,
            SafeText.Clean(summary.Workspace), SafeText.Clean(summary.Branch), SafeText.Clean(summary.Flock ?? "")) is not null;
    }

    /// <summary>Whether the engine's session search should be asked about this filter.</summary>
    public static bool AsksEngine(string filter) => filter.Trim().Length >= MeaningFrom;

    /// <summary>
    /// Whether a session the engine's search found joins the cards for this filter: always when words found it, and when
    /// only its meaning did, from <see cref="MeaningWords"/> words.
    /// </summary>
    public static bool Joins(SessionFind find, string filter)
    {
        ArgumentNullException.ThrowIfNull(find);
        return !find.ByMeaning || find.ByWords
            || filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= MeaningWords;
    }

    /// <summary>
    /// The tag a card the engine's search added carries: "by meaning" when its vectors found it, else "by words" (its
    /// card's words or something said in it, beyond what the local match reads).
    /// </summary>
    public static string FoundBy(SessionFind find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return find.ByMeaning ? "by meaning" : "by words";
    }
}
