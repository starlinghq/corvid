using System.Runtime.CompilerServices;
using Corvid.Abstractions.Events;

namespace Corvid.Evals.Tests;

public sealed class GoldenSetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "golden");

    private static string[] FileNames() =>
        [.. Directory.GetFiles(GoldenDirectory, "*.json").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Files() => [.. FileNames()];

    private static GoldenTrajectory Load(string file) => GoldenTrajectoryFile.Load(Path.Combine(GoldenDirectory, file));

    [Theory]
    [MemberData(nameof(Files))]
    public void Golden_trajectories_replay_to_their_recorded_projections(string file)
    {
        var trajectory = Load(file);
        Assert.Equal(Path.GetFileNameWithoutExtension(file), trajectory.Id);

        // Authoring: CORVID_RECORD_GOLDENS=1 rewrites the committed fixture from today's projection. Review the diff.
        if (Environment.GetEnvironmentVariable("CORVID_RECORD_GOLDENS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceGoldenDirectory(), file),
                GoldenTrajectoryFile.Serialize(ReplayRunner.Record(trajectory, Ct)));
            return;
        }

        var report = ReplayRunner.Verify(trajectory, Ct);
        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void The_seed_set_covers_the_kernel_behaviours_replay_must_keep()
    {
        var events = FileNames().Select(Load).SelectMany(trajectory => trajectory.Events).ToArray();

        Assert.Contains(events, item => item is ToolCallIssued);
        Assert.Contains(events, item => item is ToolResult);
        Assert.Contains(events, item => item is Compacted);
        Assert.Contains(events, item => item is CompactionExpanded);
        Assert.Contains(events, item => item is GoalSet);
        Assert.Contains(events, item => item is ModelSwitched);
        Assert.Contains(events, item => item is DecisionOutcomeRecorded);
    }

    // These restate what the recorded projections say, independently of the recorder, so re-recording a
    // regression into the fixtures does not silently pass.
    [Fact]
    public void Goal_todos_and_approval_survive_compaction()
    {
        using var session = ReplaySession.Create(Load("goal-across-compaction.json"), Ct);

        var compacted = session.Project(10, cancellationToken: Ct);
        Assert.Equal("ship the release notes", compacted.Goal);
        Assert.Equal([true, false], compacted.Todos.Select(item => item.Completed));
        Assert.Equal("a1", Assert.Single(compacted.PendingApprovals).Key);
        Assert.IsType<Compacted>(compacted.Transcript[0].Content);
        Assert.Equal(2, compacted.Transcript.Length);

        Assert.Null(session.Project(11, cancellationToken: Ct).Goal);
    }

    [Fact]
    public void Model_profiles_follow_the_switches()
    {
        using var session = ReplaySession.Create(Load("model-switch.json"), Ct);

        Assert.Equal("cloud-large", session.Project(4, cancellationToken: Ct).ModelProfiles["generator"].GetProperty("name").GetString());
        var end = session.Project(session.Head, cancellationToken: Ct);
        Assert.Equal("qwen3.5-0.8b", end.ModelProfiles["generator"].GetProperty("name").GetString());
        Assert.Equal(["generator", "summarizer"], end.ModelProfiles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("task_succeeded", end.Decisions["d-esc"].Outcome);
    }

    [Fact]
    public void Compaction_hides_the_range_but_the_expanded_view_keeps_it()
    {
        using var session = ReplaySession.Create(Load("compaction.json"), Ct);

        var compacted = session.Project(session.Head, cancellationToken: Ct);
        Assert.Collection(compacted.Transcript,
            entry => Assert.Equal((1L, 9L, "ask for a day"), (entry.FromSequence, entry.ThroughSequence, ((Compacted)entry.Content).Summary.NextStep)),
            entry => Assert.Equal("tuesday", Assert.IsType<UserMessage>(entry.Content).Text));
        Assert.Equal(9, session.Project(session.Head, expandCompactions: true, cancellationToken: Ct).Transcript.Length);
    }

    [Fact]
    public void Inconsistent_history_is_reported_not_thrown()
    {
        using var session = ReplaySession.Create(Load("inconsistent-history.json"), Ct);

        var snapshot = session.Project(session.Head, cancellationToken: Ct);
        Assert.Equal([4L, 5L, 8L], snapshot.Issues.Select(issue => issue.Sequence));
        // The rejected compaction leaves the raw events in the transcript.
        Assert.Equal(4, snapshot.Transcript.Length);
    }

    // Recorded from the turn loop (C-011); tests/Corvid.Kernel.Tests/GoldenTurnTests checks the loop still produces them.
    [Fact]
    public void A_turn_loop_tool_call_keeps_its_degraded_decisions_and_outcome()
    {
        using var session = ReplaySession.Create(Load("turn-read-only-tool.json"), Ct);

        var snapshot = session.Project(session.Head, cancellationToken: Ct);
        Assert.All(snapshot.Decisions.Values, state => Assert.Equal("degraded", state.Decision.Decider));
        var ingress = snapshot.Decisions.Values.Single(state => state.Decision.QuestionId == "tool" && state.Decision.State!.Value.GetProperty("step").GetInt32() == 0);
        Assert.Equal(("offer_tool:list_notes", "tool_succeeded"), (ingress.Decision.Action, ingress.Outcome));
        Assert.Equal("stop", snapshot.Decisions.Values.Single(state => state.Decision.QuestionId == "done").Decision.Action);
        Assert.IsType<AssistantMessage>(snapshot.Transcript[^1].Content);
    }

    [Fact]
    public void A_turn_loop_repair_and_approval_link_the_prompt_to_its_call()
    {
        using var session = ReplaySession.Create(Load("turn-repair-then-approval.json"), Ct);

        var asking = session.Project(10, cancellationToken: Ct);
        Assert.Equal("call-2", Assert.Single(asking.PendingApprovals).Value.CallId);
        var end = session.Project(session.Head, cancellationToken: Ct);
        Assert.Empty(end.PendingApprovals);
        Assert.Equal([true, false], end.Transcript.Select(entry => entry.Content).OfType<ToolResult>().Select(result => result.IsError));
        var risk = end.Decisions.Values.Single(state => state.Decision.QuestionId == "risk");
        Assert.Equal(("ask", "user_approved"), (risk.Decision.Action, risk.Outcome));
    }

    [Fact]
    public void Expansion_peels_one_summary_at_a_time_and_a_later_compaction_covers_it_again()
    {
        using var session = ReplaySession.Create(Load("compaction-expanded.json"), Ct);

        Assert.Equal((1L, 5L), Range(Assert.Single(session.Project(7, cancellationToken: Ct).Transcript)));
        Assert.Equal([(1L, 2L), (4L, 4L), (5L, 5L)], session.Project(8, cancellationToken: Ct).Transcript.Select(Range));
        Assert.DoesNotContain(session.Project(9, cancellationToken: Ct).Transcript, entry => entry.Content is Compacted);
        var end = session.Project(session.Head, cancellationToken: Ct);
        Assert.Equal((1L, 10L), Range(Assert.Single(end.Transcript)));
        Assert.Empty(end.Issues);

        static (long, long) Range(Corvid.Kernel.Events.TranscriptEntry entry) => (entry.FromSequence, entry.ThroughSequence);
    }

    // Recorded from the turn loop (C-016).
    [Fact]
    public void A_turn_loop_compaction_replaces_earlier_turns_and_keeps_goal_and_todos()
    {
        using var session = ReplaySession.Create(Load("turn-compaction.json"), Ct);

        var end = session.Project(session.Head, cancellationToken: Ct);
        var compacted = Assert.IsType<Compacted>(end.Transcript[0].Content);
        Assert.Equal((1L, 8L, 1), (compacted.FromSequence, compacted.ThroughSequence, compacted.Chunks));
        Assert.Equal(["drafted sections one to three"], compacted.Summary.Decisions);
        Assert.Equal("what is next?", Assert.IsType<UserMessage>(end.Transcript[1].Content).Text);
        Assert.Equal("ship the report", end.Goal);
        Assert.Single(end.Todos);
        Assert.Equal(3, session.Project(session.Head, expandCompactions: true, cancellationToken: Ct).Transcript
            .Count(entry => entry.Content is AssistantMessage { Text: var text } && text.StartsWith("The section is drafted", StringComparison.Ordinal)));
    }

    private static string SourceGoldenDirectory([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "evals", "golden"));
}
