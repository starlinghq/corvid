using Microsoft.Data.Sqlite;
using static Corvid.Memory.Tests.LessonFixtures;

namespace Corvid.Memory.Tests;

public sealed class LessonStoreTests
{
    [Fact]
    public void Filing_the_same_lesson_twice_inserts_once_and_merges_new_evidence()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var first = lessons.File(Candidate(Content(), Project));
        Assert.True(first.Inserted);
        Assert.Equal(LessonStates.Candidate, first.Lesson.State);
        Assert.StartsWith("lesson:", first.Lesson.LessonId, StringComparison.Ordinal);

        // Same content from another session, with whitespace and case differences in the title: the same lesson.
        var again = Content(title: "Build the hooks  binary before running the CLI tests", evidence: [Evidence("s-2", new string('a', 64), 3, 4)]);
        var second = lessons.File(Candidate(again, Project, "s-2"));
        Assert.False(second.Inserted);
        Assert.True(second.EvidenceAdded);
        Assert.Equal(first.Lesson.LessonId, second.Lesson.LessonId);
        Assert.Equal(["s-1", "s-2"], second.Lesson.Evidence.Select(e => e.SessionId));
        Assert.Equal([3L, 4L], second.Lesson.Evidence[1].Positions);
        // The first filing's title and session stay.
        Assert.Equal("Build the hooks binary before running the cli tests", second.Lesson.Title);
        Assert.Equal("s-1", second.Lesson.SessionId);

