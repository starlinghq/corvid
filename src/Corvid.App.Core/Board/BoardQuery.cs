using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Board;

/// <summary>
/// The Board's filter box (C-044, docs/plan/ui.md §4.13): what is typed narrows the cards at once by a fuzzy match over
/// what a card shows and is known by (its title, latest answer, id, workspace, branch and flock), with no engine involved;
/// from <see cref="MeaningFrom"/> characters, sessions the engine's session search found (by meaning, or by something said
/// in them) join them, tagged with how they were found.
/// </summary>
public static class BoardQuery
{
    /// <summary>The shortest filter the engine's session search is asked about; shorter ones match locally only.</summary>
    public const int MeaningFrom = 3;

    /// <summary>The most sessions the engine's search adds to what the filter matched locally.</summary>
    public const int MaxFound = 12;

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
    /// The tag a card the engine's search added carries: "by meaning" when its vectors found it, else "by words" (its
    /// card's words or something said in it, beyond what the local match reads).
    /// </summary>
    public static string FoundBy(SessionFind find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return find.ByMeaning ? "by meaning" : "by words";
    }
}
