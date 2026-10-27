using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;

namespace Corvid.Kernel.Tests;

public sealed class ToolRecoveryTests
{
    /// <summary>A turn built call by call; each call is its own round and its result follows it.</summary>
    private sealed class Turn(bool rounds = true)
    {
        // The read-only tools among those used here, as their descriptors would record it.
        private static readonly HashSet<string> Reads =
            ["read_file", "list_files", "search_files", "search_memory", "web_fetch", "github__get_issue", "github__search_issues"];

        private readonly List<StoredEvent> _events = [];
        private int _calls;
        private int _round;

        public IReadOnlyList<StoredEvent> Events => _events;

        public Turn Ok(string tool, string arguments = "{}") => Call(tool, arguments, JsonSerializer.SerializeToElement("done"), false);

        public Turn Fails(string tool, string arguments = "{}", string error = ToolErrorKinds.ToolFailed) =>
            Call(tool, arguments, JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = error, ["details"] = new[] { "boom" } }), true);

        /// <summary>An error result without the kernel's shape, as a tool or MCP server may return.</summary>
        public Turn FailsPlainly(string tool) => Call(tool, "{}", JsonSerializer.SerializeToElement("HTTP 500"), true);

        /// <summary>Two calls issued in one round, then both results: read-only calls run in parallel.</summary>
        public Turn Parallel(string failing, string succeeding)
        {
            var (first, second) = ($"c{++_calls}", $"c{++_calls}");
            _round++;
            Add(Issued(first, failing, "{}"));
            Add(Issued(second, succeeding, "{}"));
            Add(new ToolResult(first, Failure, true));
            Add(new ToolResult(second, Json("\"ok\"")));
            return this;
        }

        /// <summary>Two calls of one round run one at a time, as calls that need approval are: each is issued when it starts.</summary>
        public Turn Sequential(string failing, string failingArguments, string succeeding, string succeedingArguments)
        {
            var (first, second) = ($"c{++_calls}", $"c{++_calls}");
            _round++;
            Add(Issued(first, failing, failingArguments));
            Add(new ToolResult(first, Failure, true));
            Add(Issued(second, succeeding, succeedingArguments));
            Add(new ToolResult(second, Json("\"ok\"")));
            return this;
        }

        /// <summary>A7: a call the previous call's script made (code mode), with its result.</summary>
        public Turn Nested(string tool, bool fails)
        {
            var parent = $"c{_calls}";
            var id = $"{parent}.{_events.Count}";
            Add(new ToolCallIssued(id, tool, Json("{}"), _round, Reads.Contains(tool), ParentCallId: parent));
            Add(new ToolResult(id, fails ? Failure : Json("\"ok\""), fails));
            return this;
        }

        private Turn Call(string tool, string arguments, JsonElement result, bool error)
        {
            var id = $"c{++_calls}";
            _round++;
            Add(Issued(id, tool, arguments));
            Add(new ToolResult(id, result, error));
            return this;
        }

        // Without rounds, as events written before C-026 are: no round and no read-only flag.
        private ToolCallIssued Issued(string id, string tool, string arguments) => rounds
            ? new ToolCallIssued(id, tool, Json(arguments), _round, Reads.Contains(tool))
            : new ToolCallIssued(id, tool, Json(arguments));

        private static JsonElement Failure =>
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["error"] = ToolErrorKinds.ToolFailed });

        private void Add(SessionEvent payload) =>
            _events.Add(new StoredEvent("s", _events.Count + 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, payload));

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public void A_failure_recovered_by_another_tool_of_its_family_is_a_recovery()
    {
        var turn = new Turn().Fails("read_file", """{"path":"a.txt"}""").Ok("search_files", """{"query":"a"}""");

        var scan = ToolRecovery.Scan(turn.Events);

        Assert.Equal((2, 1, 0), (scan.Calls, scan.Failures, scan.Unrecovered));
        var step = Assert.Single(scan.Recoveries);
        Assert.Equal(new RecoveryStep("read_file", 1, "search_files", 3), step);
        Assert.Equal("read_file failed, then search_files succeeded", scan.Describe());
    }

    [Fact]
    public void The_same_name_with_different_arguments_is_a_recovery()
    {
        var scan = ToolRecovery.Scan(new Turn().Fails("read_file", """{"path":"a.txt"}""").Ok("read_file", """{"path":"docs/a.txt"}""").Events);

        Assert.Single(scan.Recoveries);
    }

    [Fact]
    public void Repeating_the_same_call_resolves_the_failure_but_is_no_recovery()
    {
        var scan = ToolRecovery.Scan(new Turn().Fails("read_file", """{"path":"a.txt"}""").Ok("read_file", """{ "path" : "a.txt" }""").Events);

        Assert.Equal(0, scan.Unrecovered);
        Assert.Empty(scan.Recoveries);
        Assert.Null(scan.Describe());
    }

    [Theory]
    [InlineData(ToolErrorKinds.NotRun)]
    [InlineData(ToolErrorKinds.Cancelled)]
    [InlineData(ToolErrorKinds.InvalidArguments)]
    public void Denials_cancellations_and_rejected_arguments_are_not_counted(string error)
    {
        var scan = ToolRecovery.Scan(new Turn().Fails("read_file", error: error).Ok("search_files").Events);

        Assert.Equal((0, 0), (scan.Failures, scan.Unrecovered));
        Assert.Empty(scan.Recoveries);
    }

    [Fact]
    public void A_failure_with_no_later_success_in_its_family_is_unrecovered()
    {
        // web_fetch's success is another family; memory_set resolves memory_add; the last cron failure has nothing after it.
        var turn = new Turn().Fails("read_file").Ok("web_fetch").FailsPlainly("memory_add").Ok("memory_set").Fails("cron_create");

        var scan = ToolRecovery.Scan(turn.Events);

        Assert.Equal((5, 3, 2), (scan.Calls, scan.Failures, scan.Unrecovered));
        Assert.Equal(["memory_add failed, then memory_set succeeded"], scan.Recoveries.Select(step => step.ToString()));
    }

    // A7 review (M2): a code run's nested calls are its own; the procedure is the model's calls, which a skill replays.
    [Fact]
    public void A_scripts_nested_calls_are_neither_steps_nor_recoveries()
    {
        var turn = new Turn().Ok("execute_code").Nested("read_file", fails: true).Nested("search_files", fails: false).Nested("read_file", fails: false);

        var scan = ToolRecovery.Scan(turn.Events);

        Assert.Equal((1, 0, 0), (scan.Calls, scan.Failures, scan.Unrecovered));
        Assert.Empty(scan.Recoveries);
    }

    [Fact]
    public void A_call_in_the_same_round_does_not_respond_to_the_failure()
    {
        var scan = ToolRecovery.Scan(new Turn().Parallel("read_file", "list_files").Events);

        Assert.Equal(1, scan.Unrecovered);
        Assert.Empty(scan.Recoveries);
    }

    [Fact]
    public void A_later_call_of_the_same_response_run_after_approval_does_not_respond_to_the_failure()
    {
        // Review of C-026: write_file calls need approval and run one at a time, so the second is issued after the first's
        // result. Only the recorded round shows it came from the same response; file a was never written.
        var scan = ToolRecovery.Scan(new Turn().Sequential("write_file", """{"path":"a"}""", "write_file", """{"path":"b"}""").Events);

        Assert.Equal((2, 1, 1), (scan.Calls, scan.Failures, scan.Unrecovered));
        Assert.Empty(scan.Recoveries);
    }

    [Fact]
    public void Events_without_rounds_fall_back_to_the_order_of_the_log()
    {
        var scan = ToolRecovery.Scan(new Turn(rounds: false).Parallel("write_file", "write_file").Events);
        Assert.Equal(1, scan.Unrecovered);

        // Without a read-only flag only the same name resolves a failure, but the hint still names the family's success.
        scan = ToolRecovery.Scan(new Turn(rounds: false).Fails("read_file", """{"path":"a"}""").Ok("read_file", """{"path":"b"}""").Events);
        Assert.Equal(0, scan.Unrecovered);
        scan = ToolRecovery.Scan(new Turn(rounds: false).Fails("read_file").Ok("search_files").Events);
        Assert.Equal(1, scan.Unrecovered);
        Assert.Single(scan.Recoveries);
    }

    [Fact]
    public void A_read_after_a_failed_change_leaves_it_unrecovered()
    {
        // Review of C-026: the write failed, the agent looked at the file and gave up. The read is listed as what followed,
        // but tool_failures_recovered must not hold.
        var scan = ToolRecovery.Scan(new Turn().Fails("write_file", """{"path":"a"}""").Ok("read_file", """{"path":"a"}""").Events);

        Assert.Equal((2, 1, 1), (scan.Calls, scan.Failures, scan.Unrecovered));
        Assert.Equal("write_file failed, then read_file succeeded", scan.Describe());
    }

    [Theory]
    [InlineData("write_file", "patch_file")]
    [InlineData("write_file", "write_file")]
    [InlineData("read_file", "write_file")]
    [InlineData("read_file", "list_files")]
    public void A_later_change_or_a_read_after_a_failed_read_resolves_the_failure(string failed, string succeeded)
    {
        var scan = ToolRecovery.Scan(new Turn().Fails(failed, """{"path":"a"}""").Ok(succeeded, """{"path":"b"}""").Events);

        Assert.Equal(0, scan.Unrecovered);
        Assert.Single(scan.Recoveries);
    }

    [Theory]
    [InlineData("read_file", "file")]
    [InlineData("list_files", "file")]
    [InlineData("patch_file", "file")]
    [InlineData("memory_approve", "memory")]
    [InlineData("search_memory", "memory")]
    [InlineData("cron_update", "cron")]
    [InlineData("web_fetch", "web")]
    [InlineData("github__create_issue", "github")]
    [InlineData("fetch", "fetch")]
    public void Families_drop_verbs_and_plurals_and_use_the_mcp_server(string name, string family)
    {
        Assert.Equal([family], ToolRecovery.Family(name));
    }

    [Fact]
    public void Mcp_tools_share_their_server_family()
    {
        Assert.True(ToolRecovery.SameFamily("github__get_issue", "github__search_issues"));
        Assert.False(ToolRecovery.SameFamily("github__get_issue", "gitlab__get_issue"));
        Assert.False(ToolRecovery.SameFamily("read_file", "web_fetch"));
        Assert.True(ToolRecovery.SameFamily("fetch", "fetch"));
        Assert.False(ToolRecovery.SameFamily("fetch", "run"));
        var scan = ToolRecovery.Scan(new Turn().Fails("github__get_issue").Ok("github__search_issues").Events);
        Assert.Equal("github__get_issue failed, then github__search_issues succeeded", scan.Describe());
    }
}
