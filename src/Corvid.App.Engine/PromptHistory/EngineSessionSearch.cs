using Corvid.App.Core.Palette;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;

namespace Corvid.App.Engine.PromptHistory;

/// <summary>
/// G-5 on the engine: <see cref="CorvidRuntime.SearchSessions"/> (the episodic index, caught up with the log first), for the
/// palette's <c>Ctrl+Shift+F</c>, and <see cref="CorvidRuntime.FindSessionsAsync"/> (C-044, the session index) for its
/// Sessions group and the Board's filter box. The sessions searched are the ones <see cref="CorvidRuntime.IsSearchable"/> takes (the
/// person's own: not Corvid's stores, gateway chats or job runs, the same rule as <c>/search</c>) but the memory import's
/// staging session; a match in a session the rail does not list says so when it is chosen.
/// <para>The index is SQLite, read synchronously, so the search runs on the pool, never on the UI thread.</para>
/// </summary>
public sealed class EngineSessionSearch(AppEngine engine) : ISessionSearch
{
    /// <summary>The most matches a search returns.</summary>
    public const int MaxMatches = 30;

    private readonly AppEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task<SessionMatches> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var found = await Task.Run(() => runtime.SearchSessions(query, MaxMatches, Searched, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (found.Unavailable is { } why)
            return new SessionMatches([], why);
        return new SessionMatches([.. found.Hits.Select(hit =>
            new SessionMatch(hit.SessionId, hit.Title, hit.TurnSequence, hit.Turn, hit.Who, hit.At, hit.Snippet))]);
    }

    /// <summary>
    /// C-044: <see cref="CorvidRuntime.FindSessionsAsync"/>, sessions by what they are about (the session index, by words
    /// and, with an embedder, by meaning, fused with the turns that matched), over the same sessions as
    /// <see cref="SearchAsync"/>. Its index work is synchronous SQLite, so it runs on the pool; with memory off the result
    /// says why, and the palette and the Board match the session list instead.
    /// </summary>
    public async Task<SessionFinds?> FindSessionsAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var found = await Task.Run(() => runtime.FindSessionsAsync(query, MaxMatches, Searched, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (found.Unavailable is { } why)
            return new SessionFinds([], null, why);
        return new SessionFinds([.. found.Found.Select(item =>
            new SessionFind(item.SessionId, item.Title, item.Line, item.Score, item.ByMeaning, item.Turn?.TurnSequence))], found.Embedder);
    }

    /// <summary>The sessions a search covers: every one the runtime would search, but the memory import's staging session.</summary>
    internal static bool Searched(string id) =>
        CorvidRuntime.IsSearchable(id) && id != Memory.EngineMemoryService.ImportSession;
}
