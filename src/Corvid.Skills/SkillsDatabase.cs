using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Corvid.Skills;

public enum OpportunityState
{
    Queued,
    /// <summary>Drafted and replay-tested; waiting in <c>/skills pending</c>.</summary>
    Staged,
    Applied,
    /// <summary>Drafting, the replay test or the scan failed; the detail says why.</summary>
    Failed,
}

/// <summary>A turn <c>skill_opportunity</c> queued for distillation, and how far it got.</summary>
/// <param name="Tainted">The source turn held outside content: its draft always waits for review.</param>
/// <param name="SuccessDecisionId">The <c>task_succeeded</c> decision (C-026); null for rows queued before schema version 2.</param>
/// <param name="SuccessCalibrated">A calibrated decider confirmed the task's success; otherwise the draft always waits for review.</param>
public sealed record OpportunityRow(string Id, string SessionId, long FromSequence, long ThroughSequence, string DecisionId,
    string Request, OpportunityState State, string? Detail, DateTimeOffset Created, bool Tainted = false, string? SuccessDecisionId = null,
    bool SuccessCalibrated = false);

public enum PendingSkillStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>A drafted skill change waiting for review because <c>skill_write_safe</c> did not let it go live.</summary>
/// <param name="Content">The SKILL.md to write.</param>
/// <param name="DecisionId">The <c>skill_write_safe</c> decision, whose outcome the review records.</param>
/// <param name="Replay">What the replay test found, for the reviewer.</param>
public sealed record PendingSkill(string Id, string Skill, string Content, string SessionId, string DecisionId, string Reason,
    string Replay, string? OpportunityId, PendingSkillStatus Status, DateTimeOffset Created)
{
    /// <summary>
    /// What the draft was staged against: the skill's last revision then, <c>""</c> when there was no such skill, or null when
    /// it is not known (a draft staged before schema version 3).
    /// </summary>
    public string? BaseRevision { get; init; }
}

/// <summary>Per-skill state the curator reads: last use, use count and whether the user pinned it.</summary>
public sealed record SkillUsage(string Skill, DateTimeOffset? LastUsed, int Uses, bool Pinned);

/// <summary>A skill eval run: which cases passed after a change.</summary>
public sealed record EvalRecord(string Skill, string Revision, int Passed, int Total, string Detail, DateTimeOffset At);

/// <summary>
/// The skill loop's own SQLite file (WAL, <c>synchronous=FULL</c>): the opportunity queue, staged drafts, usage and eval
/// results. Skill content lives in the git repository; this holds what changes too often, or is not yet a skill.
/// </summary>
public sealed class SkillsDatabase
{
    private const int SchemaVersion = 3;

    private readonly string _connectionString;

