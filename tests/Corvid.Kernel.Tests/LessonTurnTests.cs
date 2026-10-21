using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Turns;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Tools;

namespace Corvid.Kernel.Tests;

internal sealed class RecordingLessons : ILessonLearning
{
    public List<LessonOpportunity> Queued { get; } = [];

    public void Queue(LessonOpportunity opportunity) => Queued.Add(opportunity);
}

/// <summary>C-025 in the turn loop: the session's project, <c>lesson_opportunity</c> at egress, and lessons in recall.</summary>
public sealed class LessonTurnTests
{
    private static readonly ProjectIdentity Project = new(ProjectIds.FromRemote("https://example.org/team/notes.git"), ProjectSources.Remote);
    private const string Question = "future session";

    private static TurnHarness Harness(RecordingLessons lessons, TurnOptions? options = null, ProjectIdentity? project = null,
        params Func<GeneratorCall, Microsoft.Extensions.AI.ChatResponse>[] script)
    {
        var generator = new FakeGenerator(script.Length > 0 ? script : [Reply.Call("list_notes", """{"limit":5}"""), Reply.Say("No notes.")])
        {
            YesNo = FakeGenerator.YesTo(Question, "fully handled"),
        };
        return new TurnHarness(generator, options: options) { Project = project ?? Project, LessonLearning = lessons };
    }

    [Fact]
    public async Task An_answered_turn_with_a_tool_call_asks_lesson_opportunity_and_queues_the_turn()
    {
        var lessons = new RecordingLessons();
        using var harness = Harness(lessons);
        var result = await harness.RunAsync("list my notes");

        Assert.True(result.LessonQueued);
        var decision = Assert.Single(harness.Decisions("lesson_opportunity"));
        Assert.Equal("queue_lesson_draft", decision.Action);
        var queued = Assert.Single(lessons.Queued);
        var events = harness.Events();
        Assert.Equal(new ProjectBound(Project.Id, ProjectSources.Remote), events[0].Payload);
        Assert.Equal(2, queued.FromSequence);
        Assert.IsType<UserMessage>(events[1].Payload);
        Assert.IsType<AssistantMessage>(events.Single(e => e.Sequence == queued.ThroughSequence).Payload);
        Assert.Equal((TurnHarness.Session, Project.Id, decision.DecisionId, "list my notes", false, false),
            (queued.SessionId, queued.ProjectId, queued.DecisionId, queued.Request, queued.Tainted, queued.LocalOnly));
    }

