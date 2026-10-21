using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>Lesson kinds (C-025, after Beacon's <c>beacon.learning.v1</c>). Persisted: renaming one needs a migration.</summary>
public static class LessonKinds
{
    /// <summary>A procedure with a required order.</summary>
    public const string Workflow = "workflow";
    /// <summary>The user corrected the agent, and the correction generalises.</summary>
    public const string Correction = "correction";
    /// <summary>A symptom that points to a cause that is not obvious.</summary>
    public const string DebuggingPattern = "debugging_pattern";
    /// <summary>A trap with no error message, or a misleading one.</summary>
    public const string Gotcha = "gotcha";
    /// <summary>A project rule the trace shows, not a personal preference.</summary>
    public const string Convention = "convention";

    public static ImmutableArray<string> All { get; } = [Workflow, Correction, DebuggingPattern, Gotcha, Convention];

    public static bool IsKind(string? value) => value is not null && All.Contains(value);
}

/// <summary>A lesson's review state. Persisted: renaming one needs a migration.</summary>
public static class LessonStates
{
    /// <summary>Filed and waiting for review in <c>/memory lessons pending</c>.</summary>
    public const string Candidate = "candidate";
    /// <summary>Recalled for its project.</summary>
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    /// <summary>Replaced by a newer approved lesson (<see cref="Lesson.SupersededBy"/>); never recalled.</summary>
    public const string Superseded = "superseded";
}

/// <summary>Who wrote a lesson.</summary>
public static class LessonAuthors
{
    public const string Agent = "agent";
    public const string User = "user";
}

/// <summary>Where a lesson comes from: events of one session, and a hash of their persisted form.</summary>
/// <param name="Positions">The cited event sequences, ascending.</param>
/// <param name="ContentHash">Lowercase hex SHA-256 over the cited events' persisted JSON, so the evidence can be re-checked against the log.</param>
public sealed record LessonEvidence(string SessionId, ImmutableArray<long> Positions, string Summary, string ContentHash);

/// <summary>
/// A lesson (C-025): something a future session in the same project should do differently or know, between a fact
/// (declarative) and a skill (a procedure). Content is fixed once filed; only the state moves.
/// </summary>
/// <param name="SessionId">The session that first filed it, where its <c>memory_op</c> decision and events are recorded.</param>
/// <param name="Proposed">The <c>memory_op</c> option the decider chose (add, update, noop), when it answered.</param>
public sealed record Lesson(
    string LessonId,
    string ProjectId,
    string Kind,
    string Title,
    string Applicability,
    string Body,
    ImmutableArray<string> Tags,
    string State,
    string? SupersededBy,
    string Author,
    string? ReviewReason,
    string? DecisionId,
    bool LocalOnly,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    ImmutableArray<LessonEvidence> Evidence,
    string SessionId,
    string? Proposed = null)
{
    public bool IsApproved => State == LessonStates.Approved;

    /// <summary>How recall renders it into a prompt.</summary>
    public string Text => $"Lesson ({Kind}): {Title} — {Applicability}\n{Body}";

    public static bool IsLessonItem(string itemId) => itemId.StartsWith(IdPrefix, StringComparison.Ordinal);

    internal const string IdPrefix = "lesson:";

    /// <summary>
    /// <c>lesson:</c> and the SHA-256 of project, kind, title and body (whitespace collapsed, lowercased), so filing the same
    /// lesson twice names the same row. Evidence is left out: a second sighting adds evidence, not a lesson.
    /// </summary>
    public static string IdFor(string projectId, string kind, string title, string body)
    {
        var key = string.Join('\0', projectId, kind, Normalize(title), Normalize(body));
        return IdPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    internal static string Normalize(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <param name="Rank">Lower is better (BM25), or 0 for a LIKE match.</param>
public sealed record LessonHit(Lesson Lesson, double Rank);

/// <summary>What <see cref="LessonStore.File"/> did.</summary>
/// <param name="Inserted">False when the lesson already existed; its state is unchanged.</param>
/// <param name="EvidenceAdded">Whether the evidence was new for the lesson.</param>
public sealed record LessonFiling(Lesson Lesson, bool Inserted, bool EvidenceAdded);

/// <summary>A turn <c>lesson_opportunity</c> queued for drafting, and how far it got.</summary>
public sealed record LessonOpportunityRow(
    string OpportunityId,
    string SessionId,
    string ProjectId,
    long FromSequence,
    long ThroughSequence,
    string DecisionId,
    string Request,
    bool Tainted,
    bool LocalOnly,
    string State,
    string? Detail,
    string? LessonId,
    DateTimeOffset CreatedAt);

/// <summary>Opportunity states. Persisted.</summary>
public static class LessonOpportunityStates
{
    public const string Queued = "queued";
    public const string Filed = "filed";
    public const string Approved = "approved";
    public const string Duplicate = "duplicate";
    public const string Declined = "declined";
    public const string Failed = "failed";
}

/// <summary>
/// The lessons tier (C-025) in the memory database: lessons with their review state, append-only evidence, a trigram
/// FTS index for recall, and the drafting queue. Triggers keep content fixed and allow only candidate → approved or
/// rejected and approved → superseded.
/// </summary>
public sealed class LessonStore
{
    private readonly MemoryDatabase _database;
    private readonly TimeProvider _time;

    public LessonStore(MemoryDatabase database, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Files a lesson as a candidate, or adds its evidence to the lesson already filed under the same id (whatever that
    /// lesson's state: a rejected or superseded lesson is never proposed again).
    /// </summary>
    public LessonFiling File(Lesson lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var id = Lesson.IdFor(lesson.ProjectId, lesson.Kind, lesson.Title, lesson.Body);
        if (lesson.LessonId != id)
            throw new ArgumentException("The lesson id is not the hash of its content.", nameof(lesson));
        var now = _time.GetUtcNow();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        bool inserted, evidenceAdded = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO lessons (lesson_id, project_id, kind, title, applicability, body, tags, state, author, review_reason,
                    local_only, session_id, created_at)
                VALUES ($id, $project, $kind, $title, $applicability, $body, $tags, 'candidate', $author, $reason, $local, $session, $at)
                ON CONFLICT (lesson_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$project", lesson.ProjectId);
            command.Parameters.AddWithValue("$kind", lesson.Kind);
            command.Parameters.AddWithValue("$title", lesson.Title);
            command.Parameters.AddWithValue("$applicability", lesson.Applicability);
            command.Parameters.AddWithValue("$body", lesson.Body);
            command.Parameters.AddWithValue("$tags", string.Join(' ', lesson.Tags));
            command.Parameters.AddWithValue("$author", lesson.Author);
            command.Parameters.AddWithValue("$reason", (object?)lesson.ReviewReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$local", lesson.LocalOnly ? 1 : 0);
            command.Parameters.AddWithValue("$session", lesson.SessionId);
            command.Parameters.AddWithValue("$at", Ticks.From(now));
            inserted = command.ExecuteNonQuery() == 1;
        }
        foreach (var evidence in lesson.Evidence.IsDefault ? [] : lesson.Evidence)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO lesson_evidence (lesson_id, session_id, positions, summary, content_hash, recorded_at)
                VALUES ($id, $session, $positions, $summary, $hash, $at)
                ON CONFLICT (lesson_id, session_id, content_hash) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$session", evidence.SessionId);
            command.Parameters.AddWithValue("$positions", string.Join(',', evidence.Positions.Select(p => p.ToString(CultureInfo.InvariantCulture))));
            command.Parameters.AddWithValue("$summary", evidence.Summary);
            command.Parameters.AddWithValue("$hash", evidence.ContentHash);
            command.Parameters.AddWithValue("$at", Ticks.From(now));
            evidenceAdded |= command.ExecuteNonQuery() == 1;
        }
        // A lesson filed again from a local-only turn becomes local-only; the flag is never cleared.
        if (!inserted && lesson.LocalOnly)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE lessons SET local_only = 1 WHERE lesson_id = $id AND local_only = 0;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return new LessonFiling(Read(connection, "l.lesson_id = $id", ("$id", id)).Single(), inserted, evidenceAdded);
    }

    public Lesson? Get(string lessonId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lessonId);
        using var connection = _database.Open();
        return Read(connection, "l.lesson_id = $id", ("$id", lessonId)).SingleOrDefault();
    }

    /// <summary>
    /// The projects with at least one approved lesson (C-028): what an MCP caller's project argument may map onto. Cheap (the
    /// <c>lessons_project</c> index), and read on every call so a lesson approved in a running session counts at once.
    /// </summary>
    public IReadOnlySet<string> KnownProjects()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT project_id FROM lessons WHERE state = 'approved';";
        using var reader = command.ExecuteReader();
        var projects = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            projects.Add(reader.GetString(0));
        return projects;
    }

    /// <summary>A project's lessons, oldest first; all states when <paramref name="state"/> is null.</summary>
    public IReadOnlyList<Lesson> List(string projectId, string? state = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _database.Open();
        return state is null
            ? Read(connection, "l.project_id = $project", ("$project", projectId))
            : Read(connection, "l.project_id = $project AND l.state = $state", ("$project", projectId), ("$state", state));
    }

    /// <summary>Records the gate's verdict on a candidate that stays a candidate: its decision, proposal and reason.</summary>
    public bool Annotate(string lessonId, string? decisionId, string? proposed, string reason)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE lessons SET decision_id = $decision, proposed = $proposed, review_reason = $reason
            WHERE lesson_id = $id AND state = 'candidate';
            """;
        command.Parameters.AddWithValue("$decision", (object?)decisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$proposed", (object?)proposed ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", lessonId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Approves a candidate, and in the same transaction supersedes <paramref name="supersedes"/>, which must be an approved
    /// lesson of the same project. False when the candidate is no longer a candidate.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="supersedes"/> is not an approved lesson of the same project.</exception>
    public bool Approve(string lessonId, string reason, string? supersedes = null, string? decisionId = null, string? proposed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lessonId);
        var now = Ticks.From(_time.GetUtcNow());
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE lessons SET state = 'approved', review_reason = $reason, resolved_at = $at,
                    decision_id = coalesce($decision, decision_id), proposed = coalesce($proposed, proposed)
                WHERE lesson_id = $id AND state = 'candidate';
                """;
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$at", now);
            command.Parameters.AddWithValue("$decision", (object?)decisionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$proposed", (object?)proposed ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", lessonId);
            if (command.ExecuteNonQuery() != 1)
                return false;
        }
        if (supersedes is not null)
            SupersedeIn(connection, transaction, supersedes, lessonId, now);
        transaction.Commit();
        return true;
    }

    /// <summary>Rejects a candidate; false when it is no longer a candidate.</summary>
    public bool Reject(string lessonId, string reason, string? decisionId = null, string? proposed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lessonId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE lessons SET state = 'rejected', review_reason = $reason, resolved_at = $at,
                decision_id = coalesce($decision, decision_id), proposed = coalesce($proposed, proposed)
            WHERE lesson_id = $id AND state = 'candidate';
            """;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$at", Ticks.From(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$decision", (object?)decisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$proposed", (object?)proposed ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", lessonId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Marks an approved lesson superseded by another approved lesson of the same project.</summary>
    /// <exception cref="InvalidOperationException">Either lesson is not approved, they differ in project, or they are the same.</exception>
    public void Supersede(string oldLessonId, string newLessonId)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var replacement = Read(connection, "l.lesson_id = $id", ("$id", newLessonId)).SingleOrDefault();
        if (replacement is not { State: LessonStates.Approved })
            throw new InvalidOperationException($"'{newLessonId}' is not an approved lesson.");
        SupersedeIn(connection, transaction, oldLessonId, newLessonId, Ticks.From(_time.GetUtcNow()));
        transaction.Commit();
    }

    private static void SupersedeIn(SqliteConnection connection, SqliteTransaction transaction, string oldLessonId, string newLessonId, long now)
    {
        if (oldLessonId == newLessonId)
            throw new InvalidOperationException("A lesson cannot supersede itself.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE lessons SET state = 'superseded', superseded_by = $new, resolved_at = $at
            WHERE lesson_id = $old AND state = 'approved'
              AND project_id = (SELECT project_id FROM lessons WHERE lesson_id = $new);
            """;
        command.Parameters.AddWithValue("$new", newLessonId);
        command.Parameters.AddWithValue("$old", oldLessonId);
        command.Parameters.AddWithValue("$at", now);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"'{oldLessonId}' is not an approved lesson of the same project.");
    }

    /// <summary>Full-text search over a project's approved lessons, ranked by BM25 (LIKE for short terms only).</summary>
    public IReadOnlyList<LessonHit> SearchApproved(string projectId, string query, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var (match, shortTerms) = FtsQuery.Build(query);
        if (match is null && shortTerms.Count == 0)
            return [];
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        if (match is not null)
        {
            command.CommandText = """
                SELECT l.lesson_id, bm25(lessons_fts) AS rank FROM lessons_fts JOIN lessons l ON l.id = lessons_fts.rowid
                WHERE lessons_fts MATCH $match AND l.state = 'approved' AND l.project_id = $project
                ORDER BY rank, l.created_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$match", match);
        }
        else
        {
            var likes = string.Join(" OR ", shortTerms.Select((_, i) =>
                $"(l.title LIKE $t{i} ESCAPE '\\' OR l.applicability LIKE $t{i} ESCAPE '\\' OR l.body LIKE $t{i} ESCAPE '\\' OR l.tags LIKE $t{i} ESCAPE '\\')"));
            command.CommandText = $"""
                SELECT l.lesson_id, 0.0 AS rank FROM lessons l WHERE ({likes}) AND l.state = 'approved' AND l.project_id = $project
                ORDER BY l.created_at DESC, l.id DESC LIMIT $limit;
                """;
            for (var i = 0; i < shortTerms.Count; i++)
                command.Parameters.AddWithValue($"$t{i}", FtsQuery.LikePattern(shortTerms[i]));
        }
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<(string Id, double Rank)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetDouble(1)));
        return [.. rows.Select(row => new LessonHit(Read(connection, "l.lesson_id = $id", ("$id", row.Id)).Single(), row.Rank))];
    }

    // ---- the drafting queue ----

    public LessonOpportunityRow Enqueue(string sessionId, string projectId, long from, long through, string decisionId, string request,
        bool tainted, bool localOnly)
    {
        var row = new LessonOpportunityRow(Guid.NewGuid().ToString("N")[..12], sessionId, projectId, from, through, decisionId, request,
            tainted, localOnly, LessonOpportunityStates.Queued, null, null, _time.GetUtcNow());
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lesson_opportunities (opportunity_id, session_id, project_id, from_sequence, through_sequence, decision_id,
                request, tainted, local_only, state, created_at, updated_at)
            VALUES ($id, $session, $project, $from, $through, $decision, $request, $tainted, $local, 'queued', $at, $at);
            """;
        command.Parameters.AddWithValue("$id", row.OpportunityId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$through", through);
        command.Parameters.AddWithValue("$decision", decisionId);
        command.Parameters.AddWithValue("$request", request);
        command.Parameters.AddWithValue("$tainted", tainted ? 1 : 0);
        command.Parameters.AddWithValue("$local", localOnly ? 1 : 0);
        command.Parameters.AddWithValue("$at", Ticks.From(row.CreatedAt));
        command.ExecuteNonQuery();
        return row;
    }

    public IReadOnlyList<LessonOpportunityRow> Opportunities(string? state = null, int limit = 50)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT opportunity_id, session_id, project_id, from_sequence, through_sequence, decision_id, request, tainted, local_only,
                state, detail, lesson_id, created_at
            FROM lesson_opportunities WHERE $state IS NULL OR state = $state ORDER BY created_at, opportunity_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<LessonOpportunityRow>();
        while (reader.Read())
            rows.Add(new LessonOpportunityRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7) == 1, reader.GetInt64(8) == 1,
                reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                Ticks.To(reader.GetInt64(12))));
        return rows;
    }

    public void SetOpportunityState(string opportunityId, string state, string? detail, string? lessonId = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE lesson_opportunities SET state = $state, detail = $detail, lesson_id = coalesce($lesson, lesson_id), updated_at = $at
            WHERE opportunity_id = $id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$lesson", (object?)lessonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", Ticks.From(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$id", opportunityId);
        command.ExecuteNonQuery();
    }

    // ---- reading ----

    private const string Columns = """
        l.lesson_id, l.project_id, l.kind, l.title, l.applicability, l.body, l.tags, l.state, l.superseded_by, l.author,
        l.review_reason, l.decision_id, l.local_only, l.created_at, l.resolved_at, l.session_id, l.proposed
        """;

    private static List<Lesson> Read(SqliteConnection connection, string where, params (string Name, object Value)[] parameters)
    {
        var lessons = new List<Lesson>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {Columns} FROM lessons l WHERE {where} ORDER BY l.created_at, l.id;";
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                lessons.Add(new Lesson(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), [.. reader.GetString(6).Split(' ', StringSplitOptions.RemoveEmptyEntries)], reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetInt64(12) == 1, Ticks.To(reader.GetInt64(13)),
                    Ticks.ToNullable(reader, 14), [], reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16)));
        }
        return [.. lessons.Select(lesson => lesson with { Evidence = Evidence(connection, lesson.LessonId) })];
    }

    private static ImmutableArray<LessonEvidence> Evidence(SqliteConnection connection, string lessonId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, positions, summary, content_hash FROM lesson_evidence WHERE lesson_id = $id ORDER BY recorded_at, rowid;
            """;
        command.Parameters.AddWithValue("$id", lessonId);
        using var reader = command.ExecuteReader();
        var evidence = ImmutableArray.CreateBuilder<LessonEvidence>();
        while (reader.Read())
            evidence.Add(new LessonEvidence(reader.GetString(0),
                [.. reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => long.Parse(p, CultureInfo.InvariantCulture))],
                reader.GetString(2), reader.GetString(3)));
        return evidence.ToImmutable();
    }
}