        var third = lessons.File(Candidate(again, Project, "s-2"));
        Assert.False(third.EvidenceAdded);
        Assert.Equal(2, third.Lesson.Evidence.Length);
        Assert.Single(lessons.List(Project));
    }

    [Fact]
    public void The_same_lesson_in_another_project_is_another_lesson()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var a = lessons.File(Candidate(Content(), Project)).Lesson;
        var b = lessons.File(Candidate(Content(), OtherProject)).Lesson;
        Assert.NotEqual(a.LessonId, b.LessonId);
        Assert.Single(lessons.List(Project));
    }

    [Fact]
    public void A_rejected_lesson_filed_again_stays_rejected()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var lesson = lessons.File(Candidate(Content(), Project)).Lesson;
        Assert.True(lessons.Reject(lesson.LessonId, "not useful"));
        var again = lessons.File(Candidate(Content(evidence: [Evidence("s-9", new string('b', 64))]), Project, "s-9"));
        Assert.False(again.Inserted);
        Assert.Equal(LessonStates.Rejected, again.Lesson.State);
        Assert.Empty(lessons.List(Project, LessonStates.Candidate));
    }

    [Fact]
    public void Approve_and_supersede_move_through_the_allowed_states_only()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var old = lessons.File(Candidate(Content(), Project)).Lesson;
        Assert.True(lessons.Approve(old.LessonId, "ok"));
        Assert.False(lessons.Approve(old.LessonId, "again"));
        Assert.False(lessons.Reject(old.LessonId, "late"));

        var replacement = lessons.File(Candidate(Content(title: "Build the hooks binary with make before go test"), Project)).Lesson;
        Assert.True(lessons.Approve(replacement.LessonId, "ok", supersedes: old.LessonId));
        var superseded = lessons.Get(old.LessonId)!;
        Assert.Equal((LessonStates.Superseded, replacement.LessonId), (superseded.State, superseded.SupersededBy));
        Assert.Throws<InvalidOperationException>(() => lessons.Supersede(old.LessonId, replacement.LessonId));

        // Another project's lesson cannot be superseded by this one.
        var foreign = lessons.File(Candidate(Content(), OtherProject)).Lesson;
        lessons.Approve(foreign.LessonId, "ok");
        Assert.Throws<InvalidOperationException>(() => lessons.Supersede(foreign.LessonId, replacement.LessonId));
        Assert.Throws<InvalidOperationException>(() => lessons.Supersede(replacement.LessonId, replacement.LessonId));
    }

    [Fact]
    public void Triggers_refuse_illegal_changes_and_deletes()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var lesson = lessons.File(Candidate(Content(), Project)).Lesson;
        void Exec(string sql)
        {
            using var connection = store.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", lesson.LessonId);
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET title = 'Rewrite it' WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET body = 'x' WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET project_id = 'p' WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET state = 'superseded', superseded_by = $id WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("DELETE FROM lessons WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("DELETE FROM lesson_evidence WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("UPDATE lesson_evidence SET summary = 'x' WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("""
            INSERT INTO lessons (lesson_id, project_id, kind, title, applicability, body, tags, state, author, session_id, created_at)
            VALUES ('lesson:x', 'p', 'gotcha', 't', 'when x', 'b', 'a b', 'approved', 'agent', 's', 0)
            """));

        Exec("UPDATE lessons SET state = 'rejected' WHERE lesson_id = $id");
        // Rejected is final.
        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET state = 'approved' WHERE lesson_id = $id"));
        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET state = 'candidate' WHERE lesson_id = $id"));
        // The local-only flag can be set but never cleared.
        Exec("UPDATE lessons SET local_only = 1 WHERE lesson_id = $id");
        Assert.Throws<SqliteException>(() => Exec("UPDATE lessons SET local_only = 0 WHERE lesson_id = $id"));
    }

    [Fact]
    public void Search_returns_approved_lessons_of_the_project_only()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var approved = lessons.File(Candidate(Content(), Project)).Lesson;
        lessons.Approve(approved.LessonId, "ok");
        lessons.File(Candidate(Content(title: "Build the hooks binary with make in a candidate"), Project));
        var foreign = lessons.File(Candidate(Content(), OtherProject)).Lesson;
        lessons.Approve(foreign.LessonId, "ok");

        var hits = lessons.SearchApproved(Project, "hooks binary build");
        Assert.Equal([approved.LessonId], hits.Select(hit => hit.Lesson.LessonId));
        // Short terms fall back to LIKE, still within the project.
        Assert.Equal([approved.LessonId], lessons.SearchApproved(Project, "go").Select(hit => hit.Lesson.LessonId));
        Assert.Empty(lessons.SearchApproved(Project, "kubernetes"));
    }

    [Fact]
    public void The_drafting_queue_keeps_its_rows_and_states()
    {
        using var store = new TempStore();
        var lessons = new LessonStore(store.Database, store.Time);
        var row = lessons.Enqueue("s-1", Project, 3, 9, "d-1", "fix the build", tainted: true, localOnly: false);
        var queued = Assert.Single(lessons.Opportunities(LessonOpportunityStates.Queued));
        Assert.Equal((row.OpportunityId, 3L, 9L, true), (queued.OpportunityId, queued.FromSequence, queued.ThroughSequence, queued.Tainted));
        lessons.SetOpportunityState(row.OpportunityId, LessonOpportunityStates.Filed, "staged", "lesson:x");
        Assert.Empty(lessons.Opportunities(LessonOpportunityStates.Queued));
        Assert.Equal("lesson:x", Assert.Single(lessons.Opportunities()).LessonId);
    }

    [Fact]
    public void A_version_2_store_is_migrated_to_version_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "memory.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = MemoryDatabase.Schema + MemoryDatabase.LocalOnlyMigration + """
                    INSERT INTO facts (fact_id, text, valid_from, recorded_at) VALUES ('fact:old', 'Sam likes tea', 0, 0);
                    """;
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(2L, command.ExecuteScalar());
            }

            var database = new MemoryDatabase(path);
            Assert.Equal("Sam likes tea", new FactGraph(database).Get("fact:old")!.Text);
            var lessons = new LessonStore(database);
            Assert.True(lessons.File(Candidate(Content(), Project)).Inserted);
            using (var connection = database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(3L, command.ExecuteScalar());
            }
            // Reopening a version 3 store migrates nothing.
            Assert.Single(new LessonStore(new MemoryDatabase(path)).List(Project));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
