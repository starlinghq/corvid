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

/// <summary>Requests to gated models since a moment: all, how many were redacted, how many were refused.</summary>
public sealed record EgressCounts(int Requests, int Redacted, int Refused);

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

    /// <summary>Told why a request could not be recorded in full; by default a line on standard error.</summary>
    public Action<string> Failed { get; set; } = reason => Console.Error.WriteLine($"corvid: egress ledger: {reason}");

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
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$tainted", tainted ? 1 : 0);
        command.Parameters.AddWithValue("$host", (object?)host ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", (object?)secret ?? DBNull.Value);
        command.Parameters.AddWithValue("$messages", blocks is null ? DBNull.Value : JsonSerializer.Serialize(digests.ToArray(), EgressJson.Default.StringArray));
        command.Parameters.AddWithValue("$tools", JsonSerializer.Serialize(tools.ToArray(), EgressJson.Default.StringArray));
        command.ExecuteNonQuery();
        transaction.Commit();
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

    /// <summary>How many requests the ledger holds since <paramref name="since"/>, redacted and refused among them.</summary>
    public EgressCounts Count(DateTimeOffset since)
    {
        using var connection = Open(create: false);
        if (connection is null)
            return new(0, 0, 0);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(outcome = 'redacted'), 0), COALESCE(SUM(outcome = 'refused'), 0)
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
            Mode = create ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
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
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
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
                    CREATE TABLE messages (
                        digest TEXT PRIMARY KEY,
                        body TEXT NOT NULL
                    );
                    CREATE TABLE requests (
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
                    CREATE INDEX requests_session ON requests (session_id, number);
                    CREATE INDEX requests_time ON requests (recorded_at);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
            }
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
