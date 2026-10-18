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

    private static string SourceGoldenDirectory([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "evals", "golden"));
}
