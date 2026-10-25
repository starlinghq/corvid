using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>One indexed session event. <see cref="ItemId"/> is <c>event:</c> and the event ID.</summary>
public sealed record Episode(string ItemId, string SessionId, long Sequence, string Kind, DateTimeOffset RecordedAt, string Text);

/// <param name="Rank">Lower is better: the FTS5 BM25 score, or 0 for a LIKE match on short terms.</param>
public sealed record EpisodeHit(Episode Episode, double Rank);

/// <summary>
/// Episodic memory (plan §9.1): the text of every session event in an FTS5 table. The event log stays the source of truth;
/// this is a derived index that catches up from <see cref="IEventLog"/> and can be rebuilt from it. Compaction does not
/// remove events, so compacted ranges stay searchable (plan §8).
/// </summary>
public sealed class EpisodicIndex
{
    private const int PageSize = 256;

    private readonly MemoryDatabase _database;

    public EpisodicIndex(MemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public static string ItemIdOf(Guid eventId) => "event:" + eventId.ToString("D");

    /// <summary>How many episodes are indexed, across every session.</summary>
    public int Count()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM episodes;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The last sequence indexed for a session, 0 when none.</summary>
    public long IndexedThrough(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var connection = _database.Open();
        return Through(connection, null, sessionId);
    }

    /// <summary>
    /// Indexes the session's events after the high-water mark, a page per transaction, and returns the episodes added. Events
    /// with no searchable text (decisions, model switches) advance the mark without a row.
    /// </summary>
    public IReadOnlyList<Episode> CatchUp(IEventLog log, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var added = new List<Episode>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = IndexedThrough(sessionId);
            var page = log.Read(sessionId, after, limit: PageSize, cancellationToken: cancellationToken);
            if (page.Count == 0)
                return added;
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            // Another indexer may have taken this page; the mark is re-read under the write lock.
            if (Through(connection, transaction, sessionId) != after)
                continue;
            foreach (var stored in page)
            {
                if (EpisodeText.Of(stored.Payload) is not { } entry)
                    continue;
                var episode = new Episode(ItemIdOf(stored.EventId), sessionId, stored.Sequence, entry.Kind, stored.RecordedAt, entry.Text);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO episodes (item_id, session_id, sequence, kind, recorded_at, text)
                    VALUES ($item, $session, $sequence, $kind, $at, $text);
                    """;
                insert.Parameters.AddWithValue("$item", episode.ItemId);
                insert.Parameters.AddWithValue("$session", sessionId);
                insert.Parameters.AddWithValue("$sequence", episode.Sequence);
                insert.Parameters.AddWithValue("$kind", episode.Kind);
                insert.Parameters.AddWithValue("$at", Ticks.From(episode.RecordedAt));
                insert.Parameters.AddWithValue("$text", episode.Text);
                insert.ExecuteNonQuery();
                added.Add(episode);
            }
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = """
                INSERT INTO episode_index_state (session_id, through_sequence) VALUES ($session, $through)
                ON CONFLICT (session_id) DO UPDATE SET through_sequence=excluded.through_sequence;
                """;
            mark.Parameters.AddWithValue("$session", sessionId);
            mark.Parameters.AddWithValue("$through", page[^1].Sequence);
            mark.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>
    /// Full-text search ranked by BM25. Terms under three characters cannot use the trigram index; when a query has only
    /// such terms, rows containing any of them match by LIKE, newest first.
    /// </summary>
    /// <param name="sessionId">Limits the search to one session; null searches every session.</param>
    public IReadOnlyList<EpisodeHit> Search(string query, int limit = 20, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var (match, shortTerms) = FtsQuery.Build(query);
        if (match is null && shortTerms.Count == 0)
            return [];
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        const string columns = "e.item_id, e.session_id, e.sequence, e.kind, e.recorded_at, e.text";
        if (match is not null)
        {
            command.CommandText = $"""
                SELECT {columns}, bm25(episodes_fts) AS rank FROM episodes_fts JOIN episodes e ON e.id = episodes_fts.rowid
                WHERE episodes_fts MATCH $match AND ($session IS NULL OR e.session_id = $session)
                ORDER BY rank, e.recorded_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$match", match);
        }
        else
        {
            var likes = string.Join(" OR ", shortTerms.Select((_, i) => $"e.text LIKE $t{i} ESCAPE '\\'"));
            command.CommandText = $"""
                SELECT {columns}, 0.0 AS rank FROM episodes e
                WHERE ({likes}) AND ($session IS NULL OR e.session_id = $session)
                ORDER BY e.recorded_at DESC, e.id DESC LIMIT $limit;
                """;
            for (var i = 0; i < shortTerms.Count; i++)
                command.Parameters.AddWithValue($"$t{i}", FtsQuery.LikePattern(shortTerms[i]));
        }
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var hits = new List<EpisodeHit>();
        while (reader.Read())
            hits.Add(new EpisodeHit(ReadEpisode(reader), reader.GetDouble(6)));
        return hits;
    }

    public IReadOnlyList<Episode> Get(IEnumerable<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, session_id, sequence, kind, recorded_at, text FROM episodes WHERE item_id=$item;";
        var item = command.Parameters.Add("$item", SqliteType.Text);
        var result = new List<Episode>();
        foreach (var id in itemIds)
        {
            item.Value = id;
            using var reader = command.ExecuteReader();
            if (reader.Read())
                result.Add(ReadEpisode(reader));
        }
        return result;
    }

    internal static Episode ReadEpisode(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), Ticks.To(reader.GetInt64(4)),
            reader.GetString(5));

