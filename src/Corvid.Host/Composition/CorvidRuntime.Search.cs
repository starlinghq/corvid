using Corvid.Abstractions.Events;
using Corvid.Gateway;
using Corvid.Host.Sessions;
using Corvid.Memory;

namespace Corvid.Host.Composition;

public sealed partial class CorvidRuntime
{
    // A6 review: what each searched session looked like at its head, so a search over an unchanged log reads none of it.
    private readonly SessionSearchCache _searchCache = new();

    /// <summary>
    /// G-5: searches what was said across sessions through the episodic index (<see cref="SessionSearch"/>), after the index
    /// has caught up with the log for every session whose head moved past it. Episodes the catch-up adds are embedded in the
    /// background, as after a turn, so retrieval finds them by meaning too. Synchronous (SQLite): a surface calls it off its
    /// UI thread.
    /// </summary>
    /// <param name="include">Narrows the sessions searched, within <see cref="IsSearchable"/>; null searches every one of those.</param>
    public SessionSearchResult SearchSessions(string query, int limit = 20, Func<string, bool>? include = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Memory is not { } memory)
            return SessionSearchResult.Off("memory is off (Corvid:Memory:Enabled), so there is no episodic index to search");
        var heads = Log.ListHeads(cancellationToken)
            .Where(pair => IsSearchable(pair.Key) && (include?.Invoke(pair.Key) ?? true))
            .ToDictionary(StringComparer.Ordinal);
        return SessionSearch.Search(Log, memory.Episodes, heads, query, limit, added => QueueEmbed(memory, added), _searchCache, cancellationToken);
    }

    /// <summary>What a search covers, in words a surface shows beside it.</summary>
    public const string SearchScope = "your own sessions; gateway chats (other people's messages) and scheduled job runs are left out";

    /// <summary>
    /// A session whose words a search covers: the person's own conversations. Left out (A6 review, one rule for
    /// <c>/search</c> and the app's palette, as for prompt history): the logs Corvid keeps for itself (MCP pins, the delivery
    /// ledger), gateway chats (other people's messages, which a search would quote back) and scheduled job runs (a job's
    /// prompt and its unattended run, not something said).
    /// </summary>
    public static bool IsSearchable(string sessionId) =>
        sessionId is not (McpSessions.Pins or GatewaySessions.Ledger or MemoryJobs.Session)
        && !sessionId.StartsWith(GatewaySessions.Prefix, StringComparison.Ordinal)
        && !sessionId.StartsWith("job:", StringComparison.Ordinal)
        // C-042: a flock's log holds its members' messages, each also in the member's own log.
        && !FlockSessions.IsFlock(sessionId);

    /// <summary>
    /// C-044 (G-5): finds sessions by what they are about (<see cref="SessionFinder"/>): their cards' words and, when memory
    /// has a retrieval embedder, their meaning, fused with the turns <see cref="SearchSessions"/> would find. The cards of
    /// sessions whose head moved are rewritten first and embedded in the background. Without an embedder the search is by
    /// words only (<see cref="SessionFindResult.Embedder"/> null); with memory off there is no index, and the result says why.
    /// The index work is synchronous SQLite and only the query's embedding awaits, so a surface calls it on the pool.
    /// </summary>
    /// <param name="include">Narrows the sessions searched, within <see cref="IsSearchable"/>; null searches every one of those.</param>
    public async Task<SessionFindResult> FindSessionsAsync(string query, int limit = 20, Func<string, bool>? include = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Memory is not { } memory)
            return SessionFindResult.Off("memory is off (Corvid:Memory:Enabled), so there is no session index to search");
        var heads = Log.ListHeads(cancellationToken)
            .Where(pair => IsSearchable(pair.Key) && (include?.Invoke(pair.Key) ?? true))
            .ToDictionary(StringComparer.Ordinal);
        var sources = new SessionFinderSources(Log, memory.Sessions ?? new SessionIndex(memory.Database), memory.Episodes, heads)
        {
            Vectors = memory.Vectors,
            BranchOf = id => WorkspaceOf(id).Branch,
            Embed = cards => QueueEmbed(memory, cards),
            EpisodesCaughtUp = added => QueueEmbed(memory, added),
            Cache = _searchCache,
            Time = SearchClock,
        };
        return await SessionFinder.FindAsync(sources, query, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The clock a session search's recency boost reads; a test sets its own.</summary>
    internal TimeProvider SearchClock { get; set; } = TimeProvider.System;

    private void QueueEmbed(MemoryServices memory, IReadOnlyList<Episode> added) =>
        QueueEmbed(memory, [.. added.Select(episode => (episode.ItemId, episode.Text))]);

    // On the maintenance chain, as QueueCatchUp's embedding is: best effort, and a failure never reaches the search.
    private void QueueEmbed(MemoryServices memory, IReadOnlyList<(string ItemId, string Text)> items)
    {
        lock (_maintenanceGate)
            _maintenance = _maintenance.ContinueWith(async _ =>
            {
                try
                {
                    await memory.Vectors.EmbedAsync(items).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
}
