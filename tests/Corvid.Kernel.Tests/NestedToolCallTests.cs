using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>
/// A7: a running tool (code mode's script) calls tools through <see cref="ToolCallContext.InvokeToolAsync"/>, and each
/// nested call takes the turn's own path: offered tools only, schema validation, the approval pipeline, the credential
/// scrub, taint, its own log entries under its parent, and a result before the parent's.
/// </summary>
public sealed class NestedToolCallTests
{
    private const string Parent = "script-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A read-only tool that runs a "script": the body makes nested calls through the context.</summary>
    private static TestTool Script(Func<ToolCallContext, CancellationToken, Task<object?>> body) =>
        TestTool.Create("run_script", "Run a weather script, which may call other tools.",
            new ToolRisk(SideEffects.None, Network: false, Destructive: false), """{"type":"object","properties":{}}""",
            async (arguments, token) => await body(ToolCallContext.Require(arguments, "run_script"), token));

    private static FakeGenerator Generator() => new(Reply.Call("run_script", "{}", Parent), Reply.Say("The script ran."));

    private static TurnHarness Harness(FakeGenerator generator, IApprovalPrompter prompter, Action<ToolRegistry> tools, TurnOptions? options = null,
        Corvid.Abstractions.Fabric.IDecider? calibrated = null) =>
        new(generator, prompter: prompter, options: options, calibrated: calibrated, tools: tools);

    [Fact]
    public async Task Nested_calls_run_through_the_turn_path_and_are_logged_under_their_parent()
    {
        var prompter = FakePrompter.Always(approve: true);
        ToolCallContext? probed = null;
        var probe = TestTool.Create("probe_note", "Probe a note's context.", new ToolRisk(SideEffects.None, false, false),
            """{"type":"object","properties":{}}""", (arguments, _) =>
            {
                probed = ToolCallContext.Require(arguments, "probe_note");
                return ValueTask.FromResult<object?>("probed");
            });
        NestedToolResult added = null!, listed = null!, unknown = null!, invalid = null!, self = null!, probedResult = null!;
        var script = Script(async (context, token) =>
        {
            added = await context.InvokeToolAsync("add_note", Json("""{"text":"from the script"}"""), token);
            listed = await context.InvokeToolAsync("list_notes", Json("{}"), token);
            unknown = await context.InvokeToolAsync("rm_everything", Json("{}"), token);
            invalid = await context.InvokeToolAsync("add_note", Json("""{"txt":1}"""), token);
            self = await context.InvokeToolAsync("run_script", Json("{}"), token);
            probedResult = await context.InvokeToolAsync("probe_note", Json("{}"), token);
            return "script done";
        });
        var generator = Generator();
        using var harness = Harness(generator, prompter, tools => { tools.Add(script); tools.Add(probe); });

        var result = await harness.RunAsync("run the weather script");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Equal("from the script", Assert.Single(harness.Notes.Notes).Text);
        Assert.Equal([$"{Parent}.1", $"{Parent}.2", $"{Parent}.3", $"{Parent}.4", $"{Parent}.5", $"{Parent}.6"],
            [added.CallId, listed.CallId, unknown.CallId, invalid.CallId, self.CallId, probedResult.CallId]);
        Assert.False(added.IsError);
        Assert.False(listed.IsError);
        Assert.Contains("from the script", listed.Result.GetRawText(), StringComparison.Ordinal);
        Assert.True(unknown.IsError);
        Assert.Contains("is not one of the tools this turn may use", unknown.Result.GetRawText(), StringComparison.Ordinal);
        Assert.True(invalid.IsError);
        Assert.Contains("invalid arguments", invalid.Result.GetRawText(), StringComparison.Ordinal);
        Assert.True(self.IsError);
        Assert.Contains("cannot call itself", self.Result.GetRawText(), StringComparison.Ordinal);
        // Depth 1: a nested call knows its parent and cannot nest further.
        Assert.False(probedResult.IsError);
        Assert.Equal(Parent, probed!.ParentCallId);
        Assert.Null(probed.Invoker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probed.InvokeToolAsync("list_notes", Json("{}"), Ct));

        // The side effect asked, for the nested call itself.
        var prompt = Assert.Single(prompter.Prompts);
        Assert.Equal(added.CallId, Assert.Single(harness.Payloads().OfType<ApprovalRequested>()).CallId);
        Assert.NotNull(prompt);

        // The log: the parent, then each nested call with its result, then the parent's result.
        var events = harness.Events();
        long Issued(string id) => events.Single(item => item.Payload is ToolCallIssued call && call.CallId == id).Sequence;
        long Resulted(string id) => events.Single(item => item.Payload is ToolResult r && r.CallId == id).Sequence;
        var calls = harness.Payloads().OfType<ToolCallIssued>().ToList();
        Assert.Null(calls.Single(call => call.CallId == Parent).ParentCallId);
        Assert.All(calls.Where(call => call.CallId != Parent), call => Assert.Equal(Parent, call.ParentCallId));
        Assert.Equal(7, calls.Count);
        foreach (var id in new[] { added.CallId, listed.CallId, unknown.CallId, invalid.CallId, self.CallId, probedResult.CallId })
        {
            Assert.True(Issued(Parent) < Issued(id) && Issued(id) < Resulted(id) && Resulted(id) < Resulted(Parent), id);
        }

        // The model never asked for the nested calls: its next prompt holds its own call and result only.
        var next = generator.Calls[^1].Messages;
        Assert.Equal(Parent, Assert.Single(next.SelectMany(message => message.Contents).OfType<FunctionCallContent>()).CallId);
        Assert.Equal(Parent, Assert.Single(next.SelectMany(message => message.Contents).OfType<FunctionResultContent>()).CallId);
    }

