using System.Diagnostics;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel;
using Corvid.Tools;

namespace Corvid.Host.Tests;

/// <summary>A7: what every surface shows of a code mode run: its language, source and usage, and its approval card's lines.</summary>
public sealed class CodeRunSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_run_is_read_from_its_arguments_and_its_usage_from_its_result()
    {
        var run = CodeRunPresentation.Read("execute_code", Json("""{"language":"js","code":"console.log(1)\nconsole.log(2)\n"}"""))!;

        Assert.Equal(("js", "JavaScript", 2), (run.Language, run.DisplayLanguage, run.Lines));
        Assert.Null(CodeRunPresentation.Read("shell", Json("""{"code":"x"}""")));
        Assert.Null(CodeRunPresentation.Read("execute_code", Json("""{"language":"python"}""")));
        Assert.Equal("ruby", CodeRunPresentation.Read("execute_code", Json("""{"language":"ruby","source":"puts 1"}"""))!.DisplayLanguage);
        Assert.Equal("3 of 50 tool calls · 1 KB of 8 MB results · exit 1 · timed out",
            CodeRunPresentation.Usage(0, Json("""{"calls":3,"response_bytes":1024,"exit_code":1,"timed_out":true}""")));
        Assert.Equal("2 of 50 tool calls", CodeRunPresentation.Usage(2, Json("\"done\"")));
        // The code tool's result: "tool_calls" counts, "calls" lists them.
        Assert.Equal("1 of 50 tool calls · exit 0",
            CodeRunPresentation.Usage(0, Json("""{"language":"python","exit_code":0,"stdout":"","tool_calls":1,"calls":[{"id":"c1.1","tool":"read_file","ok":true}]}""")));
        Assert.Equal("", CodeRunPresentation.Outcome(null));
    }

    private static ApprovalPrompt Prompt(string tool, string arguments)
    {
        var descriptor = new ToolDescriptor(tool, "Run a script.", """{"type":"object","properties":{}}""",
            new ToolRisk(SideEffects.Process, Network: false, Destructive: false), []);
        return new ApprovalPrompt("a-1", "s-1", new ActionAnalysis("tool", tool, [], null), [], [ApprovalScope.Once], DateTimeOffset.UnixEpoch)
        {
            Action = new ToolAction(descriptor, Json(arguments)),
        };
    }

    [Fact]
    public void The_approval_card_shows_every_line_of_the_source_with_hidden_characters_as_escapes()
    {
        // A right-to-left override, a lone carriage return and a line separator, written as JSON escapes.
        var prompt = Prompt("execute_code", """{"language":"python","code":"a = 1 # \u202e\r\nb = 2\rc = 3\u2028d = 4\n"}""");

        var lines = CodeRunCard.Lines(prompt);

        Assert.StartsWith("Code run: Python, 2 lines; at most 50 tool calls", lines[0], StringComparison.Ordinal);
        Assert.Equal(CodeRunPresentation.EachCallAsked, lines[1]);
        Assert.Equal("The source holds hidden characters, shown below as escapes.", lines[2]);
        Assert.Equal(["Source:", "  a = 1 # \\u202E", "  b = 2\\u000Dc = 3\\u2028d = 4"], lines.Skip(3));
        Assert.Empty(CodeRunCard.Lines(Prompt("shell", """{"command":"ls"}""")));
        Assert.Equal(["Code run: the call names no script, so nothing would run."], CodeRunCard.Lines(Prompt("execute_code", "{}")));
    }

    [Fact]
    public async Task A_running_code_run_is_announced_with_its_script_and_other_calls_without_input()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, new ToolPresentation(runtime), runtime.Log.GetHead(session.Id, Ct), update =>
        {
            lock (live)
                live.Add(update);
        });
        SessionEvent[] issued =
        [
            new UserMessage("run it"),
            new ToolCallIssued("c1", "execute_code", Json("""{"language":"python","code":"print(1)"}"""), 1),
            new ToolCallIssued("c2", "list_notes", Json("{}"), 1, true),
        ];

        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, Ct), [.. issued.Select(EventData.Create)], Ct);
            foreach (var (id, tool) in new[] { ("c1", "execute_code"), ("c2", "list_notes") })
                using (var span = KernelTelemetry.Source.StartActivity(ActivityKind.Internal, tags:
                       [
                           new(KernelTelemetry.OperationName, "execute_tool"),
                           new(KernelTelemetry.ToolName, tool),
                           new(KernelTelemetry.ToolCallId, id),
                           new(KernelTelemetry.ConversationId, session.Id),
                           new(KernelTelemetry.ToolApproval, false),
                       ], name: KernelTelemetry.ExecuteToolPrefix + tool))
                {
                    Assert.NotNull(span);
                    span.SetTag(KernelTelemetry.ToolOutcome, "succeeded");
                }
            await reporter.FinishAsync(null);
        }

        var started = live.OfType<ToolCallStarted>().ToDictionary(update => update.CallId);
        Assert.Equal("print(1)", started["c1"].Input!.Value.GetProperty("code").GetString());
        Assert.Null(started["c2"].Input);
    }
}
