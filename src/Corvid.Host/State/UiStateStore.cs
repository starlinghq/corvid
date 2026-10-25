using System.Collections.Immutable;
using System.Globalization;
using Corvid.Host.Configuration;
using Microsoft.Data.Sqlite;

namespace Corvid.Host.State;

/// <summary>A draft kept for a session: the composer's text and the one put aside with <c>Ctrl+S</c>.</summary>
public sealed record UiDraft(string SessionId, string Text, string? Stash, DateTimeOffset UpdatedAt);

/// <summary>The last review point of a session (docs/plan/ui.md §4.7): the turn comments were sent at, how many, when, and the log head then.</summary>
public sealed record UiReviewPoint(string SessionId, int Turn, int CommentsSent, DateTimeOffset At, long Head);

/// <summary>When a person last looked at a session, and how far into its log; a recap on return reads from here.</summary>
public sealed record UiSeen(string SessionId, long Sequence, DateTimeOffset At);

/// <summary>
/// <c>ui.db</c> (G-7, docs/plan/ui.md §8): the state a person's surfaces keep about sessions and themselves, beside the
/// event log and never in it, so the CLI and the windowed app share it. Drafts, pinned sessions, review points, when a
/// session was last seen, and the app's own preferences (density, notifications, layout). Nothing here is ever sent to a
/// model or decides what a tool may do: losing the file loses conveniences, never history.
/// <para>
/// Safe from several processes at once: SQLite in WAL mode, each operation on its own short connection and transaction,
/// writes as upserts on one row (or, for preferences, one batch), and a busy timeout so a writer waits for another
/// process's write rather than failing. The last write of a row wins. The schema is versioned with <c>user_version</c>; a
/// file from a newer Corvid is refused rather than read wrongly.
/// </para>
/// </summary>
public sealed class UiStateStore
{
    /// <summary>The schema this build writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The longest draft kept, in characters; a longer one is cut (a paste of a whole file is not a draft).</summary>
    public const int MaxDraftLength = 64 * 1024;

    private readonly string _connectionString;
    private readonly Lock _gate = new();
    private bool _ready;

    public UiStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:")
            throw new ArgumentException("ui.db is shared between processes, so it must be a file.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            // Seconds a statement waits on another process's write lock before failing.
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <summary><c>ui.db</c> in the data directory, beside <c>events.db</c>.</summary>
    public static string PathFor(CorvidOptions options) => System.IO.Path.Combine(CorvidPaths.DataDirectory(options), "ui.db");

    public string Path { get; }

    // ---- drafts ----

    public UiDraft? GetDraft(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text, stash, updated_at FROM drafts WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new UiDraft(sessionId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), Time(reader.GetString(2)))
            : null;
    }