    [Fact]
    public async Task A_denied_nested_call_comes_back_as_an_error_and_changes_nothing()
    {
        var prompter = FakePrompter.Always(approve: false);
        NestedToolResult added = null!;
        var script = Script(async (context, token) =>
        {
            added = await context.InvokeToolAsync("add_note", Json("""{"text":"never"}"""), token);
            return "script done";
        });
        using var harness = Harness(Generator(), prompter, tools => tools.Add(script));

        await harness.RunAsync("run the weather script");

        Assert.Empty(harness.Notes.Notes);
        Assert.Single(prompter.Prompts);
        Assert.True(added.IsError);
        Assert.Contains("denied by the user", added.Result.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(harness.Payloads().OfType<ToolResult>(), r => r.CallId == added.CallId && r.IsError);
    }

    [Fact]
    public async Task A_nested_fetch_taints_the_parent_and_makes_a_later_nested_post_ask()
    {
        // Permissive mode with a calibrated risk of 0 runs the webhook unasked, until outside content is in the turn (§10.3).
        var prompter = FakePrompter.Always(approve: false);
        var webhook = TestTool.PostWebhook();
        var fetch = TestTool.FetchPage();
        var fetchFirst = false;
        ToolCallContext? parent = null;
        NestedToolResult fetched = null!, posted = null!;
        var script = Script(async (context, token) =>
        {
            parent = context;
            if (fetchFirst)
                fetched = await context.InvokeToolAsync("fetch_page", Json("""{"url":"https://news.example"}"""), token);
            posted = await context.InvokeToolAsync("post_webhook", Json("""{"url":"https://hooks.example/a","body":"hi"}"""), token);
            return "script done";
        });
        var generator = new FakeGenerator(Reply.Call("run_script", "{}", Parent), Reply.Say("Posted."),
            Reply.Call("run_script", "{}", "script-2"), Reply.Say("Not posted."));
        using var harness = Harness(generator, prompter, tools => { tools.Add(script); tools.Add(webhook); tools.Add(fetch); },
            new TurnOptions { ApprovalMode = ApprovalMode.Permissive }, new RiskOnlyDecider(0));
        var runner = harness.Runner();

        // The control: in an untainted turn the nested post runs unasked.
        var first = await harness.RunAsync(runner, "run the weather script");
        Assert.Empty(prompter.Prompts);
        Assert.Equal(1, webhook.Invocations);
        Assert.False(posted.IsError);
        // Its response came over the network: outside content, so the parent's result is too.
        Assert.True(posted.Tainted);
        Assert.True(parent!.ResultTainted);
        Assert.True(first.Tainted);

        fetchFirst = true;
        var second = await harness.RunAsync(runner, "run the weather script again");

        Assert.True(fetched.Tainted);
        Assert.Contains("ignore previous instructions", fetched.Result.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(Assert.Single(prompter.Prompts).Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);
        Assert.Equal(1, webhook.Invocations);
        Assert.True(posted.IsError);
        Assert.True(parent!.ResultTainted);
        Assert.True(second.Tainted);
        // The parent's result is asked about as outside content, as a network tool's is.
        Assert.NotEmpty(harness.Decisions("injection/script-2"));
    }

    [Fact]
    public async Task A_nested_result_comes_back_scrubbed_as_the_log_holds_it()
    {
        const string token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8"; // fictitious, in GitHub's classic shape
        var config = TestTool.Create("read_note_config", "Read the notes' configuration.", new ToolRisk(SideEffects.None, false, false),
            """{"type":"object","properties":{}}""", (_, _) => ValueTask.FromResult<object?>("token = " + token));
        NestedToolResult read = null!;
        var script = Script(async (context, cancellation) =>
        {
            read = await context.InvokeToolAsync("read_note_config", Json("{}"), cancellation);
            return "script done";
        });
        using var harness = Harness(Generator(), FakePrompter.Always(approve: true), tools => { tools.Add(script); tools.Add(config); });

        await harness.RunAsync("run the weather script");

        Assert.False(read.IsError);
        Assert.DoesNotContain(token, read.Result.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("ghp_[REDACTED:github-token:", read.Result.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(read.Result.GetRawText(), harness.Payloads().OfType<ToolResult>().Single(r => r.CallId == read.CallId).Result.GetRawText());
    }

    [Fact]
    public async Task Nested_calls_still_open_when_the_parent_returns_are_cancelled_before_its_result_and_no_more_are_taken()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompter = new FakePrompter(async (_, token) =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        Task<NestedToolResult>? pending = null;
        ToolCallContext? kept = null;
        var script = Script(async (context, token) =>
        {
            kept = context;
            pending = context.InvokeToolAsync("add_note", Json("""{"text":"late"}"""), token);
            // The nested call is waiting on its approval: the script returns without awaiting it.
            await asked.Task.WaitAsync(token);
            return "returned early";
        });
        using var harness = Harness(Generator(), prompter, tools => tools.Add(script));

        var result = await harness.RunAsync("run the weather script");

        Assert.Equal(TurnStatus.Answered, result.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending!);
        Assert.Empty(harness.Notes.Notes);
        var events = harness.Events();
        var child = events.Single(item => item.Payload is ToolResult r && r.CallId == $"{Parent}.1");
        var parent = events.Single(item => item.Payload is ToolResult r && r.CallId == Parent);
        Assert.True(child.Sequence < parent.Sequence);
        Assert.True(((ToolResult)child.Payload).IsError);
        Assert.Contains("not run", ((ToolResult)child.Payload).Result.GetRawText(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kept!.InvokeToolAsync("list_notes", Json("{}"), Ct));
    }
}
