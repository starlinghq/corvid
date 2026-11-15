using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Memory;

namespace Corvid.Host.Sessions;

/// <summary>Which of a session search's lists found a session.</summary>
[Flags]
public enum SessionFoundSources
{
    None = 0,

    /// <summary>The words of its card (title, first message, latest answer, branch), by BM25.</summary>
    Text = 1,

    /// <summary>Its card's meaning, by the embedder's vectors.</summary>
    Meaning = 2,

    /// <summary>Something said in one of its turns (or a child's), through the episodic index.</summary>
    Said = 4,
}

/// <summary>
/// A session a search found (C-044): the session, its title and a line (its latest answer, or the matched words when only
/// what was said found it), the fused score, the lists that found it and, when a turn matched, the best one.
/// </summary>
/// <param name="Title">The session's title (its rename, generated title or first message), or null. Untrusted text.</param>
/// <param name="Line">One line to show under the title, or null. Untrusted text.</param>
/// <param name="Turn">The best matched turn in it (or a child of it), when what was said matched.</param>
public sealed record SessionFound(string SessionId, string? Title, string? Line, double Score, SessionFoundSources Sources,
    SessionSearchHit? Turn = null)
{
    /// <summary>The embedder's vectors found it: a surface says "by meaning" beside it.</summary>
    public bool ByMeaning => Sources.HasFlag(SessionFoundSources.Meaning);
}

/// <summary>What a session search found, best first, or why it could not search.</summary>
/// <param name="Embedder">The embedder whose vectors were searched, or null when the search was by words only.</param>
/// <param name="Unavailable">Why there is no index (memory is off); null when the search ran.</param>
public sealed record SessionFindResult(IReadOnlyList<SessionFound> Found, string? Embedder, string? Unavailable = null)
{
    public static SessionFindResult Off(string why) => new([], null, why);
}

