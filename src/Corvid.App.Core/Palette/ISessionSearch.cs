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
/// G-5: search over what was said across sessions, through the engine's episodic index (docs/plan/ui.md §4.6, F20). The
/// palette's <c>Ctrl+Shift+F</c> asks it as the person types; a match opens its session at the matched turn. Where there is
/// no index (the demo) the palette searches titles, previews and ids instead.
/// </summary>
public interface ISessionSearch
{
    Task<SessionMatches> SearchAsync(string query, CancellationToken cancellationToken = default);
}
