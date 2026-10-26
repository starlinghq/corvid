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
        sessionId is not (McpSessions.Pins or GatewaySessions.Ledger)
        && !sessionId.StartsWith(GatewaySessions.Prefix, StringComparison.Ordinal)
        && !sessionId.StartsWith("job:", StringComparison.Ordinal);

    // On the maintenance chain, as QueueCatchUp's embedding is: best effort, and a failure never reaches the search.
    private void QueueEmbed(MemoryServices memory, IReadOnlyList<Episode> added)
    {
        lock (_maintenanceGate)
            _maintenance = _maintenance.ContinueWith(async _ =>
            {
                try
                {
                    await memory.Vectors.EmbedAsync([.. added.Select(episode => (episode.ItemId, episode.Text))]).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
}
