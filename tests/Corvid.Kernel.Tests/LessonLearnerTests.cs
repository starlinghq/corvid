using System.Security.Cryptography;
using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Kernel.Lessons;
using Corvid.Kernel.Turns;
using Corvid.Memory;

namespace Corvid.Kernel.Tests;

public sealed class LessonLearnerTests
{
    private static readonly string Project = ProjectIds.FromRemote("https://example.org/team/notes.git");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Draft = """
        Here is the lesson:
        ```json
        {"kind": "gotcha", "title": "Pass a limit to list_notes or it returns nothing",
         "applicability": "when listing notes with list_notes",
         "body": "Call list_notes with an explicit limit.\nWithout one the tool fails with 'limit required' (events 3-4).\nConfirm the call returns notes.",
         "tags": ["notes", "tools"], "positions": [3, 4], "summary": "list_notes failed without a limit"}
        ```
        """;

    private static (TurnHarness Harness, MemoryStore Memory, LessonLearner Learner) Build(params string[] replies)
    {
        var harness = new TurnHarness(new FakeGenerator());
        var memory = new MemoryStore();
        var drafter = new FakeGenerator([.. replies.Select(Reply.Say)]);
        var learner = new LessonLearner(new LessonWriter(new LessonStore(memory.Database), harness.Fabric, harness.Log), harness.Log, drafter);
        // One turn: the request, a failed call, the retry and the answer (events 1-6).
        harness.Seed(
            new UserMessage("list my notes"),
            new ToolCallIssued("c1", "list_notes", Samples.Json("{}")),
            new ToolResult("c1", Samples.Json("\"limit required\""), IsError: true),
            new ToolCallIssued("c2", "list_notes", Samples.Json("""{"limit":5}""")),
            new ToolResult("c2", Samples.Json("\"buy milk\"")),
            new AssistantMessage("One note: buy milk."));
        return (harness, memory, learner);
    }

    private static LessonOpportunity Opportunity(bool tainted = false, bool localOnly = false) =>
        new(TurnHarness.Session, Project, 1, 6, "d-1", "list my notes", tainted, localOnly);

    [Fact]
    public async Task A_drafted_lesson_is_filed_for_review_with_evidence_hashed_from_the_log()
    {
        var (harness, memory, learner) = Build(Draft);
        using var _ = harness;
        using var __ = memory;
        learner.Queue(Opportunity(localOnly: true));
        var result = Assert.Single(await learner.ProcessQueuedAsync(Ct));

        Assert.Equal(LessonLearningStatus.Filed, result.Status);
        var lesson = new LessonStore(memory.Database).Get(result.LessonId!)!;
        Assert.Equal((LessonStates.Candidate, LessonKinds.Gotcha, Project, true), (lesson.State, lesson.Kind, lesson.ProjectId, lesson.LocalOnly));
        var evidence = Assert.Single(lesson.Evidence);
        Assert.Equal([3L, 4L], evidence.Positions);
        var cited = harness.Events().Where(e => e.Sequence is 3 or 4).Select(e => EventJson.Serialize(e.Payload));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", cited)))), evidence.ContentHash);
        Assert.Equal(LessonOpportunityStates.Filed, Assert.Single(new LessonStore(memory.Database).Opportunities()).State);
        Assert.Contains(new MemoryWritten(lesson.LessonId, MemoryOperations.Staged), harness.Payloads());
    }

    [Fact]
    public void The_prompt_numbers_each_event_of_the_turn()
    {
        var (harness, memory, learner) = Build(Draft);
        using var _ = harness;
        using var __ = memory;
        var prompt = learner.DraftPrompt(harness.Events());
        Assert.Contains("[event 1] User: list my notes", prompt, StringComparison.Ordinal);
        Assert.Contains("[event 3] Tool error (list_notes): limit required", prompt, StringComparison.Ordinal);
        Assert.Contains("[event 6] Assistant: One note: buy milk.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Positions_outside_the_turn_are_declined()
    {
        var (harness, memory, learner) = Build(Draft.Replace("[3, 4]", "[3, 9]", StringComparison.Ordinal));
        using var _ = harness;
        using var __ = memory;
        learner.Queue(Opportunity());
        var result = Assert.Single(await learner.ProcessQueuedAsync(Ct));
        Assert.Equal(LessonLearningStatus.Declined, result.Status);
        Assert.Contains("outside the turn", result.Detail, StringComparison.Ordinal);
        Assert.Empty(new LessonStore(memory.Database).List(Project));
    }

    [Theory]
    [InlineData("""{"none": true}""", "no lesson")]
    [InlineData("I could not find anything.", "not a JSON lesson")]
    [InlineData("""{"kind": "gotcha", "title": "The agent fixed list_notes", "applicability": "always", "body": "x", "tags": ["a"], "positions": [3], "summary": "s"}""", "quality bar")]
    public async Task No_lesson_or_a_bad_draft_is_declined(string reply, string detail)
    {
        var (harness, memory, learner) = Build(reply);
        using var _ = harness;
        using var __ = memory;
        learner.Queue(Opportunity());
        var result = Assert.Single(await learner.ProcessQueuedAsync(Ct));
        Assert.Equal(LessonLearningStatus.Declined, result.Status);
        Assert.Contains(detail, result.Detail, StringComparison.Ordinal);
        Assert.Equal(LessonOpportunityStates.Declined, Assert.Single(new LessonStore(memory.Database).Opportunities()).State);
    }

    [Fact]
    public async Task An_undone_turn_is_not_drafted_from()
    {
        var (harness, memory, learner) = Build(Draft);
        using var _ = harness;
        using var __ = memory;
        harness.Seed(new SessionRewound(0, "undo", []));
        learner.Queue(Opportunity());
        var result = Assert.Single(await learner.ProcessQueuedAsync(Ct));
        Assert.Equal(LessonLearningStatus.Failed, result.Status);
        Assert.Contains("undone", result.Detail, StringComparison.Ordinal);
        Assert.Empty(new LessonStore(memory.Database).List(Project));
    }

    [Fact]
    public async Task The_same_lesson_drafted_twice_is_one_lesson()
    {
        var (harness, memory, learner) = Build(Draft, Draft);
        using var _ = harness;
        using var __ = memory;
        learner.Queue(Opportunity());
        learner.Queue(Opportunity());
        var results = await learner.ProcessQueuedAsync(Ct);
        Assert.Equal([LessonLearningStatus.Filed, LessonLearningStatus.Duplicate], results.Select(r => r.Status));
        Assert.Single(new LessonStore(memory.Database).List(Project));
    }

    [Fact]
    public async Task A_tainted_turn_is_drafted_with_its_taint_and_filed_without_the_gate()
    {
        var (harness, memory, learner) = Build(Draft);
        using var _ = harness;
        using var __ = memory;
        learner.Queue(Opportunity(tainted: true));
        var result = Assert.Single(await learner.ProcessQueuedAsync(Ct));
        Assert.Equal(LessonLearningStatus.Filed, result.Status);
        Assert.Equal(MemoryWriter.TaintedReason, new LessonStore(memory.Database).Get(result.LessonId!)!.ReviewReason);
        Assert.DoesNotContain(harness.Payloads(), e => e is Decision { QuestionId: "memory_op" });
    }
}
