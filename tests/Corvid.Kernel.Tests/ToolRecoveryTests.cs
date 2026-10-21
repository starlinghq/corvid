using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;

namespace Corvid.Kernel.Tests;

public sealed class ToolRecoveryTests
{
    /// <summary>A turn built call by call; each result follows its call.</summary>
    private sealed class Turn
    {
        private readonly List<StoredEvent> _events = [];
        private int _calls;

        public IReadOnlyList<StoredEvent> Events => _events;

        public Turn Ok(string tool, string arguments = "{}") => Call(tool, arguments, JsonSerializer.SerializeToElement("done"), false);

        public Turn Fails(string tool, string arguments = "{}", string error = ToolErrorKinds.ToolFailed) =>
            Call(tool, arguments, JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = error, ["details"] = new[] { "boom" } }), true);

        /// <summary>An error result without the kernel's shape, as a tool or MCP server may return.</summary>
        public Turn FailsPlainly(string tool) => Call(tool, "{}", JsonSerializer.SerializeToElement("HTTP 500"), true);

        /// <summary>Two calls issued in one round, then both results.</summary>
        public Turn Parallel(string failing, string succeeding)
        {
            var (first, second) = ($"c{++_calls}", $"c{++_calls}");
            Add(new ToolCallIssued(first, failing, Json("{}")));
            Add(new ToolCallIssued(second, succeeding, Json("{}")));
            Add(new ToolResult(first, JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["error"] = ToolErrorKinds.ToolFailed }), true));
            Add(new ToolResult(second, Json("\"ok\"")));
            return this;
        }

        private Turn Call(string tool, string arguments, JsonElement result, bool error)
        {
            var id = $"c{++_calls}";
            Add(new ToolCallIssued(id, tool, Json(arguments)));
            Add(new ToolResult(id, result, error));
            return this;
        }

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
        // web_fetch's success is another family; memory_search recovers memory_add; the last cron failure has nothing after it.
        var turn = new Turn().Fails("read_file").Ok("web_fetch").FailsPlainly("memory_add").Ok("search_memory").Fails("cron_create");

        var scan = ToolRecovery.Scan(turn.Events);

        Assert.Equal((5, 3, 2), (scan.Calls, scan.Failures, scan.Unrecovered));
        Assert.Equal(["memory_add failed, then search_memory succeeded"], scan.Recoveries.Select(step => step.ToString()));
    }

    [Fact]
    public void A_call_in_the_same_round_does_not_respond_to_the_failure()
    {
        var scan = ToolRecovery.Scan(new Turn().Parallel("read_file", "list_files").Events);

        Assert.Equal(1, scan.Unrecovered);
        Assert.Empty(scan.Recoveries);
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
