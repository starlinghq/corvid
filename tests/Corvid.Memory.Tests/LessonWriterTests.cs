using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using static Corvid.Memory.Tests.Fixtures;
using static Corvid.Memory.Tests.LessonFixtures;

namespace Corvid.Memory.Tests;

public sealed class LessonWriterTests
{
    private const string Session = "s-1";

    private static LessonWriter Writer(TempStore store, IDecider? calibrated = null) =>
        new(new LessonStore(store.Database, store.Time), new DecisionFabric(DecisionPolicy.Default, Degraded(), store.Log, calibrated), store.Log);

    private static List<SessionEvent> Events(TempStore store) => [.. store.Log.Read(Session, cancellationToken: Ct).Select(e => e.Payload)];

    [Fact]
    public async Task Without_kev_every_lesson_is_a_candidate()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        var result = await writer.FileAsync(new LessonDraft(Project, Content()), Session, tainted: false, Ct);

        Assert.Equal(LessonFileStatus.Staged, result.Status);
        var lesson = Assert.Single(writer.Pending(Project));
        Assert.Equal((result.Lesson!.LessonId, "no answer"), (lesson.LessonId, lesson.ReviewReason));
        Assert.Equal(result.Decision!.DecisionId, lesson.DecisionId);
        var events = Events(store);
        var decision = Assert.IsType<Decision>(events[0]);
        Assert.Equal(("memory_op", "stage_for_review"), (decision.QuestionId, decision.Action));
        // The decider saw the lesson, not a fact.
        Assert.Contains("\"applicability\"", decision.Question!.Instructions.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(new MemoryWritten(lesson.LessonId, MemoryOperations.Staged), events[1]);
    }

