using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Models.Privacy;
using Microsoft.Data.Sqlite;

namespace Corvid.Host.Audit;

/// <summary>One request in the egress ledger: where it went, what the gate did, and whether its text as sent is kept.</summary>
/// <param name="Number">The ledger's own number for it, increasing ("request 7").</param>
/// <param name="RequestId">Its id, which the session log's <see cref="EgressRecorded"/> event carries.</param>
/// <param name="SessionId">The session it was sent for; null for a request outside any session.</param>
/// <param name="Outcome">An <see cref="EgressOutcomes"/> name.</param>
/// <param name="Placeholders">How many distinct values the gate redacted in it.</param>
/// <param name="Kept">Whether its text as sent is in the store (never for a refused request).</param>
public sealed record EgressEntry(long Number, string RequestId, string? SessionId, DateTimeOffset At, string Model, string Slot,
    string Outcome, int Placeholders, long Bytes, ImmutableDictionary<string, int> Labels, string Reason, bool Tainted, string? Host,
    string? Secret, bool Kept)
{
    /// <summary>Something left the machine.</summary>
    public bool Left => Outcome != EgressOutcomes.Refused;
}

/// <summary>Requests to gated models since a moment: all of them, the values redacted in them, and how many were refused.</summary>
public sealed record EgressCounts(int Requests, int RedactedValues, int Refused);

/// <summary>
/// How long the egress store keeps requests (<c>Corvid:Privacy:Ledger</c>): requests older than <see cref="MaxAge"/> go, then
/// the oldest until the message text kept is at most <see cref="MaxBytes"/>; null keeps them all by that measure.
/// </summary>
public sealed record EgressRetention(TimeSpan? MaxAge, long? MaxBytes)
{
    /// <summary>90 days and 256 MB, the defaults of <c>Corvid:Privacy:Ledger</c>.</summary>
    public static EgressRetention Default { get; } = new(TimeSpan.FromDays(90), 256L * 1024 * 1024);
}

/// <summary>What a prune removed: requests, and the message bodies no request still referenced.</summary>
/// <param name="NotCompacted">
/// Compacting was asked for and the file was in use (a process recording into it), so it was pruned but not compacted
/// (A4 verification); <c>corvid egress prune</c> says so.
/// </param>
public sealed record EgressPruned(int Requests, int Messages, bool NotCompacted = false);

/// <summary>
/// The egress ledger (G-9, docs/plan/ui.md §4.12, F8): every request the privacy egress gate decided on, for every gated
/// binding (each cloud model by default). Each observation is kept in <c>egress.db</c> beside the log: the request as sent,
/// after redaction, as <see cref="EgressRendering"/> renders it, stored message by message under the digest of its text, so
/// a conversation's shared prefix is kept once (the gate's placeholders are stable per session, so the prefix does not
/// change between requests). An <see cref="EgressRecorded"/> event in the session's log then links the request to the turn
/// it was sent in. A refused request is recorded (it counts) but nothing of it is kept: nothing of it left, and it is
/// unredacted. Nothing here ever holds a secret's value: the gate redacts credentials before anything is sent, and each
/// message is credential-scrubbed again before it is stored.
/// <para>
/// Recording never stops a request: a store that cannot be written loses the text (the event says why, in
/// <see cref="EgressRecorded.Unkept"/>), and an event that cannot be appended is reported through <see cref="Failed"/>.
/// The store is created, owner only, at the first request; readers never create it. Each operation owns its connection, so
/// the CLI and the app can record into one file.
/// </para>
/// </summary>
public sealed class EgressLedger
{
    private const int SchemaVersion = 1;
    private const int MaxAppendAttempts = 8;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private bool _created;
    // When a prune is next due in this process, in UTC ticks (the store says when the last one ran, in any process), and
    // whether one runs now.
    private long _nextPrune;
    private int _pruning;

    /// <summary>How often the store is pruned while requests are recorded: at most daily, the first time on opening it.</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    public EgressLedger(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The store's file.</summary>
    public string Path { get; }

