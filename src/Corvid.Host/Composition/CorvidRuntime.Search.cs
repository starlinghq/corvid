using Corvid.Abstractions.Events;
using Corvid.Gateway;
using Corvid.Host.Sessions;
using Corvid.Memory;

namespace Corvid.Host.Composition;

public sealed partial class CorvidRuntime
{
    /// <summary>
    /// G-5: searches what was said across sessions through the episodic index (<see cref="SessionSearch"/>), after each
    /// session's index has caught up with the log. Episodes the catch-up adds are embedded in the background, as after a
    /// turn, so retrieval finds them by meaning too. Synchronous (SQLite): a surface calls it off its UI thread.
    /// </summary>
    /// <param name="sessions">The sessions to search; null for every session in the log but Corvid's own stores.</param>
    public SessionSearchResult SearchSessions(string query, int limit = 20, IEnumerable<string>? sessions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Memory is not { } memory)
            return SessionSearchResult.Off("memory is off (Corvid:Memory:Enabled), so there is no episodic index to search");
        var ids = sessions ?? Log.ListSessions(cancellationToken).Where(IsSearchable);
        return SessionSearch.Search(Log, memory.Episodes, ids, query, limit, added => QueueEmbed(memory, added), cancellationToken);
    }

    /// <summary>A session whose words a search covers: every one but the logs Corvid keeps for itself (MCP pins, the delivery ledger).</summary>
    public static bool IsSearchable(string sessionId) =>
        sessionId is not (McpSessions.Pins or GatewaySessions.Ledger);

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
