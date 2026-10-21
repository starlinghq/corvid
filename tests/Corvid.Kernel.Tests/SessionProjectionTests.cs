using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Kernel.Tests;

public sealed class SessionProjectionTests : IDisposable
{
    private static readonly CompactionSummary Summary = new("move reminder", ["local only"], [], [], [], "tell sam");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempLog _db = new();

    public void Dispose() => _db.Dispose();

    private long Append(params SessionEvent[] events)
    {
        var head = _db.Log.GetHead("s", cancellationToken: Ct);
        return _db.Log.Append("s", head, Samples.Data(events), cancellationToken: Ct)[^1].Sequence;
    }

    private SessionSnapshot Load(long? through = null, bool expand = false) =>
        SessionProjection.Load(_db.Log, "s", through, expand, cancellationToken: Ct);

    private static Decision Decide(string id) =>
        new(id, "intent", ImmutableDictionary<string, double>.Empty.Add("use_tools", 0.9), 0.9, 0.6, "use_tools");

    [Fact]
    public void Compaction_replaces_a_range_in_the_transcript_only()
    {
        Append(
            new UserMessage("one"),                                         // 1
            new AssistantMessage("two"),                                    // 2
            new ToolCallIssued("c1", "read", Samples.Json("{}")),           // 3
            new ToolResult("c1", Samples.Json("\"text\"")),                 // 4
            new UserMessage("five"));                                       // 5
        Append(new Compacted(1, 4, Summary));                               // 6

        var snapshot = Load();

        Assert.Equal(6, snapshot.Head);
        Assert.Collection(snapshot.Transcript,
            entry => Assert.Equal((1L, 4L, "tell sam"), (entry.FromSequence, entry.ThroughSequence, ((Compacted)entry.Content).Summary.NextStep)),
            entry => Assert.Equal("five", Assert.IsType<UserMessage>(entry.Content).Text));
        Assert.Empty(snapshot.Issues);
        // The raw range is still in the log, and the expanded view shows it.
        Assert.Equal(6, _db.Log.Read("s", cancellationToken: Ct).Count);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], Load(expand: true).Transcript.Select(entry => entry.FromSequence));
    }

    [Fact]
    public void Goals_todos_approvals_and_decisions_survive_compaction()
    {
        Append(
            new GoalSet("ship the event log"),                                  // 1
            new TodosSet([new TodoItem("t1", "tests", Completed: false)]),     // 2
            new ApprovalRequested("a1", "git push", "publishes the branch"),    // 3
            new ApprovalRequested("a2", "rm build", "deletes files"),           // 4
            new ApprovalResolved("a2", Approved: false, "user denied"),         // 5
            Decide("d1"),                                                       // 6
            new UserMessage("chatter"),                                         // 7
            new Compacted(1, 7, Summary));                                      // 8

        var snapshot = Load();

        Assert.Equal("ship the event log", snapshot.Goal);
        Assert.Equal("tests", Assert.Single(snapshot.Todos).Text);
        Assert.Equal("a1", Assert.Single(snapshot.PendingApprovals).Key);
        Assert.Equal("d1", Assert.Single(snapshot.Decisions).Key);
        Assert.IsType<Compacted>(Assert.Single(snapshot.Transcript).Content);
    }

    [Fact]
    public void Later_goal_and_todo_events_replace_earlier_ones()
    {
        Append(new GoalSet("first"), new TodosSet([new TodoItem("t1", "a", false)]),
            new GoalSet(null), new TodosSet([new TodoItem("t1", "a", true), new TodoItem("t2", "b", false)]));

        var snapshot = Load();

        Assert.Null(snapshot.Goal);
        Assert.Equal([true, false], snapshot.Todos.Select(item => item.Completed));
        Assert.Equal("first", Load(through: 1).Goal);
    }

    [Fact]
    public void The_latest_project_binding_is_the_sessions_project_and_stays_out_of_the_transcript()
    {
        Assert.Null(Load().ProjectId);
        Append(new ProjectBound("p1", "remote"), new UserMessage("one"), new ProjectBound("p2", "path"));

        var snapshot = Load();

        Assert.Equal("p2", snapshot.ProjectId);
        Assert.Equal("p1", Load(through: 2).ProjectId);
        Assert.Equal("one", Assert.IsType<UserMessage>(Assert.Single(snapshot.Transcript).Content).Text);
    }

    [Fact]
    public void Decision_outcomes_are_separate_events_and_the_decision_is_unchanged()
    {
        Append(Decide("d1"), Decide("d2"), new DecisionOutcomeRecorded("d1", "user_approved"));

        var snapshot = Load();
        var raw = _db.Log.Read("s", cancellationToken: Ct);

        Assert.Equal("user_approved", snapshot.Decisions["d1"].Outcome);
        Assert.Null(snapshot.Decisions["d2"].Outcome);
        Assert.Equal(0.6, snapshot.Decisions["d1"].Decision.Threshold);
        Assert.IsType<Decision>(raw[0].Payload);
        Assert.IsType<DecisionOutcomeRecorded>(raw[2].Payload);
        Assert.Null(Load(through: 2).Decisions["d1"].Outcome);
    }

    [Fact]
    public void A_position_projects_exactly_the_prefix()
    {
        Append(new UserMessage("a"), new GoalSet("g1"), new AssistantMessage("b"),
            new ModelSwitched("generator", Samples.Json("""{"n":"small"}"""), Samples.Json("""{"n":"large"}"""), "escalate"),
            new GoalSet("g2"), new CheckpointTaken("k1", "rev"), new UserMessage("c"));

        var events = _db.Log.Read("s", cancellationToken: Ct);
        for (long position = 0; position <= events.Count; position++)
        {
            var at = Load(through: position);
            var replay = SessionProjection.Build("s", events.Take((int)position));
            Assert.Equal(position, at.Head);
            Assert.Equal(replay.Goal, at.Goal);
            Assert.Equal(replay.Transcript.Select(entry => entry.FromSequence), at.Transcript.Select(entry => entry.FromSequence));
            Assert.Equal(replay.ModelProfiles.Keys, at.ModelProfiles.Keys);
            Assert.Equal(replay.Checkpoints.Length, at.Checkpoints.Length);
        }
        Assert.Equal("g1", Load(through: 4).Goal);
        Assert.Equal("large", Load(through: 4).ModelProfiles["generator"].GetProperty("n").GetString());
    }

    [Fact]
    public void Loading_pages_through_long_sessions()
    {
        // More than one page of the log's maximum read size.
        for (var batch = 0; batch < 5; batch++)
            Append([.. Enumerable.Range(0, 1000).Select(i => new UserMessage($"{batch}:{i}"))]);

        var snapshot = SessionProjection.Load(_db.Reopen(), "s", cancellationToken: Ct);

        Assert.Equal(5000, snapshot.Head);
        Assert.Equal(5000, snapshot.Transcript.Length);
        Assert.Equal("4:999", Assert.IsType<UserMessage>(snapshot.Transcript[^1].Content).Text);
    }

    [Fact]
    public void A_wider_compaction_replaces_the_summary_it_contains()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new Compacted(1, 2, Summary),   // 1-3
            new UserMessage("4"), new Compacted(1, 4, Summary with { NextStep = "wider" }), // 4-5
            new UserMessage("6"));                                                          // 6

        var snapshot = Load();

        Assert.Empty(snapshot.Issues);
        Assert.Collection(snapshot.Transcript,
            entry => Assert.Equal("wider", ((Compacted)entry.Content).Summary.NextStep),
            entry => Assert.Equal(6, entry.FromSequence));
    }

    [Fact]
    public void Expanding_a_compaction_reveals_what_it_covered_one_layer_at_a_time()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new Compacted(1, 2, Summary with { NextStep = "inner" }), // 1-3
            new UserMessage("4"), new Compacted(1, 4, Summary with { NextStep = "outer" }),                           // 4-5
            new UserMessage("6"));                                                                                    // 6
        Append(new CompactionExpanded(5));                                                                            // 7

        var outer = Load();

        Assert.Empty(outer.Issues);
        Assert.Collection(outer.Transcript,
            entry => Assert.Equal("inner", ((Compacted)entry.Content).Summary.NextStep),
            entry => Assert.Equal(4, entry.FromSequence),
            entry => Assert.Equal(6, entry.FromSequence));
        Assert.Equal("outer", ((Compacted)Load(through: 6).Transcript[0].Content).Summary.NextStep);

        Append(new CompactionExpanded(3));                                                                            // 8
        Assert.Equal([1L, 2L, 4L, 6L], Load().Transcript.Select(entry => entry.FromSequence));
    }

    [Fact]
    public void After_an_expansion_a_new_compaction_must_still_contain_the_summaries_it_overlaps()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new Compacted(1, 2, Summary),   // 1-3
            new UserMessage("4"), new Compacted(1, 4, Summary), new CompactionExpanded(5), // 4-6
            new Compacted(2, 4, Summary),                                                  // 7: splits the inner summary again
            new Compacted(1, 4, Summary with { NextStep = "again" }));                     // 8

        var snapshot = Load();

        Assert.Equal(7, Assert.Single(snapshot.Issues).Sequence);
        Assert.Equal("again", ((Compacted)Assert.Single(snapshot.Transcript).Content).Summary.NextStep);
    }

    [Fact]
    public void An_expansion_of_nothing_or_twice_is_reported()
    {
        Append(new UserMessage("1"), new Compacted(1, 1, Summary), new CompactionExpanded(1), new CompactionExpanded(2),
            new CompactionExpanded(2));

        var snapshot = Load();

        Assert.Equal([3L, 5L], snapshot.Issues.Select(issue => issue.Sequence));
        Assert.Equal([1L], snapshot.Transcript.Select(entry => entry.FromSequence));
        Assert.IsType<UserMessage>(snapshot.Transcript[0].Content);
    }

    public static TheoryData<long, long, string> InvalidCompactions() => new()
    {
        { 2, 3, "splits a tool call" },   // the call at 2 without its result at 4
        { 4, 5, "splits a tool call" },   // the result at 4 without its call at 2
        { 1, 7, "prior history" },        // includes itself
        { 2, 2, "splits a tool call" },
    };

    [Theory]
    [MemberData(nameof(InvalidCompactions))]
    public void Invalid_compactions_are_ignored_and_reported(long from, long through, string reason)
    {
        Append(new UserMessage("1"), new ToolCallIssued("c1", "read", Samples.Json("{}")), new AssistantMessage("3"),
            new ToolResult("c1", Samples.Json("{}")), new UserMessage("5"), new UserMessage("6"));
        // The log only checks that a range is prior history; relationships are the projection's to judge.
        if (through >= 7)
        {
            Assert.Throws<ArgumentException>(() => Append(new Compacted(from, through, Summary)));
            return;
        }
        Append(new Compacted(from, through, Summary));

        var snapshot = Load();

        Assert.Contains(reason, Assert.Single(snapshot.Issues).Reason, StringComparison.Ordinal);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], snapshot.Transcript.Select(entry => entry.FromSequence));
    }

    [Fact]
    public void A_partial_overlap_with_an_active_summary_is_ignored()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new UserMessage("3"),
            new Compacted(1, 2, Summary), new Compacted(2, 3, Summary));

        var snapshot = Load();

        Assert.Equal(5, Assert.Single(snapshot.Issues).Sequence);
        Assert.Equal([1L, 3L], snapshot.Transcript.Select(entry => entry.FromSequence));
    }

    [Fact]
    public void Inconsistent_persisted_events_are_reported_without_making_the_session_unreadable()
    {
        Append(
            new ToolCallIssued("c1", "read", Samples.Json("{}")),
            new ToolCallIssued("c1", "read", Samples.Json("{}")),
            new ToolResult("missing", Samples.Json("{}")),
            new DecisionOutcomeRecorded("nope", "accepted"),
            new ApprovalResolved("nope", true, ""),
            Decide("d1"), Decide("d1"));

        var snapshot = Load();

        Assert.Equal([2L, 3L, 4L, 5L, 7L], snapshot.Issues.Select(issue => issue.Sequence));
        Assert.Single(snapshot.Decisions);
        Assert.Equal(3, snapshot.Transcript.Length);
    }

    [Fact]
    public void Replay_input_must_be_one_sessions_complete_prefix()
    {
        Append(new UserMessage("a"), new UserMessage("b"), new UserMessage("c"));
        var events = _db.Log.Read("s", cancellationToken: Ct);

        Assert.Throws<ArgumentException>(() => SessionProjection.Build("s", events.Skip(1)));
        Assert.Throws<ArgumentException>(() => SessionProjection.Build("other", events));
        Assert.Throws<ArgumentException>(() => SessionProjection.Build("s", events, throughSequence: 4));
        Assert.Throws<ArgumentException>(() => Load(through: 4));
        Assert.Equal(0, SessionProjection.Load(_db.Log, "empty", cancellationToken: Ct).Head);
    }

    [Fact]
    public void A_rewind_shows_the_transcript_as_it_was_and_keeps_everything_else()
    {
        Append(new UserMessage("one"), new AssistantMessage("first"),                                       // 1-2
            new UserMessage("two"), new ToolCallIssued("call-1", "write_file", Samples.Json("{}")), Decide("d1"), // 3-5
            new ToolResult("call-1", Samples.Json("{}")), new AssistantMessage("second"), new GoalSet("kept"));  // 6-8
        Append(new SessionRewound(2, "undo", ["a.txt"]));                                                    // 9
        Append(new UserMessage("three"));                                                                   // 10

        var snapshot = Load();

        Assert.Equal(["one", "first", "three"], snapshot.Transcript.Select(entry => entry.Content switch
        {
            UserMessage user => user.Text,
            AssistantMessage assistant => assistant.Text,
            _ => "?",
        }));
        Assert.Equal("kept", snapshot.Goal);
        Assert.Contains("d1", snapshot.Decisions.Keys);
        Assert.Contains("call-1", snapshot.IssuedCallIds);
        Assert.Equal([new HiddenRange(2, 9)], snapshot.Hidden);
        Assert.False(snapshot.IsVisible(4));
        Assert.True(snapshot.IsVisible(10));
        Assert.Empty(snapshot.Issues);
        // Any earlier position still projects without the rewind.
        Assert.Equal(6, Load(through: 8).Transcript.Length);
    }

    [Fact]
    public void Rewinds_nest_and_a_summary_written_after_the_position_goes_with_it()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new UserMessage("3"), new Compacted(2, 3, Summary)); // 1-4
        Append(new SessionRewound(1, "undo", []));                                                                 // 5
        Append(new UserMessage("4"));                                                                             // 6
        Append(new SessionRewound(0, "rollback", []));                                                             // 7

        Assert.Empty(Load().Transcript);
        Assert.Equal(["1", "4"], Load(through: 6).Transcript.Select(entry => ((UserMessage)entry.Content).Text));
        Assert.Empty(Load().Issues);
    }

    [Fact]
    public void A_summary_a_rewind_hid_stays_hidden_after_a_later_compaction()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new Compacted(1, 2, Summary with { NextStep = "undone" })); // 1-3
        Append(new SessionRewound(2, "undo", []));                                                                   // 4
        Append(new UserMessage("5"), new UserMessage("6"), new Compacted(5, 6, Summary with { NextStep = "later" })); // 5-7

        var snapshot = Load();

        Assert.Empty(snapshot.Issues);
        Assert.Collection(snapshot.Transcript,
            entry => Assert.Equal("1", ((UserMessage)entry.Content).Text),
            entry => Assert.Equal("2", ((UserMessage)entry.Content).Text),
            entry => Assert.Equal("later", ((Compacted)entry.Content).Summary.NextStep));
    }

    [Fact]
    public void A_rewind_takes_back_an_expansion_written_after_the_position()
    {
        Append(new UserMessage("1"), new UserMessage("2"), new Compacted(1, 2, Summary), new UserMessage("4")); // 1-4
        Append(new CompactionExpanded(3));                                                                      // 5
        Assert.Equal([1L, 2L, 4L], Load().Transcript.Select(entry => entry.FromSequence));
        Append(new SessionRewound(4, "undo", []));                                                              // 6

        var snapshot = Load();

        Assert.Empty(snapshot.Issues);
        Assert.Collection(snapshot.Transcript,
            entry => Assert.IsType<Compacted>(entry.Content),
            entry => Assert.Equal(4, entry.FromSequence));
    }

    [Fact]
    public void A_rewind_that_would_split_a_call_from_its_result_is_an_issue_and_is_ignored()
    {
        Append(new UserMessage("a"), new ToolCallIssued("call-1", "t", Samples.Json("{}")), new ToolResult("call-1", Samples.Json("{}")),
            new ToolCallIssued("call-2", "t", Samples.Json("{}")));                                         // 1-4, call-2 still open
        Assert.NotNull(SessionProjection.CheckRewind(_db.Log, "s", 2, Ct));
        Assert.NotNull(SessionProjection.CheckRewind(_db.Log, "s", 3, Ct));
        Assert.NotNull(SessionProjection.CheckRewind(_db.Log, "s", 9, Ct));
        Append(new ToolResult("call-2", Samples.Json("{}")));                                               // 5
        Assert.Null(SessionProjection.CheckRewind(_db.Log, "s", 3, Ct));
        Append(new SessionRewound(2, "undo", []));                                                          // 6

        var snapshot = Load();

        Assert.Equal(6, Assert.Single(snapshot.Issues).Sequence);
        Assert.Equal(5, snapshot.Transcript.Length);
        Assert.Empty(snapshot.Hidden);
    }
}