    /// <summary>The event log the <see cref="EgressRecorded"/> events go to; set once the runtime has opened it.</summary>
    public IEventLog? Log { get; set; }

    /// <summary>
    /// Told why a request could not be recorded in full, or the store not pruned. Called inside the model call, so never a
    /// write to the terminal: the runtime routes it to its notices (<c>CorvidRuntime.Notice</c>); by default a trace line.
    /// </summary>
    public Action<string> Failed { get; set; } = reason => System.Diagnostics.Trace.WriteLine($"corvid: egress ledger: {reason}");

    /// <summary>
    /// How long requests are kept: pruned when the store is first opened to record in a process and at most daily after
    /// (<see cref="PruneInterval"/>), and by <c>corvid egress prune</c>.
    /// </summary>
    public EgressRetention Retention { get; set; } = EgressRetention.Default;

    /// <summary>The prune running in the background, if any (tests wait for it).</summary>
    internal Task Pruning { get; private set; } = Task.CompletedTask;

    /// <summary>The observer for one gated binding: what each of its requests is recorded with.</summary>
    /// <param name="slot">The slot the binding serves (<c>generator</c>, <c>summarizer</c>…).</param>
    /// <param name="host">Where its requests go, when known.</param>
    /// <param name="secret">The Vault secret they authenticate with, by name.</param>
    public IEgressObserver For(string slot, string? host = null, string? secret = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return new Observer(this, slot, string.IsNullOrWhiteSpace(host) ? null : host, string.IsNullOrWhiteSpace(secret) ? null : secret);
    }

    private sealed class Observer(EgressLedger ledger, string slot, string? host, string? secret) : IEgressObserver
    {
        public void Observe(EgressObservation observation) => ledger.Record(observation, slot, host, secret);
    }

    /// <summary>Records one observation; never throws.</summary>
    internal void Record(EgressObservation observation, string slot, string? host, string? secret)
    {
        try
        {
            RecordCore(observation, slot, host, secret);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Failed($"a request to {observation.Profile.Id} could not be recorded: {exception.Message}");
        }
    }

    private void RecordCore(EgressObservation observation, string slot, string? host, string? secret)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var decision = observation.Decision;
        // A request the policy would redact but that held nothing to redact went as it was.
        var outcome = decision.Outcome switch
        {
            EgressOutcome.Redacted when observation.Placeholders > 0 => EgressOutcomes.Redacted,
            EgressOutcome.Refused => EgressOutcomes.Refused,
            _ => EgressOutcomes.Sent,
        };
        var labels = decision.Labels.ToImmutableDictionary(StringComparer.Ordinal);
        var blocks = observation.Sent is { } sent ? EgressRendering.Blocks(sent) : [];
        var tools = observation.Sent is null ? [] : observation.Tools;
        var bytes = observation.Sent is null ? 0 : EgressRendering.Size(EgressRendering.Full(blocks, tools));
        var sessionId = observation.Context.SessionId;
        string? unkept = null;
        try
        {
            Write(requestId, sessionId, observation.Profile.Id, slot, outcome, observation.Placeholders, bytes, labels, decision.Reason,
                observation.Context.Tainted, host, secret, observation.Sent is null ? null : blocks, tools);
            SchedulePrune();
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            unkept = "the egress store could not be written: " + exception.Message;
            Failed(unkept);
        }

