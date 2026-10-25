using System.Globalization;
using Corvid.Abstractions.Events;
using Microsoft.Data.Sqlite;

namespace Corvid.Kernel.Events;

/// <summary>
/// A dedicated on-disk event database. Each operation owns its connection; instances can share a file. Every appended event
/// passes through <see cref="EventScrubber"/> first (C-040), so no credential value a detector rule finds is stored.
/// </summary>
public sealed class SqliteEventLog : IEventLog
{
    private readonly string connectionString;

    public SqliteEventLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:")
            throw new ArgumentException("The event log requires an on-disk database.", nameof(path));
        path = Path.GetFullPath(path);
        // A4 verification: on a fresh home the log can be the first to make the data directory: owner only.
        Corvid.Abstractions.IO.PrivateDirectory.CreateFor(path);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooled (C-011): without a pool every append closed the file's last connection, and SQLite checkpoints the WAL
            // on that close, which roughly tripled an append's cost (measured 6.8 ms against 2.3 ms). Commits still fsync
            // the WAL (synchronous=FULL); the WAL is checkpointed by SQLite's automatic threshold instead.
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();

        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is not (0 or 1))
            throw new InvalidDataException($"Unsupported event database schema version {version}.");
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE events (
                    session_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL CHECK (sequence > 0),
                    event_id TEXT NOT NULL UNIQUE,
                    recorded_at TEXT NOT NULL,
                    payload_version INTEGER NOT NULL,
                    payload TEXT NOT NULL,
                    PRIMARY KEY (session_id, sequence)
                );
                CREATE TRIGGER events_no_update BEFORE UPDATE ON events
                    BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
                CREATE TRIGGER events_no_delete BEFORE DELETE ON events
                    BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public long GetHead(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        return Head(connection, null, sessionId);
    }

    public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedHead);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            throw new ArgumentException("An append batch must not be empty.", nameof(events));
        cancellationToken.ThrowIfCancellationRequested();

        // Scrub credential values (C-040) and serialize before acquiring the writer lock, and detach JsonElements from
        // caller-owned documents. The returned events are read back from the scrubbed JSON, so callers see what was stored.
        var prepared = events.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Payload);
            if (item.EventId == Guid.Empty)
                throw new ArgumentException("An event ID must not be empty.", nameof(events));
            return (item.EventId, Json: EventJson.Serialize(EventScrubber.Scrub(item.Payload)));
        }).ToArray();

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var head = Head(connection, transaction, sessionId);
        if (head != expectedHead)
            throw new EventLogConflictException(sessionId, expectedHead, head);

        var appended = new List<StoredEvent>(prepared.Length);
        foreach (var item in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = checked(++head);
            var payload = EventJson.Deserialize(EventJson.Version, item.Json);
            // Other event relationships belong to the session actor. A summary can only refer to prior history.
            if (payload is Compacted compacted && (compacted.FromSequence < 1 ||
                compacted.ThroughSequence < compacted.FromSequence || compacted.ThroughSequence >= sequence))
                throw new ArgumentException("Compaction must reference an existing, non-empty range of prior events.", nameof(events));

            var recordedAt = DateTimeOffset.UtcNow;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO events (session_id, sequence, event_id, recorded_at, payload_version, payload)
                VALUES ($session, $sequence, $id, $at, $version, $payload);
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$id", item.EventId.ToString("D"));
            command.Parameters.AddWithValue("$at", recordedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$version", EventJson.Version);
            command.Parameters.AddWithValue("$payload", item.Json);
            command.ExecuteNonQuery();
            appended.Add(new(sessionId, sequence, item.EventId, recordedAt, payload));
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return appended.AsReadOnly();
    }

    public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue,
        int limit = 256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(throughSequence, afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 4096);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, recorded_at, payload_version, payload FROM events
            WHERE session_id=$session AND sequence>$after AND sequence<=$through
            ORDER BY sequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$through", throughSequence);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<StoredEvent>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new(sessionId, reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                EventJson.Deserialize(reader.GetInt32(3), reader.GetString(4))));
        }
        return result.AsReadOnly();
    }

    /// <summary>Every session with at least one event, in ordinal order. For tools that read a whole log, such as calibration.</summary>
    public IReadOnlyList<string> ListSessions(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT session_id FROM events ORDER BY session_id COLLATE BINARY;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result.AsReadOnly();
    }

    /// <summary>
    /// A marker that changes whenever any session is appended to: the highest row id. The log is append-only (its triggers
    /// refuse updates and deletes), so the marker only grows, and reading it is one look at the table's last row. A
    /// surface that polls for other processes' writes checks it first and reads the sessions only when it moved.
    /// </summary>
    public long ChangeMarker(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(rowid), 0) FROM events;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Every session with at least one event and its head, in one query (the primary key's index).</summary>
    public IReadOnlyDictionary<string, long> ListHeads(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, MAX(sequence) FROM events GROUP BY session_id;";
        using var reader = command.ExecuteReader();
        var heads = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            heads[reader.GetString(0)] = reader.GetInt64(1);
        return heads;
    }

    /// <summary>
    /// The <paramref name="limit"/> sessions most recently appended to, the latest first: for a surface that reads recent
    /// activity without walking every session's log.
    /// </summary>
    public IReadOnlyList<string> ListRecentSessions(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Rows are appended in order, so a session's highest rowid is its latest append.
        command.CommandText = "SELECT session_id FROM events GROUP BY session_id ORDER BY MAX(rowid) DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result.AsReadOnly();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static long Head(SqliteConnection connection, SqliteTransaction? transaction, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM events WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        return (long)command.ExecuteScalar()!;
    }
}