/// <summary>What <see cref="SessionFinder.FindAsync"/> searches and how it reports what it caught up.</summary>
/// <param name="Heads">The sessions to search and each one's head (the log's <c>ListHeads</c>, filtered by the caller).</param>
public sealed record SessionFinderSources(
    IEventLog Log,
    SessionIndex Index,
    EpisodicIndex Episodes,
    IReadOnlyDictionary<string, long> Heads)
{
    /// <summary>The memory items' vectors; null, or one without a retrieval embedder, searches by words only.</summary>
    public MemoryVectors? Vectors { get; init; }

    /// <summary>A session's worktree branch, or null; it goes on the session's card.</summary>
    public Func<string, string?> BranchOf { get; init; } = _ => null;

    /// <summary>Called with the cards whose text changed, to embed them in the background; may be null.</summary>
    public Action<IReadOnlyList<(string ItemId, string Text)>>? Embed { get; init; }

    /// <summary>Called with the episodes the turn search's catch-up added, as <see cref="SessionSearch"/> does.</summary>
    public Action<IReadOnlyList<Episode>>? EpisodesCaughtUp { get; init; }

    /// <summary>What earlier turn searches learned about each session at its head; null learns it afresh.</summary>
    public SessionSearchCache? Cache { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// The least cosine similarity a card's vector needs to count as found by meaning. Nearest-neighbour search always
    /// returns the nearest cards, related or not; this floor, with <see cref="SessionFinder.MeaningCandidates"/>, keeps the
    /// unrelated ones out. A heuristic: embedders differ in how similar unrelated text looks.
    /// </summary>
    public double MinSimilarity { get; init; } = SessionFinder.DefaultMinSimilarity;
}

/// <summary>
/// C-044 (G-5): search for sessions by what they are about, so "the sandbox flake one" returns sessions, not turns. Three
/// ranked lists are merged by reciprocal rank fusion (<see cref="RankFusion"/>, k = 60):
/// <list type="number">
/// <item>the session cards' full text (<see cref="SessionIndex"/>, BM25; terms under three characters by LIKE);</item>
/// <item>the cards' vectors, when memory has a retrieval embedder (the gate <see cref="HybridRetriever"/> uses);</item>
/// <item>the turns that matched (<see cref="SessionSearch"/>, which keeps its undo and rollback rules), grouped by session
/// at each one's best rank, a child's turns counting for its parent.</item>
/// </list>
/// Then a mild boost for recent activity (half-life <see cref="RecencyHalfLife"/>). Without an embedder the second list is
/// empty and the result says so (<see cref="SessionFindResult.Embedder"/> null: "by words").
/// <para>
/// The cards are refreshed lazily, here, as <see cref="SessionSearch"/> catches up the episodic index: one look at the
/// index's marks, and every searched session whose head moved is described (<see cref="SessionCatalogue.Describe"/>, cached
/// by head) and its card rewritten; cards whose text changed go to <see cref="SessionFinderSources.Embed"/>. A child
/// session gets no card of its own: it is a row under its parent. Synchronous SQLite but for the query's embedding, so a
/// surface runs it on the pool; cancellation is checked between sessions.
/// </para>
/// </summary>
public static class SessionFinder
{
    /// <summary>The most sessions a search returns.</summary>
    public const int MaxFound = 50;

    /// <summary>How fast the recency boost fades: a session idle this long gets half of it.</summary>
    public static readonly TimeSpan RecencyHalfLife = TimeSpan.FromDays(14);

    /// <summary>The fused score is multiplied by 1 + weight · 0.5^(age / half-life).</summary>
    public const double RecencyWeight = 0.5;

    /// <summary>How many of the nearest cards the meaning list takes, at most.</summary>
    public const int MeaningCandidates = 8;

    /// <summary>The default for <see cref="SessionFinderSources.MinSimilarity"/>.</summary>
    public const double DefaultMinSimilarity = 0.3;

    /// <summary>
    /// Rewrites the card of every searched session whose head moved past the index's mark for it, and returns the cards
    /// whose text changed (to embed).
    /// </summary>
    public static IReadOnlyList<(string ItemId, string Text)> CatchUp(SessionFinderSources sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var marks = sources.Index.Marks();
        var changed = new List<(string, string)>();
        foreach (var (id, head) in sources.Heads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsChild(id) || marks.GetValueOrDefault(id) == head)
                continue;
            if (SessionCatalogue.Describe(sources.Log, id, cancellationToken) is not { } described)
                continue;
            var text = SessionIndexText.Of(described, sources.BranchOf(id));
            if (text.Length > 0 && sources.Index.Upsert(id, described.Head, text))
                changed.Add((SessionIndex.ItemIdOf(id), text));
        }
        return changed;
    }

    public static async Task<SessionFindResult> FindAsync(SessionFinderSources sources, string query, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(query);
        limit = Math.Clamp(limit, 1, MaxFound);
        var embedder = sources.Vectors?.RetrievalEmbedder?.Id;
        if (query.Trim().Length == 0 || sources.Heads.Count == 0)
            return new([], embedder);

        var changed = CatchUp(sources, cancellationToken);
        if (changed.Count > 0)
            sources.Embed?.Invoke(changed);

        var n = Math.Min(limit * 3, 150);
        var found = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        Candidate Of(string id) => found.TryGetValue(id, out var known) ? known : found[id] = new Candidate(id);
        void Fuse(IReadOnlyList<string> ranked, SessionFoundSources source)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                var candidate = Of(ranked[i]);
                candidate.Fused += RankFusion.Score(i);
                candidate.Sources |= source;
            }
        }

        // 1. The cards' words.
        Fuse([.. sources.Index.Search(query, n).Select(hit => hit.Card.SessionId).Where(sources.Heads.ContainsKey)], SessionFoundSources.Text);

        // 2. The cards' meaning. A failed embedder leaves the words; the result then says it searched by words only.
        if (sources.Vectors is { } vectors && embedder is not null)
        {
            try
            {
                var hits = await vectors.SearchAsync(query, MeaningCandidates, SessionIndex.IsSessionItem, cancellationToken).ConfigureAwait(false);
                Fuse([.. hits.Where(hit => hit.Similarity >= sources.MinSimilarity).Select(hit => hit.ItemId[SessionIndex.ItemPrefix.Length..]).Where(sources.Heads.ContainsKey)], SessionFoundSources.Meaning);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ArgumentException)
            {
                embedder = null;
            }
        }

        // 3. What was said, a session (or its parent, for a child) at its best turn's rank.
        var said = SessionSearch.Search(sources.Log, sources.Episodes, sources.Heads, query, SessionSearch.MaxHits, sources.EpisodesCaughtUp,
            sources.Cache, cancellationToken);
        var bySession = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in said.Hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = IsChild(hit.SessionId) ? ParentOf(sources.Log, hit.SessionId) : hit.SessionId;
            if (owner is null || !sources.Heads.ContainsKey(owner) || !seen.Add(owner))
                continue;
            bySession.Add(owner);
            Of(owner).Turn = hit;
        }
        Fuse(bySession, SessionFoundSources.Said);

        var now = sources.Time.GetUtcNow();
        var results = new List<SessionFound>(Math.Min(limit, found.Count));
        foreach (var candidate in found.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var described = SessionCatalogue.Describe(sources.Log, candidate.SessionId, cancellationToken);
            var recency = described is null ? 0 : Recency(described.LastActivity, now);
            var line = candidate.Sources == SessionFoundSources.Said ? candidate.Turn?.Snippet : described?.Preview ?? candidate.Turn?.Snippet;
            results.Add(new SessionFound(candidate.SessionId, described?.Title, line, candidate.Fused * (1 + RecencyWeight * recency),
                candidate.Sources, candidate.Turn));
        }
        return new([.. results.OrderByDescending(item => item.Score).ThenBy(item => item.SessionId, StringComparer.Ordinal).Take(limit)], embedder);
    }

    /// <summary>A delegated child session (A5): never a card of its own, and its turns count for its parent.</summary>
    internal static bool IsChild(string sessionId) => sessionId.StartsWith(ChildSessions.Surface + "-", StringComparison.Ordinal);

    private static string? ParentOf(IEventLog log, string childId) =>
        log.Read(childId, 0, limit: 1) is [{ Payload: ChildOf origin }] ? origin.ParentId : null;

    private static double Recency(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        return age <= TimeSpan.Zero ? 1 : Math.Pow(0.5, age / RecencyHalfLife);
    }

    private sealed class Candidate(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public double Fused { get; set; }
        public SessionFoundSources Sources { get; set; }
        public SessionSearchHit? Turn { get; set; }
    }
}
