using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Evals.Tests;

public sealed class ReplayRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Fixture = """
        {
          "format": "corvid-golden-trajectory",
          "format_version": 1,
          "payload_version": 1,
          "id": "small",
          "description": "two messages and a goal",
          "events": [
            { "type": "user_message", "text": "hi" },
            { "type": "goal_set", "goal": "say hello" },
            { "type": "assistant_message", "text": "hello" }
          ],
          "checks": [ { "through": 1, "expected": null }, { "through": 3, "expected": null } ]
        }
        """;

    private static GoldenTrajectory Recorded() => ReplayRunner.Record(GoldenTrajectoryFile.Parse(Fixture), Ct);

    [Fact]
    public void A_recorded_trajectory_verifies()
    {
        var report = ReplayRunner.Verify(Recorded(), Ct);

        Assert.True(report.Passed, report.Describe());
        Assert.Equal([1L, 3L], report.Checks.Select(check => check.Through));
    }

    [Fact]
    public void A_changed_projection_is_reported_with_its_path()
    {
        var recorded = Recorded();
        // Change only the last expectation; the goal_set event itself keeps "say hello".
        var text = GoldenTrajectoryFile.Serialize(recorded);
        var at = text.LastIndexOf("\"goal\": \"say hello\"", StringComparison.Ordinal);
        var tampered = string.Concat(text.AsSpan(0, at), "\"goal\": \"say goodbye\"", text.AsSpan(at + "\"goal\": \"say hello\"".Length));

        var report = ReplayRunner.Verify(GoldenTrajectoryFile.Parse(tampered), Ct);

        Assert.False(report.Passed);
        Assert.True(report.Checks[0].Passed);
        Assert.Equal("$.goal: expected \"say goodbye\", actual \"say hello\"", Assert.Single(report.Checks[1].Differences));
        Assert.Contains("through 3", report.Describe());
    }

    [Fact]
    public void An_unrecorded_check_fails()
    {
        var report = ReplayRunner.Verify(GoldenTrajectoryFile.Parse(Fixture), Ct);

        Assert.False(report.Passed);
        Assert.All(report.Checks, check => Assert.Contains("not been recorded", Assert.Single(check.Differences)));
    }

    [Fact]
    public void A_trajectory_without_checks_fails()
    {
        var report = ReplayRunner.Verify(Recorded() with { Checks = [] }, Ct);

        Assert.False(report.Passed);
        Assert.Contains("no checks", Assert.Single(report.Failures));
    }

    [Fact]
    public void A_check_past_the_head_is_reported_not_thrown()
    {
        var recorded = Recorded();
        var report = ReplayRunner.Verify(recorded with { Checks = [.. recorded.Checks, new GoldenCheck(9, false, null)] }, Ct);

        Assert.False(report.Passed);
        Assert.Contains("does not project", Assert.Single(report.Checks[^1].Differences));
    }

    [Fact]
    public void Every_position_projects_the_prefix()
    {
        using var session = ReplaySession.Create(Recorded(), Ct);

        Assert.Equal(3, session.Head);
        Assert.Equal([0L, 1L, 2L, 3L], Enumerable.Range(0, 4).Select(i => session.Project(i, cancellationToken: Ct).Head));
        Assert.Null(session.Project(1, cancellationToken: Ct).Goal);
        Assert.Equal("say hello", session.Project(2, cancellationToken: Ct).Goal);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Project(4, cancellationToken: Ct));
    }

    [Fact]
    public void The_temporary_log_is_removed_on_dispose()
    {
        string path;
        using (var session = ReplaySession.Create(Recorded(), Ct))
        {
            path = session.DatabasePath!;
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void A_caller_supplied_log_must_not_already_hold_the_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-replay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SqliteEventLog(Path.Combine(root, "events.db"));
            using (var session = ReplaySession.Create(Recorded(), log, "s1", Ct))
            {
                Assert.Null(session.DatabasePath);
                Assert.Equal("say hello", session.Project(3, cancellationToken: Ct).Goal);
            }

            Assert.Throws<EventLogConflictException>(() => ReplaySession.Create(Recorded(), log, "s1", Ct));
            Assert.Throws<EventLogConflictException>(() => ReplaySession.Create(Recorded() with { Events = [] }, log, "s1", Ct));
            // Disposing a session over a caller's log leaves the log alone.
            Assert.Equal(3, log.GetHead("s1", Ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Snapshot_json_is_independent_of_dictionary_order()
    {
        var events = new SessionEvent[]
        {
            new ApprovalRequested("b", "x", "y"),
            new ApprovalRequested("a", "x", "y"),
            new ModelSwitched("vision", Json("null"), Json("""{"n":1}"""), "start"),
            new ModelSwitched("generator", Json("null"), Json("""{"n":2}"""), "start"),
        };
        var stored = events.Select((payload, i) => new StoredEvent("s", i + 1L, Guid.NewGuid(), DateTimeOffset.UnixEpoch, payload));

        var json = SnapshotJson.ToJson(SessionProjection.Build("s", stored));

        Assert.Equal(["a", "b"], json.GetProperty("pending_approvals").EnumerateArray().Select(item => item.GetProperty("approval_id").GetString()));
        Assert.Equal(["generator", "vision"], json.GetProperty("model_profiles").EnumerateArray().Select(item => item.GetProperty("slot").GetString()));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class GoldenTrajectoryFileTests
{
    private const string Valid = """
        {
          "format": "corvid-golden-trajectory", "format_version": 1, "payload_version": 1,
          "id": "x", "description": "",
          "events": [ { "type": "user_message", "text": "hi" } ],
          "checks": [ { "through": 1, "expected": { "head": 1 } } ]
        }
        """;

    [Fact]
    public void Parses_events_in_the_persisted_format()
    {
        var trajectory = GoldenTrajectoryFile.Parse(Valid);

        Assert.Equal("hi", Assert.IsType<UserMessage>(Assert.Single(trajectory.Events)).Text);
        var check = Assert.Single(trajectory.Checks);
        Assert.Equal(1, check.Through);
        Assert.False(check.ExpandCompactions);
        Assert.Equal(1, check.Expected!.Value.GetProperty("head").GetInt64());
    }

    [Fact]
    public void Serialize_round_trips()
    {
        var trajectory = GoldenTrajectoryFile.Parse(Valid);
        var text = GoldenTrajectoryFile.Serialize(trajectory);
        var again = GoldenTrajectoryFile.Parse(text);

        Assert.Equal(text, GoldenTrajectoryFile.Serialize(again));
        Assert.Equal(trajectory.Events, again.Events);
    }

    [Theory]
    [InlineData("\"format_version\": 1", "\"format_version\": 2", "format version")]
    [InlineData("\"format\": \"corvid-golden-trajectory\"", "\"format\": \"other\"", "format")]
    [InlineData("\"payload_version\": 1", "\"payload_version\": 2", "payload version 2")]
    [InlineData("\"payload_version\": 1", "\"payload_version\": 4294967297", "payload_version")]
    [InlineData("\"description\": \"\"", "\"description\": \"\", \"extra\": true", "$.extra")]
    [InlineData("\"through\": 1", "\"through\": 2", "through must be between")]
    [InlineData("\"through\": 1", "\"through\": -1", "through must be between")]
    [InlineData("\"user_message\"", "\"user_said\"", "$.events[0]")]
    [InlineData(", \"expected\": { \"head\": 1 }", "", "expected is required")]
    [InlineData("\"through\": 1,", "\"through\": 1, \"expand\": true,", "$.checks[0].expand")]
    [InlineData("\"id\": \"x\"", "\"id\": \"\"", "$.id")]
    public void Rejects_what_version_1_does_not_define(string find, string replace, string message)
    {
        Assert.Contains(find, Valid);
        var exception = Assert.Throws<InvalidDataException>(() => GoldenTrajectoryFile.Parse(Valid.Replace(find, replace, StringComparison.Ordinal)));
        Assert.Contains(message, exception.Message);
    }
}

public sealed class JsonDiffTests
{
    private static IReadOnlyList<string> Diff(string expected, string actual)
    {
        using var left = JsonDocument.Parse(expected);
        using var right = JsonDocument.Parse(actual);
        return JsonDiff.Compare(left.RootElement, right.RootElement);
    }

    [Fact]
    public void Equal_values_with_different_number_formatting_match() =>
        Assert.Empty(Diff("""{"a":[1,0.5,true,null,"s"]}""", """{"a":[1.0,5e-1,true,null,"s"]}"""));

    [Fact]
    public void Reports_paths_for_each_kind_of_difference() =>
        Assert.Equal(
        [
            "$.a: expected 1, actual 2",
            "$.b: expected \"x\", actual is missing",
            "$.c: expected 2 items, actual 1",
            "$.d.e: expected true, actual false",
            "$.f: expected null, actual {}",
            "$.g: not expected, actual 3",
        ],
        Diff("""{"a":1,"b":"x","c":[1,2],"d":{"e":true},"f":null}""", """{"a":2,"c":[1],"d":{"e":false},"f":{},"g":3}"""));
}
