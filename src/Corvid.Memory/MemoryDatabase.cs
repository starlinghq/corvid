using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>
/// The memory store's own SQLite file (WAL, <c>synchronous=FULL</c>), separate from the event log. Each operation owns its
/// connection, as in the event log, so components built on one database can be used from different threads.
/// </summary>
/// <remarks>SQLite calls are synchronous. Call from a storage worker if the caller must not block.</remarks>
public sealed class MemoryDatabase
{
    internal const int SchemaVersion = 3;

    private readonly string _connectionString;

    public MemoryDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:")
            throw new ArgumentException("The memory store requires an on-disk database.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
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
            throw new InvalidDataException($"Unsupported memory database schema version {version}.");
        if (version == 0)
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }
        if (version < 2)
        {
            command.CommandText = LocalOnlyMigration;
            command.ExecuteNonQuery();
        }
        if (version < 3)
        {
            command.CommandText = LessonsMigration;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string Path { get; }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Version 2 (C-015, plan §10.4): facts gain a local-only flag that can be set but never cleared, and core blocks become
    // local-only. Nothing could reach a cloud model before version 2, so no block was shared on purpose: the migration marks
    // them all local-only, and the user opts a block in. The trigger is recreated so the flag is the one other column that
    // may change, and only from 0 to 1.
    internal const string LocalOnlyMigration = """
        ALTER TABLE facts ADD COLUMN local_only INTEGER NOT NULL DEFAULT 0 CHECK (local_only IN (0, 1));
        DROP TRIGGER facts_close_once;
        CREATE TRIGGER facts_close_once BEFORE UPDATE ON facts
            WHEN (old.valid_to IS NOT NULL AND (new.valid_to IS NOT old.valid_to OR new.invalidated_at IS NOT old.invalidated_at))
              OR new.id IS NOT old.id OR new.fact_id IS NOT old.fact_id OR new.text IS NOT old.text OR new.subject_id IS NOT old.subject_id
              OR new.predicate IS NOT old.predicate OR new.object_id IS NOT old.object_id
              OR new.valid_from IS NOT old.valid_from OR new.recorded_at IS NOT old.recorded_at
              OR new.supersedes IS NOT old.supersedes OR new.session_id IS NOT old.session_id
              OR new.source_sequence IS NOT old.source_sequence
              OR new.local_only < old.local_only
            BEGIN SELECT RAISE(ABORT, 'a fact changes only by closing its validity once, or by becoming local-only'); END;
        UPDATE core_blocks SET local_only = 1;
        PRAGMA user_version=2;
        """;

    // Version 3 (C-025): the lessons tier. A lesson's content (project, kind, title, applicability, body, tags, author, first
    // session, creation time) never changes; only its review state moves, candidate -> approved | rejected and approved ->
    // superseded, with superseded_by set exactly when superseded. While a lesson stays in one state only its decision, the
    // decider's proposal, the review reason and the set-only local-only flag may change. Nothing is deleted. Evidence is
    // append-only and unique per (lesson, session, content hash), so filing the same lesson again merges new evidence.
    // lesson_opportunities is the drafting queue lesson_opportunity fills at egress.
    internal const string LessonsMigration = """
        CREATE TABLE lessons (
            id INTEGER PRIMARY KEY,
            lesson_id TEXT NOT NULL UNIQUE,
            project_id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('workflow', 'correction', 'debugging_pattern', 'gotcha', 'convention')),
            title TEXT NOT NULL,
            applicability TEXT NOT NULL,
            body TEXT NOT NULL,
            tags TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('candidate', 'approved', 'rejected', 'superseded')),
            superseded_by TEXT REFERENCES lessons (lesson_id),
            author TEXT NOT NULL CHECK (author IN ('agent', 'user')),
            review_reason TEXT,
            decision_id TEXT,
            proposed TEXT,
            local_only INTEGER NOT NULL DEFAULT 0 CHECK (local_only IN (0, 1)),
            session_id TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            resolved_at INTEGER,
            CHECK ((state = 'superseded') = (superseded_by IS NOT NULL))
        );
        CREATE INDEX lessons_project ON lessons (project_id, state);
        CREATE VIRTUAL TABLE lessons_fts USING fts5(title, applicability, body, tags, content='lessons', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER lessons_fts_insert AFTER INSERT ON lessons
            BEGIN INSERT INTO lessons_fts (rowid, title, applicability, body, tags) VALUES (new.id, new.title, new.applicability, new.body, new.tags); END;
        CREATE TRIGGER lessons_insert_candidate BEFORE INSERT ON lessons
            WHEN new.state IS NOT 'candidate' OR new.superseded_by IS NOT NULL
            BEGIN SELECT RAISE(ABORT, 'a lesson is filed as a candidate'); END;
        CREATE TRIGGER lessons_no_delete BEFORE DELETE ON lessons
            BEGIN SELECT RAISE(ABORT, 'lessons are rejected or superseded, never deleted'); END;
        CREATE TRIGGER lessons_transition BEFORE UPDATE ON lessons
            WHEN new.id IS NOT old.id OR new.lesson_id IS NOT old.lesson_id OR new.project_id IS NOT old.project_id
              OR new.kind IS NOT old.kind OR new.title IS NOT old.title OR new.applicability IS NOT old.applicability
              OR new.body IS NOT old.body OR new.tags IS NOT old.tags OR new.author IS NOT old.author
              OR new.session_id IS NOT old.session_id OR new.created_at IS NOT old.created_at
              OR new.local_only < old.local_only
              OR NOT ((new.state IS old.state AND new.superseded_by IS old.superseded_by AND new.resolved_at IS old.resolved_at)
                   OR (old.state = 'candidate' AND new.state IN ('approved', 'rejected') AND new.superseded_by IS NULL)
                   OR (old.state = 'approved' AND new.state = 'superseded' AND new.superseded_by IS NOT NULL
                       AND new.superseded_by IS NOT old.lesson_id))
            BEGIN SELECT RAISE(ABORT, 'a lesson changes only from candidate to approved or rejected, or from approved to superseded'); END;

        CREATE TABLE lesson_evidence (
            lesson_id TEXT NOT NULL REFERENCES lessons (lesson_id),
            session_id TEXT NOT NULL,
            positions TEXT NOT NULL,
            summary TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            recorded_at INTEGER NOT NULL,
            PRIMARY KEY (lesson_id, session_id, content_hash)
        );
        CREATE TRIGGER lesson_evidence_no_update BEFORE UPDATE ON lesson_evidence
            BEGIN SELECT RAISE(ABORT, 'lesson evidence is append-only'); END;
        CREATE TRIGGER lesson_evidence_no_delete BEFORE DELETE ON lesson_evidence
            BEGIN SELECT RAISE(ABORT, 'lesson evidence is append-only'); END;

        CREATE TABLE lesson_opportunities (
            opportunity_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            from_sequence INTEGER NOT NULL,
            through_sequence INTEGER NOT NULL,
            decision_id TEXT NOT NULL,
            request TEXT NOT NULL,
            tainted INTEGER NOT NULL CHECK (tainted IN (0, 1)),
            local_only INTEGER NOT NULL CHECK (local_only IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('queued', 'filed', 'approved', 'duplicate', 'declined', 'failed')),
            detail TEXT,
            lesson_id TEXT,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );
        CREATE INDEX lesson_opportunities_state ON lesson_opportunities (state, created_at);
        PRAGMA user_version=3;
        """;

    // Times are UTC ticks so comparisons are numeric. FTS rows are keyed by an explicit INTEGER PRIMARY KEY, because VACUUM
    // may renumber an implicit rowid and desynchronise an external-content index. FTS tables use the trigram tokenizer: it
    // indexes every three-character sequence, so text without word breaks (Chinese, Japanese) is searchable; terms shorter
    // than three characters fall back to LIKE (see FtsQuery).
    internal const string Schema = """
        CREATE TABLE core_blocks (
            name TEXT PRIMARY KEY,
            content TEXT NOT NULL,
            budget_tokens INTEGER NOT NULL CHECK (budget_tokens > 0),
            local_only INTEGER NOT NULL CHECK (local_only IN (0, 1)),
            revision INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );

        CREATE TABLE episodes (
            id INTEGER PRIMARY KEY,
            item_id TEXT NOT NULL UNIQUE,
            session_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            kind TEXT NOT NULL,
            recorded_at INTEGER NOT NULL,
            text TEXT NOT NULL,
            UNIQUE (session_id, sequence)
        );
        CREATE INDEX episodes_recorded ON episodes (recorded_at);
        CREATE VIRTUAL TABLE episodes_fts USING fts5(text, content='episodes', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER episodes_fts_insert AFTER INSERT ON episodes
            BEGIN INSERT INTO episodes_fts (rowid, text) VALUES (new.id, new.text); END;
        CREATE TRIGGER episodes_no_update BEFORE UPDATE ON episodes
            BEGIN SELECT RAISE(ABORT, 'episodes mirror the append-only event log'); END;
        CREATE TRIGGER episodes_no_delete BEFORE DELETE ON episodes
            BEGIN SELECT RAISE(ABORT, 'episodes mirror the append-only event log'); END;
        CREATE TABLE episode_index_state (
            session_id TEXT PRIMARY KEY,
            through_sequence INTEGER NOT NULL
        );

        CREATE TABLE entities (
            entity_id TEXT PRIMARY KEY,
            label TEXT NOT NULL,
            name TEXT NOT NULL,
            key TEXT NOT NULL,
            recorded_at INTEGER NOT NULL,
            UNIQUE (label, key)
        );

        CREATE TABLE facts (
            id INTEGER PRIMARY KEY,
            fact_id TEXT NOT NULL UNIQUE,
            text TEXT NOT NULL,
            subject_id TEXT REFERENCES entities (entity_id),
            predicate TEXT,
            object_id TEXT REFERENCES entities (entity_id),
            valid_from INTEGER NOT NULL,
            valid_to INTEGER,
            recorded_at INTEGER NOT NULL,
            invalidated_at INTEGER,
            supersedes TEXT REFERENCES facts (fact_id),
            session_id TEXT,
            source_sequence INTEGER,
            CHECK ((valid_to IS NULL) = (invalidated_at IS NULL)),
            CHECK (valid_to IS NULL OR valid_to >= valid_from),
            CHECK ((subject_id IS NULL) = (predicate IS NULL) AND (predicate IS NULL) = (object_id IS NULL))
        );
        CREATE INDEX facts_valid ON facts (valid_to, valid_from);
        CREATE TABLE fact_entities (
            fact_id TEXT NOT NULL REFERENCES facts (fact_id),
            entity_id TEXT NOT NULL REFERENCES entities (entity_id),
            PRIMARY KEY (fact_id, entity_id)
        );
        CREATE INDEX fact_entities_entity ON fact_entities (entity_id);
        CREATE VIRTUAL TABLE facts_fts USING fts5(text, content='facts', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER facts_fts_insert AFTER INSERT ON facts
            BEGIN INSERT INTO facts_fts (rowid, text) VALUES (new.id, new.text); END;
        -- Bi-temporal history: a fact is never deleted, and the only change is closing its validity once.
        CREATE TRIGGER facts_no_delete BEFORE DELETE ON facts
            BEGIN SELECT RAISE(ABORT, 'facts are invalidated, never deleted'); END;
        CREATE TRIGGER facts_close_once BEFORE UPDATE ON facts
            WHEN old.valid_to IS NOT NULL
              OR new.id IS NOT old.id OR new.fact_id IS NOT old.fact_id OR new.text IS NOT old.text OR new.subject_id IS NOT old.subject_id
              OR new.predicate IS NOT old.predicate OR new.object_id IS NOT old.object_id
              OR new.valid_from IS NOT old.valid_from OR new.recorded_at IS NOT old.recorded_at
              OR new.supersedes IS NOT old.supersedes OR new.session_id IS NOT old.session_id
              OR new.source_sequence IS NOT old.source_sequence
            BEGIN SELECT RAISE(ABORT, 'a fact changes only by closing its validity, once'); END;
        CREATE TRIGGER fact_entities_no_delete BEFORE DELETE ON fact_entities
            BEGIN SELECT RAISE(ABORT, 'facts are invalidated, never deleted'); END;

        CREATE TABLE vectors (
            item_id TEXT NOT NULL,
            embedder_id TEXT NOT NULL,
            dimensions INTEGER NOT NULL CHECK (dimensions > 0),
            vector BLOB NOT NULL,
            PRIMARY KEY (item_id, embedder_id)
        );
        CREATE INDEX vectors_embedder ON vectors (embedder_id);

        CREATE TABLE embedder_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            active TEXT,
            pending TEXT,
            CHECK (pending IS NULL OR pending IS NOT active)
        );
        INSERT INTO embedder_state (id, active, pending) VALUES (1, NULL, NULL);

        CREATE TABLE pending_writes (
            pending_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            proposal TEXT NOT NULL,
            decision_id TEXT,
            reason TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'rejected')),
            resolved_at INTEGER,
            resolution TEXT
        );
        CREATE INDEX pending_writes_status ON pending_writes (status, created_at);

        PRAGMA user_version=1;
        """;
}
