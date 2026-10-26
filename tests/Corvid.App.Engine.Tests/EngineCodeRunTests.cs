using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A7: a code mode run on the engine: the approval card carries the run's whole script (from the prompt's in-memory
/// arguments), the run's call brings its script, the script's calls name their run live and on reopening, and the run's
/// end says how it ended.
/// </summary>
public sealed class EngineCodeRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Tools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    [Fact]
    public async Task A_run_shows_its_script_on_its_cards_and_its_calls_under_it_live_and_reopened()
    {
        // The JSON escape puts a right-to-left override in the script.
        var models = new FakeModels(new FakeGenerator(
            Reply.Call("execute_code", """{"language":"python","code":"import corvid_tools as t\nprint(t.list_notes())  # \u202e"}"""),
            Reply.Say("The script listed your notes.")),
            // Code mode is offered only to a generator outside this process (plan §15).
            generatorPrivacy: Corvid.Models.PrivacyClass.LocalServer);
        // A7 e2e: the cards show the configured call limit, not the default 50.
        await using var engine = await CustomEngine.StartAsync(models,
            new Dictionary<string, string?> { ["Corvid:Approvals:Mode"] = "Manual", ["Corvid:Tools:Code:MaxCalls"] = "7" },
            services => services.AddSingleton<IToolProvider>(new Tools(new FakeCodeTool(("list_notes", "{}")))));
        var id = engine.Runtime.OpenSession(surface: "app").Id;
        using var service = engine.Sessions();
        var waiter = new SessionWaiter();
        using (await service.OpenAsync(id, waiter.Add, Ct))
        {
            await service.SendAsync(id, "run the weather script over my notes", Ct);
            var request = await waiter.WaitAsync<ApprovalRequested>();

            var code = Assert.IsType<CodeRun>(request.Code);
            Assert.Equal(("Python", "import corvid_tools as t\nprint(t.list_notes())  # \u202e"), (code.Language, code.Source));
            Assert.Equal(7, code.MaxCalls);
            Assert.Contains("at most 7 tool calls", code.Limits, StringComparison.Ordinal);
            Assert.Contains("no network", code.Limits, StringComparison.Ordinal);
            // The run's card, announced by its prompt, shows the script while it waits.
            var waiting = SessionFold.Replay(id, waiter.Updates.ToList()).Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == request.CallId);
            Assert.Equal(code, waiting.Code);

            await service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Once), Ct);
            await waiter.WaitAsync<TurnEnded>();
            await service.IdleAsync();

            var live = waiter.Updates.ToList();
            var run = live.OfType<ToolCall>().Last(call => call.Tool == "execute_code");
            Assert.NotNull(run.Code);
            Assert.Null(run.ParentCallId);
            var nested = live.OfType<ToolCall>().Where(call => call.Tool == "list_notes").ToList();
            Assert.NotEmpty(nested);
            Assert.All(nested, call => Assert.Equal(run.CallId, call.ParentCallId));
            Assert.Contains(live.OfType<ToolCallUpdate>(), change => change.CallId == run.CallId && change.CodeOutcome == "exit 0");
            AssertNested(SessionFold.Replay(id, live), run.CallId);
        }

        // Reopened: the replay brings the same nesting.
        var again = new SessionWaiter();
        using var reopened = await service.OpenAsync(id, again.Add, Ct);
        var view = SessionFold.Replay(id, [.. reopened.History, .. again.Updates]);
        AssertNested(view, view.Transcript.OfType<ToolEntry>().Single(tool => tool.Tool == "execute_code").CallId);
    }

    [Fact]
    public async Task Review_names_the_run_a_scripts_change_came_from()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = fixture.Session();
        fixture.Append(id, new Corvid.Abstractions.Events.UserMessage("run the script"),
            new Corvid.Abstractions.Events.ToolCallIssued("c1", "execute_code",
                System.Text.Json.JsonSerializer.SerializeToElement(new { language = "python", code = "t.write_file('a.txt', 'x')" })));
        // The script's write, as the turn records a nested call: issued under its run, the ledger's checkpoints around it.
        fixture.Append(id, new Corvid.Abstractions.Events.ToolCallIssued("c1.1", "write_file",
            System.Text.Json.JsonSerializer.SerializeToElement(new { path = "a.txt", content = "x\n" }), ParentCallId: "c1"));
        var analysis = fixture.Runtime.Approvals.Analyze(new Corvid.Approvals.ShellAction("rm a.txt"));
        var before = await fixture.Runtime.Checkpoints!.BeforeAsync("c1.1", analysis);
        fixture.Append(id, before);
        fixture.Write("a.txt", "x\n");
        fixture.Append(id, await fixture.Runtime.Checkpoints.AfterAsync("c1.1", before, analysis),
            new Corvid.Abstractions.Events.ToolResult("c1.1", System.Text.Json.JsonSerializer.SerializeToElement(new { path = "a.txt" })),
            new Corvid.Abstractions.Events.ToolResult("c1", System.Text.Json.JsonSerializer.SerializeToElement(new { calls = 1, exit_code = 0 })),
            new Corvid.Abstractions.Events.AssistantMessage("Done."));
        using var review = fixture.Review();

        var changes = await review.GetChangesAsync(id, Corvid.App.Core.Review.ReviewScope.ThisSession, Ct);

        var file = Assert.Single(changes.Files);
        Assert.Equal(("a.txt", "write_file", "execute_code"), (file.Path, file.Tool, file.Via));
    }

    private static void AssertNested(SessionView view, string runId)
    {
        var tools = view.Transcript.OfType<ToolEntry>().ToList();
        var run = tools.Single(tool => tool.CallId == runId);
        Assert.NotNull(run.Code);
        Assert.Equal(1, run.NestedCalls);
        Assert.Equal("exit 0", run.CodeOutcome);
        var at = tools.IndexOf(run);
        Assert.Equal(("list_notes", runId), (tools[at + 1].Tool, tools[at + 1].ParentCallId));
    }
}