    private static long Through(SqliteConnection connection, SqliteTransaction? transaction, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT through_sequence FROM episode_index_state WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        return command.ExecuteScalar() is long through ? through : 0;
    }
}

/// <summary>The searchable text of an event, by its persisted discriminator. Fabric bookkeeping is not episodic memory.</summary>
internal static class EpisodeText
{
    public static (string Kind, string Text)? Of(SessionEvent payload) => payload switch
    {
        // A4: a `!!` line's message, call and output are the user's alone; recall would hand them to the model later.
        UserMessage { LocalOnly: true } or ToolCallIssued { LocalOnly: true } or ToolResult { LocalOnly: true } => null,
        UserMessage message => Entry("user_message", message.Text),
        AssistantMessage message => Entry("assistant_message", message.Text),
        ToolCallIssued call => Entry("tool_call_issued", $"{call.ToolName} {Json(call.Arguments)}"),
        ToolResult result => Entry("tool_result", Json(result.Result)),
        ApprovalRequested approval => Entry("approval_requested", $"{approval.Action} {approval.Reason}"),
        Compacted compacted => Entry("compacted", Summary(compacted.Summary)),
        GoalSet goal => goal.Goal is null ? null : Entry("goal_set", goal.Goal),
        TodosSet todos => Entry("todos_set", string.Join("\n", todos.Items.Select(item => item.Text))),
        _ => null,
    };

    private static (string, string)? Entry(string kind, string text) =>
        string.IsNullOrWhiteSpace(text) ? null : (kind, text);

    // Values, not raw JSON: the log's serializer escapes non-ASCII text, which would hide it from search.
    private static string Json(JsonElement element)
    {
        var builder = new StringBuilder();
        Flatten(element, builder);
        return builder.ToString().TrimEnd();
    }

    private static void Flatten(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    builder.Append(property.Name).Append(": ");
                    Flatten(property.Value, builder);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Flatten(item, builder);
                break;
            case JsonValueKind.String:
                builder.Append(element.GetString()).Append('\n');
                break;
            case JsonValueKind.Undefined:
                break;
            default:
                builder.Append(element.GetRawText()).Append('\n');
                break;
        }
    }

    private static string Summary(CompactionSummary summary)
    {
        var builder = new StringBuilder();
        void Line(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                builder.AppendLine(value);
        }
        Line(summary.Goal);
        foreach (var list in new[] { summary.Constraints, summary.Decisions, summary.Files, summary.OpenQuestions })
            foreach (var item in list.IsDefault ? [] : list)
                Line(item);
        Line(summary.NextStep);
        return builder.ToString().TrimEnd();
    }
}