    public SkillsDatabase(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Time = time ?? TimeProvider.System;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
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
        if (version is not (0 or 1 or 2 or SchemaVersion))
            throw new InvalidDataException($"Unsupported skills database schema version {version}.");
        if (version == 0)
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }
        if (version < 2)
        {
            command.CommandText = SuccessMigration;
            command.ExecuteNonQuery();
        }
        if (version < 3)
        {
            command.CommandText = BaseRevisionMigration;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string Path { get; }

    internal TimeProvider Time { get; }

    // Version 1; later versions migrate from it.
    internal const string Schema = """
        CREATE TABLE opportunities (
            id TEXT PRIMARY KEY,
            session TEXT NOT NULL,
            from_sequence INTEGER NOT NULL,
            through_sequence INTEGER NOT NULL,
            decision_id TEXT NOT NULL,
            request TEXT NOT NULL,
            state TEXT NOT NULL,
            detail TEXT,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL,
            tainted INTEGER NOT NULL
        );
        CREATE INDEX opportunities_state ON opportunities(state, created);
        CREATE TABLE pending (
            id TEXT PRIMARY KEY,
            skill TEXT NOT NULL,
            content TEXT NOT NULL,
            session TEXT NOT NULL,
            decision_id TEXT NOT NULL,
            reason TEXT NOT NULL,
            replay TEXT NOT NULL,
            opportunity TEXT,
            status TEXT NOT NULL,
            created INTEGER NOT NULL
        );
        CREATE TABLE usage (
            skill TEXT PRIMARY KEY,
            last_used INTEGER,
            uses INTEGER NOT NULL DEFAULT 0,
            pinned INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE evals (
            skill TEXT NOT NULL,
            revision TEXT NOT NULL,
            passed INTEGER NOT NULL,
            total INTEGER NOT NULL,
            detail TEXT NOT NULL,
            at INTEGER NOT NULL
        );
        PRAGMA user_version = 1;
        """;

    // Version 2 (C-026): an opportunity records the task_succeeded decision that granted it, and whether a calibrated decider
    // answered. Rows queued earlier read as uncalibrated, so their drafts wait for review.
    internal const string SuccessMigration = """
        ALTER TABLE opportunities ADD COLUMN success_decision TEXT;
        ALTER TABLE opportunities ADD COLUMN success_calibrated INTEGER NOT NULL DEFAULT 0 CHECK (success_calibrated IN (0, 1));
        PRAGMA user_version = 2;
        """;

    // Version 3: what a staged draft was made against, so a review never applies it over a skill changed since.
    internal const string BaseRevisionMigration = """
        ALTER TABLE pending ADD COLUMN base_revision TEXT;
        PRAGMA user_version = 3;
        """;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
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

    private long Now => Time.GetUtcNow().UtcTicks;

    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    // ---- opportunities ----

    public OpportunityRow Enqueue(string sessionId, long from, long through, string decisionId, string request, bool tainted = false,
        string? successDecisionId = null, bool successCalibrated = false)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var now = Now;
        Execute("""
            INSERT INTO opportunities (id, session, from_sequence, through_sequence, decision_id, request, state, detail, created, updated,
                tainted, success_decision, success_calibrated)
            VALUES ($id, $session, $from, $through, $decision, $request, 'queued', NULL, $now, $now, $tainted, $success, $calibrated)
            """,
            ("$id", id), ("$session", sessionId), ("$from", from), ("$through", through), ("$decision", decisionId), ("$request", request), ("$now", now),
            ("$tainted", tainted ? 1 : 0), ("$success", successDecisionId), ("$calibrated", successCalibrated ? 1 : 0));
        return new OpportunityRow(id, sessionId, from, through, decisionId, request, OpportunityState.Queued, null, At(now), tainted,
            successDecisionId, successCalibrated);
    }

    private const string OpportunityColumns =
        "id, session, from_sequence, through_sequence, decision_id, request, state, detail, created, tainted, success_decision, success_calibrated";

    public IReadOnlyList<OpportunityRow> Opportunities(OpportunityState? state = null, int limit = 50) =>
        Query($"SELECT {OpportunityColumns} FROM opportunities " +
              (state is null ? "" : "WHERE state = $state ") + "ORDER BY created LIMIT $limit", ReadOpportunity,
            ("$state", state is { } value ? StateName(value) : null), ("$limit", limit));

    public OpportunityRow? FindOpportunity(string id) =>
        Query($"SELECT {OpportunityColumns} FROM opportunities WHERE id = $id", ReadOpportunity, ("$id", id)).FirstOrDefault();

    public void SetState(string id, OpportunityState state, string? detail = null) =>
        Execute("UPDATE opportunities SET state = $state, detail = $detail, updated = $now WHERE id = $id",
            ("$id", id), ("$state", StateName(state)), ("$detail", detail), ("$now", Now));

    private static OpportunityRow ReadOpportunity(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
        reader.GetInt64(3), reader.GetString(4), reader.GetString(5), ParseState(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7), At(reader.GetInt64(8)), reader.GetInt64(9) != 0,
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt64(11) != 0);