        if (sessionId is null || Log is not { } log)
            return;
        var recorded = new EgressRecorded(requestId, observation.Profile.Id, outcome, labels, observation.Placeholders, bytes, decision.Reason,
            observation.Context.Tainted, slot, host, secret, unkept);
        Append(log, sessionId, recorded);
    }

    // The egress event does not depend on the head: a concurrent writer (the turn itself) only means retrying at the new head.
    private static void Append(IEventLog log, string sessionId, SessionEvent payload)
    {
        var events = new[] { EventData.Create(payload) };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                log.Append(sessionId, log.GetHead(sessionId), events);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }

    private void Write(string requestId, string? sessionId, string model, string slot, string outcome, int placeholders, long bytes,
        ImmutableDictionary<string, int> labels, string reason, bool tainted, string? host, string? secret, ImmutableArray<string>? blocks,
        ImmutableArray<string> tools)
    {
        using var connection = Open(create: true)!;
        using var transaction = connection.BeginTransaction(deferred: false);
        var digests = new List<string>();
        foreach (var block in blocks ?? [])
        {
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(block)));
            digests.Add(digest);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO messages (digest, body) VALUES ($digest, $body);";
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$body", block);
            insert.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO requests (request_id, session_id, recorded_at, model, slot, outcome, placeholders, bytes, labels, reason, tainted,
                host, secret, messages, tools)
            VALUES ($id, $session, $at, $model, $slot, $outcome, $placeholders, $bytes, $labels, $reason, $tainted, $host, $secret, $messages, $tools);
            """;
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$placeholders", placeholders);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(labels.ToDictionary(StringComparer.Ordinal), EgressJson.Default.DictionaryStringInt32));
        // The gate's reason can quote what it matched: scrubbed as the log's copy is.
        command.Parameters.AddWithValue("$reason", Corvid.Secrets.CredentialScrub.Scrub(reason, Corvid.Secrets.CredentialDetector.Egress).Text);
        command.Parameters.AddWithValue("$tainted", tainted ? 1 : 0);
        command.Parameters.AddWithValue("$host", (object?)host ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", (object?)secret ?? DBNull.Value);
        command.Parameters.AddWithValue("$messages", blocks is null ? DBNull.Value : JsonSerializer.Serialize(digests.ToArray(), EgressJson.Default.StringArray));
        command.Parameters.AddWithValue("$tools", JsonSerializer.Serialize(tools.ToArray(), EgressJson.Default.StringArray));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // ---- retention ----

    // Off the model call: a prune deletes, and may take a while on a large store.
    private void SchedulePrune()
    {
        if (_time.GetUtcNow().UtcTicks < Volatile.Read(ref _nextPrune) || Interlocked.CompareExchange(ref _pruning, 1, 0) != 0)
            return;
        Pruning = Task.Run(() =>
        {
            try
            {
                PruneIfDue();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Failed("the egress store could not be pruned: " + exception.Message);
                Volatile.Write(ref _nextPrune, (_time.GetUtcNow() + PruneInterval).UtcTicks);
            }
            finally
            {
                Volatile.Write(ref _pruning, 0);
            }
        });
    }

    /// <summary>
    /// Prunes when the last prune (by any process, as the store records) is a day old or more, or there was none; otherwise
    /// notes when the next is due. True when it pruned.
    /// </summary>
    internal bool PruneIfDue()
    {
        var now = _time.GetUtcNow();
        using var connection = Open(create: false);
        if (connection is null)
            return false;
        if (LastPruned(connection) is { } last && now - last < PruneInterval)
        {
            Volatile.Write(ref _nextPrune, (last + PruneInterval).UtcTicks);
            return false;
        }
        Prune(connection, now, vacuum: false);
        Volatile.Write(ref _nextPrune, (now + PruneInterval).UtcTicks);
        return true;
    }

    /// <summary>
    /// Removes the requests <see cref="Retention"/> no longer keeps, then the message bodies no remaining request references
    /// (<c>corvid egress prune</c>; recording does it daily). With <paramref name="vacuum"/> the file is compacted after, so
    /// it shrinks on disk. Nothing to prune when there is no store (it is not created).
    /// </summary>
    public EgressPruned Prune(bool vacuum = false)
    {
        using var connection = Open(create: false);
        return connection is null ? new EgressPruned(0, 0) : Prune(connection, _time.GetUtcNow(), vacuum);
    }

    private EgressPruned Prune(SqliteConnection connection, DateTimeOffset now, bool vacuum)
    {
        Execute(connection, MetaTable);
        var requests = 0;
        var messages = 0;
        var retention = Retention;
        if (retention.MaxAge is { } age)
        {
            requests += Execute(connection, "DELETE FROM requests WHERE recorded_at < $cutoff;",
                ("$cutoff", (now - age).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            messages += Unreferenced(connection);
        }
        if (retention.MaxBytes is { } max)
        {
            // The oldest tenth at a time: a conversation's shared prefix is freed only once all its requests have gone. The
            // newest request always stays (A4 verification): one conversation over the cap must not empty the ledger.
            while (Scalar(connection, "SELECT COALESCE(SUM(LENGTH(CAST(body AS BLOB))), 0) FROM messages;") > max)
            {
                var count = Scalar(connection, "SELECT COUNT(*) FROM requests;");
                if (count <= 1)
                    break;
                requests += Execute(connection, "DELETE FROM requests WHERE number IN (SELECT number FROM requests ORDER BY number LIMIT $n);",
                    ("$n", Math.Min(count - 1, Math.Max(1, count / 10))));
                messages += Unreferenced(connection);
            }
        }
        Execute(connection, "INSERT INTO meta (key, value) VALUES ('pruned_at', $at) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        if (vacuum && requests + messages > 0)
        {
            try
            {
                BeforeVacuum?.Invoke();
                Execute(connection, "VACUUM;");
            }
            // A4 verification: another process recording (or reading) holds the file; what was pruned stays pruned.
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) // SQLITE_BUSY, SQLITE_LOCKED
            {
                return new EgressPruned(requests, messages, NotCompacted: true);
            }
        }
        return new EgressPruned(requests, messages);
    }

    /// <summary>Runs just before a prune compacts the file (tests: another process starts writing then).</summary>
    internal Action? BeforeVacuum { get; set; }

    private const string MetaTable ="CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);";

    // Bodies no request's message list names any more.
    private static int Unreferenced(SqliteConnection connection) => Execute(connection, """
        DELETE FROM messages WHERE digest NOT IN
            (SELECT value FROM requests, json_each(requests.messages) WHERE requests.messages IS NOT NULL);
        """);

    private static DateTimeOffset? LastPruned(SqliteConnection connection)
    {
        // A store an earlier A4 build made has no meta table yet.
        Execute(connection, MetaTable);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='pruned_at';";
        return command.ExecuteScalar() is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    private static int Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- reading ----

    /// <summary>
    /// The ledger's requests, newest first: a session's, or every one when <paramref name="sessionId"/> is null; at most
    /// <paramref name="limit"/>. Empty when there is no store yet (it is not created).
    /// </summary>
    public IReadOnlyList<EgressEntry> List(string? sessionId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(create: false);
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM requests
            WHERE ($session IS NULL OR session_id = $session)
            ORDER BY number DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<EgressEntry>();
        while (reader.Read())
            entries.Add(Entry(reader));
        return entries;
    }

    /// <summary>One request by its id or its number; null when the ledger has no such request.</summary>
    public EgressEntry? Find(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        using var connection = Open(create: false);
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM requests WHERE request_id = $id OR CAST(number AS TEXT) = $id;";
        command.Parameters.AddWithValue("$id", request);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Entry(reader) : null;
    }

    /// <summary>
    /// The request as sent, rendered (<see cref="EgressRendering.Full"/>), reassembled from the messages kept for it; null when
    /// its text was not kept (a refused request, or one the store could not write).
    /// </summary>
    public string? Render(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var connection = Open(create: false);
        if (connection is null)
            return null;
        string? messages, tools;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT messages, tools FROM requests WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", requestId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
                return null;
            messages = reader.GetString(0);
            tools = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        var digests = JsonSerializer.Deserialize(messages, EgressJson.Default.StringArray) ?? [];
        var blocks = new List<string>(digests.Length);
        foreach (var digest in digests)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM messages WHERE digest = $digest;";
            command.Parameters.AddWithValue("$digest", digest);
            if (command.ExecuteScalar() is not string body)
                throw new InvalidDataException($"The egress store lost a message of request {requestId}.");
            blocks.Add(body);
        }
        return EgressRendering.Full(blocks, tools is null ? [] : JsonSerializer.Deserialize(tools, EgressJson.Default.StringArray) ?? []);
    }

    /// <summary>How many requests the ledger holds since <paramref name="since"/>, the values redacted in them and how many were refused.</summary>
    public EgressCounts Count(DateTimeOffset since)
    {
        using var connection = Open(create: false);
        if (connection is null)
            return new(0, 0, 0);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(placeholders), 0), COALESCE(SUM(outcome = 'refused'), 0)
            FROM requests WHERE recorded_at >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private const string Columns = "number, request_id, session_id, recorded_at, model, slot, outcome, placeholders, bytes, labels, reason, tainted, " +
        "host, secret, messages IS NOT NULL";

    private static EgressEntry Entry(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt32(7),
        reader.GetInt64(8),
        (JsonSerializer.Deserialize(reader.GetString(9), EgressJson.Default.DictionaryStringInt32) ?? []).ToImmutableDictionary(StringComparer.Ordinal),
        reader.GetString(10),
        reader.GetInt32(11) != 0,
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.GetBoolean(14));

    /// <summary>A connection to the store, made and set up on first use when <paramref name="create"/>; null when there is none to read.</summary>
    private SqliteConnection? Open(bool create)
    {
        if (!create && !File.Exists(Path))
            return null;
        if (create)
            Create();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWrite,
            // Not pooled: a connection per operation, so a test's or another process's file is let go of at once.
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        if (!create && !Ready(connection))
        {
            // Made by another process that has not set it up yet: nothing is recorded in it.
            connection.Dispose();
            return null;
        }
        return connection;
    }

    private void Create()
    {
        lock (_gate)
        {
            if (_created && File.Exists(Path))
                return;
            Configuration.CorvidPaths.CreatePrivateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (!File.Exists(Path))
            {
                // Owner only before SQLite opens it: the store holds conversation content.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                try
                {
                    using (new FileStream(Path, options))
                    {
                    }
                }
                catch (IOException) when (File.Exists(Path))
                {
                    // Another process made it first.
                }
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString());
            connection.Open();
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
            if (version is not (0 or SchemaVersion))
                throw new InvalidDataException($"Unsupported egress store schema version {version}.");
            if (version == 0)
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS messages (
                        digest TEXT PRIMARY KEY,
                        body TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS requests (
                        number INTEGER PRIMARY KEY AUTOINCREMENT,
                        request_id TEXT NOT NULL UNIQUE,
                        session_id TEXT,
                        recorded_at TEXT NOT NULL,
                        model TEXT NOT NULL,
                        slot TEXT NOT NULL,
                        outcome TEXT NOT NULL,
                        placeholders INTEGER NOT NULL,
                        bytes INTEGER NOT NULL,
                        labels TEXT NOT NULL,
                        reason TEXT NOT NULL,
                        tainted INTEGER NOT NULL,
                        host TEXT,
                        secret TEXT,
                        messages TEXT,
                        tools TEXT
                    );
                    CREATE INDEX IF NOT EXISTS requests_session ON requests (session_id, number);
                    CREATE INDEX IF NOT EXISTS requests_time ON requests (recorded_at);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
            }
            // When the store was last pruned, by any process: a table beside the schema, so a store made before it needs no
            // new version, and an older Corvid still reads this one.
            command.CommandText = MetaTable;
            command.ExecuteNonQuery();
            transaction.Commit();
            _created = true;
        }
    }

    private static bool Ready(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return version switch
        {
            0 => false,
            SchemaVersion => true,
            _ => throw new InvalidDataException($"Unsupported egress store schema version {version}."),
        };
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class EgressJson : System.Text.Json.Serialization.JsonSerializerContext;
