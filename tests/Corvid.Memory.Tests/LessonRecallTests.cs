using static Corvid.Memory.Tests.Fixtures;
using static Corvid.Memory.Tests.LessonFixtures;

namespace Corvid.Memory.Tests;

public sealed class LessonRecallTests
{
    private static (HybridRetriever Retriever, LessonStore Lessons) Build(TempStore store)
    {
        var lessons = new LessonStore(store.Database, store.Time);
        return (new HybridRetriever(store.Episodes, store.Facts, time: store.Time, lessons: lessons), lessons);
    }

    private static Lesson Approved(LessonStore lessons, LessonContent content, string project, bool localOnly = false)
    {
        var lesson = lessons.File(Candidate(content, project, localOnly: localOnly)).Lesson;
        Assert.True(lessons.Approve(lesson.LessonId, "ok"));
        return lessons.Get(lesson.LessonId)!;
    }

    [Fact]
    public async Task Approved_lessons_of_the_project_join_recall_beside_facts()
    {
        using var store = new TempStore();
        var (retriever, lessons) = Build(store);
        var lesson = Approved(lessons, Content(), Project);
        store.Facts.Add(new FactInput("The hooks binary lives in cli/beacon"));

        var result = await retriever.RetrieveAsync(new RetrievalRequest("hooks binary", 1_000, ProjectId: Project), Ct);
        var item = Assert.Single(result.Items, i => i.Kind == MemoryItemKind.Lesson);
        Assert.Equal(lesson.LessonId, item.ItemId);
        Assert.StartsWith("Lesson (workflow): Build the hooks binary before running the cli tests — when running go test", item.Text, StringComparison.Ordinal);
        Assert.Contains(result.Items, i => i.Kind == MemoryItemKind.Fact);
        Assert.Equal(RetrievalSources.Bm25, item.Sources);
    }

    [Fact]
    public async Task No_project_or_no_store_means_no_lessons()
    {
        using var store = new TempStore();
        var (retriever, lessons) = Build(store);
        Approved(lessons, Content(), Project);
        Assert.DoesNotContain((await retriever.RetrieveAsync(new RetrievalRequest("hooks binary", 1_000), Ct)).Items,
            i => i.Kind == MemoryItemKind.Lesson);
        Assert.Empty((await store.Retriever().RetrieveAsync(new RetrievalRequest("hooks binary", 1_000, ProjectId: Project), Ct)).Items);
        // A request for facts only leaves lessons out, even with a project.
        Assert.Empty((await retriever.RetrieveAsync(new RetrievalRequest("hooks binary", 1_000, Kinds: [MemoryItemKind.Fact], ProjectId: Project), Ct)).Items);
    }

    [Fact]
    public async Task Other_projects_candidates_rejected_and_superseded_lessons_are_never_returned()
    {
        using var store = new TempStore();
        var (retriever, lessons) = Build(store);
        Approved(lessons, Content(), OtherProject);
        lessons.File(Candidate(Content(kind: LessonKinds.Gotcha), Project));
        var rejected = lessons.File(Candidate(Content(kind: LessonKinds.Convention), Project)).Lesson;
        lessons.Reject(rejected.LessonId, "no");
        var old = Approved(lessons, Content(kind: LessonKinds.Correction), Project);
        var current = lessons.File(Candidate(Content(kind: LessonKinds.DebuggingPattern), Project)).Lesson;
        lessons.Approve(current.LessonId, "ok", supersedes: old.LessonId);

        var result = await retriever.RetrieveAsync(new RetrievalRequest("hooks binary build", 2_000, ProjectId: Project), Ct);
        Assert.Equal([current.LessonId], result.Items.Where(i => i.Kind == MemoryItemKind.Lesson).Select(i => i.ItemId));
    }

    [Fact]
    public async Task A_local_only_lesson_is_marked_so_cloud_prompts_leave_it_out()
    {
        using var store = new TempStore();
        var (retriever, lessons) = Build(store);
        Approved(lessons, Content(), Project, localOnly: true);
        var item = Assert.Single((await retriever.RetrieveAsync(new RetrievalRequest("hooks binary", 1_000, ProjectId: Project), Ct)).Items);
        Assert.True(item.LocalOnly);
    }
}
