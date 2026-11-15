namespace Corvid.App.Core.Palette;

/// <summary>
/// One place a search across sessions found (G-5): the session, the turn the matched text falls in and where that turn's
/// prompt is in the log (so the session opens there), who wrote the text, and a snippet of it. The snippet and the title are
/// untrusted (a person's, a model's, a tool's words): the palette cleans them before showing them.
/// </summary>
/// <param name="TurnPosition">The log position of the matched turn's prompt; 0 when the match is before the first turn.</param>
/// <param name="Who">Who wrote it, in a word: "you", "Corvid", "a tool".</param>
public sealed record SessionMatch(string SessionId, string? Title, long TurnPosition, int Turn, string Who, DateTimeOffset At, string Snippet);

/// <summary>What a search found, best first, or why it could not search (<see cref="Unavailable"/>).</summary>
public sealed record SessionMatches(IReadOnlyList<SessionMatch> Matches, string? Unavailable = null);

/// <summary>
/// C-044: a session a search found by what it is about: its title and a line (the latest answer, or the words that matched
/// when only something said in it did), and whether the embedder's vectors found it ("by meaning"). Title and line are
/// untrusted text: a surface cleans them before showing them.
/// </summary>
/// <param name="TurnPosition">When a turn of the session itself matched, the log position of its prompt (where the session
/// can open); else null, as when only a child's turn matched.</param>
/// <param name="ByWords">Words found it too (its card's or something said in it), not only the embedder's vectors.</param>
public sealed record SessionFind(string SessionId, string? Title, string? Line, double Score, bool ByMeaning, long? TurnPosition = null,
    bool ByWords = false);

/// <summary>What a session search found, best first; by words only when <see cref="Embedder"/> is null; or why it could not search.</summary>
/// <param name="Embedder">The embedder whose vectors were searched, or null when the search was by words (no embedder bound).</param>
/// <param name="Unavailable">Why there is no index (memory is off); null when the search ran.</param>
public sealed record SessionFinds(IReadOnlyList<SessionFind> Finds, string? Embedder = null, string? Unavailable = null)
{
    /// <summary>"by meaning" when the vectors were searched, "by words" otherwise.</summary>
    public string How => Embedder is null ? "by words" : "by meaning";
}

/// <summary>
/// G-5: search over what was said across sessions, through the engine's episodic index (docs/plan/ui.md §4.6, F20). The
/// palette's <c>Ctrl+Shift+F</c> asks it as the person types; a match opens its session at the matched turn. Where there is
/// no index (the demo) the palette searches titles, previews and ids instead.
/// </summary>
public interface ISessionSearch
{
    Task<SessionMatches> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// C-044: sessions by what they are about (their titles, first messages, latest answers and branches, by words and, with
    /// an embedder, by meaning), for the palette's Sessions group and the Board's filter. Null where there is no session
    /// index to ask (the demo), so a surface matches the session list itself; a result with
    /// <see cref="SessionFinds.Unavailable"/> set where the engine has one but memory is off.
    /// </summary>
    Task<SessionFinds?> FindSessionsAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult<SessionFinds?>(null);
}