    [Fact]
    public async Task An_invalid_draft_files_nothing_and_says_why()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        var result = await writer.FileAsync(new LessonDraft(Project, Content(title: "The agent fixed the build")), Session, false, Ct);
        Assert.Equal(LessonFileStatus.Invalid, result.Status);
        Assert.Contains(result.Violations, v => v.Rule == "title_imperative");
        Assert.Empty(writer.Store.List(Project));
        Assert.Empty(Events(store));
    }

    [Fact]
    public async Task A_calibrated_add_above_the_floor_approves()
    {
        using var store = new TempStore();
        var writer = Writer(store, FakeCalibratedDecider.MemoryOp("add"));
        var result = await writer.FileAsync(new LessonDraft(Project, Content()), Session, false, Ct);

        Assert.Equal(LessonFileStatus.Approved, result.Status);
        Assert.Equal((LessonStates.Approved, "add"), (result.Lesson!.State, result.Lesson.Proposed));
        Assert.Empty(writer.Pending(Project));
        Assert.Equal(new MemoryWritten(result.Lesson.LessonId, MemoryOperations.Add), Events(store)[^1]);
    }

    [Fact]
    public async Task A_calibrated_update_approves_and_supersedes_the_nearest_neighbour()
    {
        using var store = new TempStore();
        var writer = Writer(store, FakeCalibratedDecider.MemoryOp("add"));
        var old = (await writer.FileAsync(new LessonDraft(Project, Content()), Session, false, Ct)).Lesson!;

        var updater = Writer(store, FakeCalibratedDecider.MemoryOp("update"));
        var result = await updater.FileAsync(new LessonDraft(Project, Content(title: "Build the hooks binary with make before go test")),
            Session, false, Ct);

        Assert.Equal(LessonFileStatus.Approved, result.Status);
        Assert.Equal(old.LessonId, result.Superseded);
        Assert.Equal(old.LessonId, Assert.Single(result.Neighbours).LessonId);
        var superseded = updater.Store.Get(old.LessonId)!;
        Assert.Equal((LessonStates.Superseded, result.Lesson!.LessonId), (superseded.State, superseded.SupersededBy));
        Assert.Equal(new MemoryWritten(result.Lesson.LessonId, MemoryOperations.Update), Events(store)[^1]);
    }

    [Fact]
    public async Task A_calibrated_noop_stages_the_candidate_for_review_and_review_can_still_approve_it()
    {
        using var store = new TempStore();
        var old = (await Writer(store, FakeCalibratedDecider.MemoryOp("add")).FileAsync(new LessonDraft(Project, Content()), Session, false, Ct)).Lesson!;
        var writer = Writer(store, FakeCalibratedDecider.MemoryOp("noop"));
        var draft = new LessonDraft(Project, Content(title: "Build the hooks binary with make before go test"));
        var result = await writer.FileAsync(draft, Session, false, Ct);

        // noop writes nothing and rejects nothing: the candidate waits in /memory lessons pending with the decider's reason.
        Assert.Equal(LessonFileStatus.Staged, result.Status);
        Assert.Equal((LessonStates.Candidate, "noop"), (result.Lesson!.State, result.Lesson.Proposed));
        Assert.Equal($"duplicate of {old.LessonId} (decider)", result.Lesson.ReviewReason);
        Assert.Equal(result.Lesson.LessonId, Assert.Single(writer.Pending(Project)).LessonId);
        Assert.Equal(new MemoryWritten(result.Lesson.LessonId, MemoryOperations.Staged), Events(store)[^1]);

        // Review overrides the decider and records the outcome for calibration.
        var approved = writer.Approve(result.Lesson.LessonId, cancellationToken: Ct);
        Assert.Equal(LessonStates.Approved, approved.State);
        var outcome = Assert.Single(Events(store).OfType<DecisionOutcomeRecorded>());
        Assert.Equal(("corrected", result.Decision!.DecisionId), (outcome.Outcome, outcome.DecisionId));
    }

    [Fact]
    public async Task A_calibrated_noop_with_no_neighbour_stages_the_candidate()
    {
        using var store = new TempStore();
        var writer = Writer(store, FakeCalibratedDecider.MemoryOp("noop"));
        var result = await writer.FileAsync(new LessonDraft(Project, Content()), Session, false, Ct);
        Assert.Equal(LessonFileStatus.Staged, result.Status);
        Assert.Equal((LessonStates.Candidate, "no neighbour to be a duplicate of"), (result.Lesson!.State, result.Lesson.ReviewReason));
        Assert.Empty(result.Neighbours);
    }

    [Fact]
    public async Task Below_the_floor_or_invalidate_leaves_a_candidate()
    {
        using var store = new TempStore();
        var low = await Writer(store, FakeCalibratedDecider.MemoryOp("add", p: 0.4)).FileAsync(new LessonDraft(Project, Content()), Session, false, Ct);
        Assert.Equal(LessonFileStatus.Staged, low.Status);
        var invalidate = await Writer(store, FakeCalibratedDecider.MemoryOp("invalidate"))
            .FileAsync(new LessonDraft(Project, Content(kind: LessonKinds.Gotcha)), Session, false, Ct);
        Assert.Equal(LessonFileStatus.Staged, invalidate.Status);
        Assert.Equal("invalidate does not apply to a lesson", invalidate.Lesson!.ReviewReason);
    }

    [Fact]
    public async Task A_tainted_source_is_always_a_candidate_without_asking_the_gate()
    {
        using var store = new TempStore();
        var kev = FakeCalibratedDecider.MemoryOp("add");
        var result = await Writer(store, kev).FileAsync(new LessonDraft(Project, Content()), Session, tainted: true, Ct);
        Assert.Equal(LessonFileStatus.Staged, result.Status);
        Assert.Equal(MemoryWriter.TaintedReason, result.Lesson!.ReviewReason);
        Assert.Empty(kev.Requests);
        Assert.DoesNotContain(Events(store), e => e is Decision);
    }

    [Fact]
    public async Task Filing_an_existing_lesson_again_merges_evidence_without_a_new_decision()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        await writer.FileAsync(new LessonDraft(Project, Content()), Session, false, Ct);
        var before = Events(store).Count;
        var again = await writer.FileAsync(new LessonDraft(Project, Content(evidence: [Evidence("s-1", new string('c', 64), 14, 16)])), Session, false, Ct);
        Assert.Equal(LessonFileStatus.Duplicate, again.Status);
        Assert.Equal(2, again.Lesson!.Evidence.Length);
        Assert.Equal(before, Events(store).Count);
    }

    [Fact]
    public async Task Review_records_outcomes_for_calibration()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        var first = (await writer.FileAsync(new LessonDraft(Project, Content()), Session, false, Ct)).Lesson!;
        var second = (await writer.FileAsync(new LessonDraft(Project, Content(kind: LessonKinds.Gotcha)), Session, false, Ct)).Lesson!;

        var approved = writer.Approve(first.LessonId, cancellationToken: Ct);
        Assert.Equal(LessonStates.Approved, approved.State);
        var rejected = writer.Reject(second.LessonId, "too specific", Ct);
        Assert.Equal((LessonStates.Rejected, "too specific"), (rejected.State, rejected.ReviewReason));
        Assert.Throws<InvalidOperationException>(() => writer.Approve(second.LessonId, cancellationToken: Ct));

        var events = Events(store);
        Assert.Contains(new MemoryWritten(first.LessonId, MemoryOperations.Add), events);
        Assert.Contains(new DecisionOutcomeRecorded(first.DecisionId!, "approved"), events);
        Assert.Contains(new DecisionOutcomeRecorded(second.DecisionId!, "rejected"), events);

        // Approving with a supersede when the decider proposed add is a correction.
        var third = (await Writer(store, FakeCalibratedDecider.MemoryOp("add", p: 0.4))
            .FileAsync(new LessonDraft(Project, Content(title: "Build the hooks binary with make before go test")), Session, false, Ct)).Lesson!;
        Assert.Equal("add", third.Proposed);
        writer.Approve(third.LessonId, supersedes: first.LessonId, cancellationToken: Ct);
        Assert.Equal(LessonStates.Superseded, writer.Store.Get(first.LessonId)!.State);
        Assert.Contains(new DecisionOutcomeRecorded(third.DecisionId!, "corrected"), Events(store));
    }

    [Fact]
    public async Task Supersede_records_the_change_in_the_given_session()
    {
        using var store = new TempStore();
        var writer = Writer(store, FakeCalibratedDecider.MemoryOp("add"));
        var a = (await writer.FileAsync(new LessonDraft(Project, Content()), Session, false, Ct)).Lesson!;
        var b = (await writer.FileAsync(new LessonDraft(Project, Content(kind: LessonKinds.Convention)), Session, false, Ct)).Lesson!;
        writer.Supersede(a.LessonId, b.LessonId, "s-review");
        Assert.Equal(LessonStates.Superseded, writer.Store.Get(a.LessonId)!.State);
        Assert.Equal(new MemoryWritten(b.LessonId, MemoryOperations.Update), Assert.Single(store.Log.Read("s-review", cancellationToken: Ct)).Payload);
    }
}