    /// <summary>Keeps a session's draft; an empty text with no stash forgets it.</summary>
    public void SetDraft(string sessionId, string text, string? stash, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        text = Cap(text)!;
        stash = Cap(string.IsNullOrEmpty(stash) ? null : stash);
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (text.Length == 0 && stash is null)
        {
            command.CommandText = "DELETE FROM drafts WHERE session_id=$session;";
        }
        else
        {
            command.CommandText = """
                INSERT INTO drafts (session_id, text, stash, updated_at) VALUES ($session, $text, $stash, $at)
                ON CONFLICT(session_id) DO UPDATE SET text=excluded.text, stash=excluded.stash, updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$stash", (object?)stash ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", Stamp(at));
        }
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
    }

    // ---- pins ----

    /// <summary>The pinned sessions, kept at the top of the app's rail and marked in <c>/sessions</c>.</summary>
    public ImmutableHashSet<string> Pinned()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM pins;";
        using var reader = command.ExecuteReader();
        var pinned = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        while (reader.Read())
            pinned.Add(reader.GetString(0));
        return pinned.ToImmutable();
    }

    public bool IsPinned(string sessionId) => Pinned().Contains(sessionId);

    public void SetPinned(string sessionId, bool pinned, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = pinned
            ? "INSERT INTO pins (session_id, pinned_at) VALUES ($session, $at) ON CONFLICT(session_id) DO NOTHING;"
            : "DELETE FROM pins WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$at", Stamp(at));
        command.ExecuteNonQuery();
    }

    // ---- review points ----

    public UiReviewPoint? GetReviewPoint(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT turn, comments, at, head FROM review_points WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new UiReviewPoint(sessionId, reader.GetInt32(0), reader.GetInt32(1), Time(reader.GetString(2)), reader.GetInt64(3))
            : null;
    }

    public void SetReviewPoint(UiReviewPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentException.ThrowIfNullOrWhiteSpace(point.SessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_points (session_id, turn, comments, at, head) VALUES ($session, $turn, $comments, $at, $head)
            ON CONFLICT(session_id) DO UPDATE SET turn=excluded.turn, comments=excluded.comments, at=excluded.at, head=excluded.head;
            """;
        command.Parameters.AddWithValue("$session", point.SessionId);
        command.Parameters.AddWithValue("$turn", point.Turn);
        command.Parameters.AddWithValue("$comments", point.CommentsSent);
        command.Parameters.AddWithValue("$at", Stamp(point.At));
        command.Parameters.AddWithValue("$head", point.Head);
        command.ExecuteNonQuery();
    }

    // ---- last seen ----

    public UiSeen? LastSeen(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, at FROM seen WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new UiSeen(sessionId, reader.GetInt64(0), Time(reader.GetString(1))) : null;
    }

    /// <summary>Records that a person saw the session through <paramref name="sequence"/> at <paramref name="at"/>.</summary>
    public void MarkSeen(string sessionId, long sequence, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        using var connection = Open();
        using var command = connection.CreateCommand();
        // The furthest point either surface reached, at the latest time either saw it.
        command.CommandText = """
            INSERT INTO seen (session_id, sequence, at) VALUES ($session, $sequence, $at)
            ON CONFLICT(session_id) DO UPDATE SET sequence=MAX(seen.sequence, excluded.sequence), at=MAX(seen.at, excluded.at);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$at", Stamp(at));
        command.ExecuteNonQuery();
    }

    // ---- preferences ----

    /// <summary>The app's preferences by key (<c>Density</c>, <c>NotifyTurnEnd</c>, <c>StartLayout</c>); keys never set are absent.</summary>
    public ImmutableDictionary<string, string> Preferences()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM preferences;";
        using var reader = command.ExecuteReader();
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            values[reader.GetString(0)] = reader.GetString(1);
        return values.ToImmutable();
    }

    /// <summary>Sets preferences in one transaction, so another process reads all of them or none.</summary>
    public void SetPreferences(IReadOnlyDictionary<string, string> values, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var (key, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO preferences (key, value, updated_at) VALUES ($key, $value, $at)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$at", Stamp(at));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // ---- the file ----

    private SqliteConnection Open()
    {
        EnsureCreated();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Created on first use, so a surface that never touches ui.db never makes the file.
    private void EnsureCreated()
    {
        lock (_gate)
        {
            if (_ready)
                return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }
            // An immediate transaction: two processes creating the file at once take turns, and the second finds version 1.
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
                throw new InvalidDataException($"{Path} has schema version {version}, written by a newer Corvid; this one reads version {SchemaVersion}.");
            if (version == 0)
            {
                command.CommandText = """
                    CREATE TABLE drafts (session_id TEXT PRIMARY KEY, text TEXT NOT NULL, stash TEXT, updated_at TEXT NOT NULL);
                    CREATE TABLE pins (session_id TEXT PRIMARY KEY, pinned_at TEXT NOT NULL);
                    CREATE TABLE review_points (session_id TEXT PRIMARY KEY, turn INTEGER NOT NULL, comments INTEGER NOT NULL,
                        at TEXT NOT NULL, head INTEGER NOT NULL);
                    CREATE TABLE seen (session_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL, at TEXT NOT NULL);
                    CREATE TABLE preferences (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _ready = true;
        }
    }

    private static string? Cap(string? text) => text is { Length: > MaxDraftLength } ? text[..MaxDraftLength] : text;

    // Round-trip UTC text: sorts as time, so MAX() in MarkSeen compares instants.
    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
