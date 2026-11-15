using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>One session's card in the session index: the text it is found by, and the log head it was built from.</summary>
/// <param name="ItemId"><c>session:</c> and the session's id: the card's vectors are kept under it.</param>
public sealed record SessionCard(string ItemId, string SessionId, long Head, string Text, DateTimeOffset UpdatedAt);

/// <param name="Rank">Lower is better: the FTS5 BM25 score, or 0 for a LIKE match on short terms.</param>
public sealed record SessionCardHit(SessionCard Card, double Rank);

/// <summary>
/// The session index (C-044, G-5): one card of text per session (its title, first message, latest answer and branch,
/// built by the host) in an FTS5 table, with the card's vector beside the other memory items' under <c>session:</c> ids.
/// A search finds sessions by what they are about rather than turns by what was said in them. Like the episodic index it
/// is derived from the event log and can be rebuilt from it; unlike episodes a card is replaced each time its session's
/// head moves, and the caller (the host's session finder) decides when that is.
/// </summary>
public sealed class SessionIndex
{
    /// <summary>The prefix of a card's item id, which keeps its vectors apart from episodes' and facts'.</summary>
    public const string ItemPrefix = "session:";

    private readonly MemoryDatabase _database;
    private readonly TimeProvider _time;

    public SessionIndex(MemoryDatabase database, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public static string ItemIdOf(string sessionId) => ItemPrefix + sessionId;

    /// <summary>Whether a memory item id names a session card; retrieval for prompts leaves those out.</summary>
    public static bool IsSessionItem(string itemId) => itemId.StartsWith(ItemPrefix, StringComparison.Ordinal);

    /// <summary>How many sessions have a card.</summary>
    public int Count()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sessions;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The head each session's card was built from, in one query; a session without a card is absent.</summary>
    public IReadOnlyDictionary<string, long> Marks()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, head FROM sessions;";
        using var reader = command.ExecuteReader();
        var marks = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            marks[reader.GetString(0)] = reader.GetInt64(1);
        return marks;
    }

    /// <summary>
    /// Writes a session's card as of <paramref name="head"/>. A card already built from that head or a later one is kept (two
    /// searches catching up at once); otherwise the text and head are replaced.
    /// </summary>
    /// <returns>True when the text changed, so its vector needs embedding again.</returns>
    public bool Upsert(string sessionId, long head, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        string? before = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT head, text FROM sessions WHERE session_id=$session;";
            read.Parameters.AddWithValue("$session", sessionId);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetInt64(0) >= head)
                    return false;
                before = reader.GetString(1);
            }
        }
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO sessions (item_id, session_id, head, text, updated_at) VALUES ($item, $session, $head, $text, $at)
            ON CONFLICT (session_id) DO UPDATE SET head=excluded.head, text=excluded.text, updated_at=excluded.updated_at;
            """;
        write.Parameters.AddWithValue("$item", ItemIdOf(sessionId));
        write.Parameters.AddWithValue("$session", sessionId);
        write.Parameters.AddWithValue("$head", head);
        write.Parameters.AddWithValue("$text", text);
        write.Parameters.AddWithValue("$at", Ticks.From(_time.GetUtcNow()));
        write.ExecuteNonQuery();
        transaction.Commit();
        return !string.Equals(before, text, StringComparison.Ordinal);
    }

    /// <summary>The cards of the given sessions that exist, by session id.</summary>
    public IReadOnlyDictionary<string, SessionCard> Get(IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, session_id, head, text, updated_at FROM sessions WHERE session_id=$session;";
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var cards = new Dictionary<string, SessionCard>(StringComparer.Ordinal);
        foreach (var id in sessionIds)
        {
            session.Value = id;
            using var reader = command.ExecuteReader();
            if (reader.Read())
                cards[id] = Read(reader);
        }
        return cards;
    }

    /// <summary>
    /// Full-text search over the cards, ranked by BM25. As in <see cref="EpisodicIndex.Search"/>, a query of only terms under
    /// three characters matches by LIKE instead, most recently updated first.
    /// </summary>
    public IReadOnlyList<SessionCardHit> Search(string query, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var (match, shortTerms) = FtsQuery.Build(query);
        if (match is null && shortTerms.Count == 0)
            return [];
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        const string columns = "s.item_id, s.session_id, s.head, s.text, s.updated_at";
        if (match is not null)
        {
            command.CommandText = $"""
                SELECT {columns}, bm25(sessions_fts) AS rank FROM sessions_fts JOIN sessions s ON s.id = sessions_fts.rowid
                WHERE sessions_fts MATCH $match ORDER BY rank, s.updated_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$match", match);
        }
        else
        {
            var likes = string.Join(" OR ", shortTerms.Select((_, i) => $"s.text LIKE $t{i} ESCAPE '\\'"));
            command.CommandText = $"""
                SELECT {columns}, 0.0 AS rank FROM sessions s WHERE ({likes})
                ORDER BY s.updated_at DESC, s.id DESC LIMIT $limit;
                """;
            for (var i = 0; i < shortTerms.Count; i++)
                command.Parameters.AddWithValue($"$t{i}", FtsQuery.LikePattern(shortTerms[i]));
        }
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var hits = new List<SessionCardHit>();
        while (reader.Read())
            hits.Add(new SessionCardHit(Read(reader), reader.GetDouble(5)));
        return hits;
    }

    private static SessionCard Read(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), Ticks.To(reader.GetInt64(4)));
}