    [Fact]
    public async Task A_turn_whose_local_prompt_held_a_local_only_block_queues_a_local_only_opportunity()
    {
        using var memory = new MemoryStore();
        memory.Blocks.Set("user", "Lives in Leeds.");
        Assert.True(memory.Blocks.Get("user").LocalOnly);
        var lessons = new RecordingLessons();
        var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":5}"""), Reply.Say("No notes."))
        {
            YesNo = FakeGenerator.YesTo(Question, "fully handled"),
        };
        using var harness = new TurnHarness(generator) { Project = Project, LessonLearning = lessons, CoreBlocks = memory.Blocks.Snapshot() };
        await harness.RunAsync("list my notes");

        Assert.Contains("Lives in Leeds.", generator.Calls[0].AllText, StringComparison.Ordinal);
        // LessonLearnerTests carries the flag on to lessons.local_only.
        Assert.True(Assert.Single(lessons.Queued).LocalOnly);
    }

    [Fact]
    public async Task A_local_only_lesson_reaches_a_local_generator_but_never_a_cloud_one()
    {
        using var memory = new MemoryStore();
        var store = new LessonStore(memory.Database);
        var content = new LessonContent(LessonKinds.Convention, "Keep each note under one line", "when adding notes to the list",
            "Write one line per note.\nThe list view truncates longer notes (event 3).\nConfirm with list_notes.", ["notes", "format"],
            [new LessonEvidence("s-old", [3], "the list cut a long note", new string('a', 64))]);
        var lesson = store.File(new Lesson(Lesson.IdFor(Project.Id, content.Kind, content.Title, content.Body), Project.Id, content.Kind,
            content.Title, content.Applicability, content.Body, content.Tags, LessonStates.Candidate, null, LessonAuthors.Agent, null, null,
            true, default, null, content.Evidence, "s-old")).Lesson;
        store.Approve(lesson.LessonId, "ok");
        // A shareable lesson alongside it, so the cloud turn is known to have recalled lessons at all.
        var shared = new LessonContent(LessonKinds.Workflow, "List the notes after adding notes", "after adding notes to the list",
            "Run list_notes.\nAdding can drop a note silently (event 5).\nConfirm the new note is listed.", ["notes", "check"],
            [new LessonEvidence("s-old", [5], "an added note was missing", new string('b', 64))]);
        store.Approve(store.File(new Lesson(Lesson.IdFor(Project.Id, shared.Kind, shared.Title, shared.Body), Project.Id, shared.Kind,
            shared.Title, shared.Applicability, shared.Body, shared.Tags, LessonStates.Candidate, null, LessonAuthors.Agent, null, null,
            false, default, null, shared.Evidence, "s-old")).Lesson.LessonId, "ok");

        var local = new FakeGenerator(Reply.Say("Done.")) { YesNo = FakeGenerator.YesTo("remembered") };
        using (var harness = new TurnHarness(local)
        {
            Project = Project,
            Retriever = new HybridRetriever(memory.Episodes, memory.Facts, lessons: store),
        })
        {
            await harness.RunAsync("adding notes to the list");
            Assert.Contains("Lesson (convention): Keep each note under one line", local.Calls[0].AllText, StringComparison.Ordinal);
        }

        var cloud = new FakeGenerator(Reply.Say("Done.")) { YesNo = FakeGenerator.YesTo("remembered") };
        using var log = new TurnHarness(new FakeGenerator());
        var embedder = new KeywordEmbedder(TurnHarness.Keywords);
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, new ModelBinding(new ModelProfile("gen-cloud", 200_000, 100_000, ToolCallSupport.Native,
                StructuredOutputSupport.None, PromptCachingStyle.Breakpoints, ModelCost.Free, PrivacyClass.Cloud, "remote"), cloud))
            .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
        var services = new TurnServices
        {
            Log = log.Log,
            Slots = new SessionModelSlots(registry, log.Log, TurnHarness.Session),
            Fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, cloud), log.Log),
            Tools = new ToolRegistry(),
            Retriever = new HybridRetriever(memory.Episodes, memory.Facts, lessons: store),
            Project = Project,
        };
        await new TurnRunner(TurnHarness.Session, services).RunAsync(
            new Envelope(TurnHarness.Session, "cli", "adding notes to the list", EnvelopeOrigin.User), TestContext.Current.CancellationToken);
        var call = Assert.Single(cloud.Calls);
        Assert.Contains("Lesson (workflow): List the notes after adding notes", call.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep each note under one line", call.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_project_is_bound_once_and_again_only_when_it_changes()
    {
        var lessons = new RecordingLessons();
        using var harness = Harness(lessons, script: [Reply.Say("one"), Reply.Say("two"), Reply.Say("three")]);
        await harness.RunAsync("hello");
        await harness.RunAsync("again");
        // A new runner for the same session (a restart) finds the project in the log.
        await harness.RunAsync(new TurnRunner(TurnHarness.Session, harness.Services()), "after a restart");
        Assert.Single(harness.Payloads().OfType<ProjectBound>());

        var other = new ProjectIdentity(ProjectIds.FromPath("/srv/other"), ProjectSources.Path);
        var services = harness.Services();
        await new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log, Slots = services.Slots, Fabric = services.Fabric, Tools = services.Tools, Approvals = services.Approvals,
            Project = other,
        }).RunAsync(new Envelope(TurnHarness.Session, "cli", "elsewhere", EnvelopeOrigin.User), TestContext.Current.CancellationToken);
        Assert.Equal([Project.Id, other.Id], harness.Payloads().OfType<ProjectBound>().Select(bound => bound.ProjectId));
        Assert.Equal(other.Id, Kernel.Events.SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: TestContext.Current.CancellationToken).ProjectId);
    }

    [Fact]
    public async Task A_turn_without_tool_calls_is_not_asked()
    {
        var lessons = new RecordingLessons();
        using var harness = Harness(lessons, script: [Reply.Say("Hello.")]);
        var result = await harness.RunAsync("hello");
        Assert.False(result.LessonQueued);
        Assert.Empty(harness.Decisions("lesson_opportunity"));
        Assert.DoesNotContain(harness.Generator.Questions, q => q.Contains(Question, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unattended_and_replay_turns_are_not_asked()
    {
        var lessons = new RecordingLessons();
        using (var harness = Harness(lessons, new TurnOptions { Interactive = false }))
        {
            await harness.RunAsync("list my notes");
            Assert.Empty(harness.Decisions("lesson_opportunity"));
        }
        using (var harness = Harness(lessons, new TurnOptions { PinnedSkill = "notes" }))
        {
            await harness.RunAsync("list my notes");
            Assert.Empty(harness.Decisions("lesson_opportunity"));
        }
        Assert.Empty(lessons.Queued);
    }

    [Fact]
    public async Task A_guest_turn_is_not_asked()
    {
        var lessons = new RecordingLessons();
        using var harness = Harness(lessons);
        await harness.RunAsync(harness.Runner(), "list my notes", EnvelopeOrigin.User);
        Assert.Single(lessons.Queued);
        lessons.Queued.Clear();

        using var guest = Harness(lessons);
        await guest.Runner().RunAsync(new Envelope(TurnHarness.Session, "telegram", "list my notes", EnvelopeOrigin.User)
        {
            Guest = new GuestScope(["list_notes"]),
        }, TestContext.Current.CancellationToken);
        Assert.Empty(guest.Decisions("lesson_opportunity"));
        Assert.Empty(lessons.Queued);
    }

    [Fact]
    public async Task Without_a_project_or_a_learner_nothing_is_asked_or_bound()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Call("list_notes", """{"limit":5}"""), Reply.Say("No notes."))
        {
            YesNo = FakeGenerator.YesTo(Question, "fully handled"),
        }) { LessonLearning = new RecordingLessons() };
        await harness.RunAsync("list my notes");
        Assert.Empty(harness.Decisions("lesson_opportunity"));
        Assert.Empty(harness.Payloads().OfType<ProjectBound>());
    }

    [Fact]
    public async Task Approved_lessons_of_the_project_reach_the_generator_through_full_recall()
    {
        using var memory = new MemoryStore();
        var store = new LessonStore(memory.Database);
        var content = new LessonContent(LessonKinds.Convention, "Keep each note under one line", "when adding notes to the list",
            "Write one line per note.\nThe list view truncates longer notes (event 3).\nConfirm with list_notes.", ["notes", "format"],
            [new LessonEvidence("s-old", [3], "the list cut a long note", new string('a', 64))]);
        var lesson = store.File(new Lesson(Lesson.IdFor(Project.Id, content.Kind, content.Title, content.Body), Project.Id, content.Kind,
            content.Title, content.Applicability, content.Body, content.Tags, LessonStates.Candidate, null, LessonAuthors.Agent, null, null,
            false, default, null, content.Evidence, "s-old")).Lesson;
        store.Approve(lesson.LessonId, "ok");

        var generator = new FakeGenerator(Reply.Say("Done.")) { YesNo = FakeGenerator.YesTo("remembered") };
        using var harness = new TurnHarness(generator)
        {
            Project = Project,
            Retriever = new HybridRetriever(memory.Episodes, memory.Facts, lessons: store),
        };
        await harness.RunAsync("adding notes to the list");
        Assert.Contains("Lesson (convention): Keep each note under one line", generator.Calls[0].AllText, StringComparison.Ordinal);

        // Another project's session sees none of it.
        var other = new FakeGenerator(Reply.Say("Done.")) { YesNo = FakeGenerator.YesTo("remembered") };
        using var elsewhere = new TurnHarness(other)
        {
            Project = new ProjectIdentity(ProjectIds.FromPath("/srv/other"), ProjectSources.Path),
            Retriever = new HybridRetriever(memory.Episodes, memory.Facts, lessons: store),
        };
        await elsewhere.RunAsync("adding notes to the list");
        Assert.DoesNotContain("Lesson (", other.Calls[0].AllText, StringComparison.Ordinal);
    }
}