    private static string StateName(OpportunityState state) => state switch
    {
        OpportunityState.Queued => "queued",
        OpportunityState.Staged => "staged",
        OpportunityState.Applied => "applied",
        OpportunityState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static OpportunityState ParseState(string state) => state switch
    {
        "queued" => OpportunityState.Queued,
        "staged" => OpportunityState.Staged,
        "applied" => OpportunityState.Applied,
        _ => OpportunityState.Failed,
    };

    // ---- pending ----

    /// <param name="baseRevision">The skill's last revision when the draft was made, <c>""</c> for a new skill, null when unknown.</param>
    public PendingSkill Stage(string skill, string content, string sessionId, string decisionId, string reason, string replay, string? opportunityId,
        string? baseRevision = null)
    {
        var id = "sk-" + Guid.NewGuid().ToString("N")[..8];
        var now = Now;
        Execute("INSERT INTO pending VALUES ($id, $skill, $content, $session, $decision, $reason, $replay, $opportunity, 'pending', $now, $base)",
            ("$id", id), ("$skill", skill), ("$content", content), ("$session", sessionId), ("$decision", decisionId), ("$reason", reason),
            ("$replay", replay), ("$opportunity", opportunityId), ("$now", now), ("$base", baseRevision));
        return new PendingSkill(id, skill, content, sessionId, decisionId, reason, replay, opportunityId, PendingSkillStatus.Pending, At(now))
        {
            BaseRevision = baseRevision,
        };
    }

    public IReadOnlyList<PendingSkill> Pending() =>
        Query("SELECT id, skill, content, session, decision_id, reason, replay, opportunity, status, created, base_revision FROM pending WHERE status = 'pending' ORDER BY created",
            ReadPending);

    public PendingSkill? FindPending(string id) =>
        Query("SELECT id, skill, content, session, decision_id, reason, replay, opportunity, status, created, base_revision FROM pending WHERE id = $id",
            ReadPending, ("$id", id)).FirstOrDefault();

    /// <summary>Resolves a pending draft once; false when it was not pending.</summary>
    public bool Resolve(string id, PendingSkillStatus status) =>
        Execute("UPDATE pending SET status = $status WHERE id = $id AND status = 'pending'",
            ("$id", id), ("$status", status == PendingSkillStatus.Approved ? "approved" : "rejected")) == 1;

    private static PendingSkill ReadPending(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8) switch { "approved" => PendingSkillStatus.Approved, "rejected" => PendingSkillStatus.Rejected, _ => PendingSkillStatus.Pending },
        At(reader.GetInt64(9)))
    {
        BaseRevision = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    // ---- usage ----

    public void RecordUse(string skill) =>
        Execute("INSERT INTO usage (skill, last_used, uses) VALUES ($skill, $now, 1) ON CONFLICT(skill) DO UPDATE SET last_used = $now, uses = uses + 1",
            ("$skill", skill), ("$now", Now));

    public void SetPinned(string skill, bool pinned) =>
        Execute("INSERT INTO usage (skill, pinned) VALUES ($skill, $pinned) ON CONFLICT(skill) DO UPDATE SET pinned = $pinned",
            ("$skill", skill), ("$pinned", pinned ? 1 : 0));

    public SkillUsage Usage(string skill) =>
        Query("SELECT skill, last_used, uses, pinned FROM usage WHERE skill = $skill", reader => new SkillUsage(reader.GetString(0),
            reader.IsDBNull(1) ? null : At(reader.GetInt64(1)), reader.GetInt32(2), reader.GetInt32(3) != 0), ("$skill", skill)).FirstOrDefault()
        ?? new SkillUsage(skill, null, 0, false);

    // ---- evals ----

    public void RecordEval(EvalRecord record) =>
        Execute("INSERT INTO evals VALUES ($skill, $revision, $passed, $total, $detail, $at)",
            ("$skill", record.Skill), ("$revision", record.Revision), ("$passed", record.Passed), ("$total", record.Total),
            ("$detail", record.Detail), ("$at", record.At.UtcTicks));

    /// <summary>A skill's eval runs, newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<EvalRecord> Evals(string skill, int limit = 20) =>
        Query("SELECT skill, revision, passed, total, detail, at FROM evals WHERE skill = $skill ORDER BY at DESC LIMIT $limit",
            reader => new EvalRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
                At(reader.GetInt64(5))), ("$skill", skill), ("$limit", Math.Clamp(limit, 1, 500)));

    public EvalRecord? LastEval(string skill) =>
        Query("SELECT skill, revision, passed, total, detail, at FROM evals WHERE skill = $skill ORDER BY at DESC LIMIT 1",
            reader => new EvalRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
                At(reader.GetInt64(5))), ("$skill", skill)).FirstOrDefault();
}
