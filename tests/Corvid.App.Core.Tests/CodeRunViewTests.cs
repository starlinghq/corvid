using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// A7: a code mode run in the transcript: the script's calls sit under the run's card (live and on replay), and the run's
/// card and approval card show its language, its whole source with hidden characters revealed, and how much of its limits
/// it used.
/// </summary>
public sealed class CodeRunViewTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    // The source holds a right-to-left override (U+202E), written as an escape here.
    private static readonly CodeRun Script = new("Python", "import corvid_tools as t\nprint(t.list_notes())  # \u202e\n") { Limits = "at most 50 tool calls; no network" };

    private static T At<T>(T update, double seconds)
        where T : SessionUpdate => update with { At = Start.AddSeconds(seconds) };

    /// <summary>A run, a parallel read the model asked for after it, then the script's two calls and the ends.</summary>
    private static List<SessionUpdate> Turn() =>
    [
        At(new UserMessage("run the weather script") { Position = 1 }, 0),
        At(new TurnStarted(1, "fake-gen") { MaxSteps = 6 }, 0),
        At(new ToolCall("c1", "execute_code", ToolKind.Execute, ToolCallStatus.InProgress) { Code = Script }, 1),
        At(new ToolCall("c2", "read_file", ToolKind.Read, ToolCallStatus.InProgress) { Target = "notes.txt" }, 1.1),
        At(new ToolCall("c1.1", "list_notes", ToolKind.Read, ToolCallStatus.InProgress) { ParentCallId = "c1" }, 1.2),
        At(new ToolCallUpdate("c2") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded }, 1.3),
        At(new ToolCallUpdate("c1.1") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded }, 1.4),
        // A call first heard of by its update (announced late) still goes under its run.
        At(new ToolCallUpdate("c1.2") { ParentCallId = "c1", Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded }, 1.5),
        At(new ToolCallUpdate("c1") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, CodeOutcome = "exit 0" }, 2),
    ];

    [Fact]
    public void A_scripts_calls_fold_under_their_run_not_at_the_turns_foot()
    {
        var state = SessionFold.Replay("cli-code", Turn());

        var tools = state.Transcript.OfType<ToolEntry>().ToList();
        Assert.Equal(["c1", "c1.1", "c1.2", "c2"], tools.Select(tool => tool.CallId));
        Assert.Equal([null, "c1", "c1", null], tools.Select(tool => tool.ParentCallId));
        Assert.Equal(2, tools[0].NestedCalls);
        Assert.Equal("exit 0", tools[0].CodeOutcome);
        Assert.All(tools, tool => Assert.Equal(1, tool.Turn));
    }

    [Fact]
    public async Task The_runs_card_shows_its_source_revealed_its_usage_and_its_calls_nested()
    {
        var (session, service, _) = await ModelSwitchViewTests.Open();
        foreach (var update in Turn())
            service.Push(update);

        var cards = session.Blocks.OfType<ToolCardBlock>().Where(card => card.CallId is "c1" or "c1.1" or "c1.2" or "c2").ToList();
        var run = cards.Single(card => card.CallId == "c1");
        Assert.True(run.IsCodeRun);
        Assert.False(run.IsNested);
        Assert.Equal("Python · 2 lines", run.Target);
        Assert.Equal("Python · 2 lines · 2 of 50 tool calls · exit 0", run.CodeHeader);
        // What ran, every character: the override is drawn as its code, never applied or dropped.
        Assert.Equal("import corvid_tools as t\nprint(t.list_notes())  # ⟨U+202E⟩", run.CodeSource);
        Assert.DoesNotContain("\u202e", run.CodeSource, StringComparison.Ordinal);
        var nested = cards.Where(card => card.IsNested).ToList();
        Assert.Equal(["c1.1", "c1.2"], nested.Select(card => card.CallId));
        Assert.All(nested, card => Assert.Equal("c1", card.ParentCallId));
        Assert.False(cards.Single(card => card.CallId == "c2").IsNested);
        // The blocks read in the transcript's order: the run, its calls, then the read.
        Assert.Equal(["c1", "c1.1", "c1.2", "c2"], cards.Select(card => card.CallId));
    }

    [Fact]
    public async Task The_runs_approval_card_shows_the_whole_source_and_says_each_call_is_asked()
    {
        var (session, service, time) = await ModelSwitchViewTests.Open();
        service.Push(new ToolCall("c9", "execute_code", ToolKind.Execute, ToolCallStatus.Pending) { At = time.GetUtcNow(), Code = Script });
        // The pipeline states the action as the script itself, and parses it as a command too.
        service.Push(new ApprovalRequested("a-9", "execute_code", Script.Source, time.GetUtcNow().AddMinutes(5))
        {
            At = time.GetUtcNow(),
            CallId = "c9",
            Code = Script,
            Commands = [new ParsedCommand("import corvid_tools", "import")],
        });

        var card = session.Blocks.OfType<ApprovalCardBlock>().Last();
        // A7V1-6: the source is shown once, in its own block; the command line names the run, and nothing is parsed out of it.
        Assert.Equal("execute_code · Python", card.CommandText);
        Assert.Empty(card.Commands);
        Assert.Equal("", card.Unparsed);
        Assert.DoesNotContain("corvid_tools", card.AutomationName, StringComparison.Ordinal);
        Assert.False(card.IsNested);
        Assert.True(card.HasCode);
        Assert.True(card.CodeHasHidden);
        Assert.Equal("Code run: Python · 2 lines; at most 50 tool calls; no network", card.CodeHeader);
        Assert.Equal("import corvid_tools as t\nprint(t.list_notes())  # ⟨U+202E⟩", card.CodeSource);
        Assert.Contains("Each tool it calls is asked for on its own", ApprovalCardBlock.CodeNote, StringComparison.Ordinal);
    }

    // A7V1-6: a call a script made says so on its approval card, and to a screen reader.
    [Fact]
    public async Task A_nested_calls_approval_card_says_a_code_run_made_it()
    {
        var (session, service, time) = await ModelSwitchViewTests.Open();
        service.Push(new ToolCall("c9", "execute_code", ToolKind.Execute, ToolCallStatus.InProgress) { At = time.GetUtcNow(), Code = Script });
        service.Push(new ToolCall("c9.1", "write_file", ToolKind.Edit, ToolCallStatus.Pending) { At = time.GetUtcNow(), ParentCallId = "c9", Target = "out.txt" });
        service.Push(new ApprovalRequested("a-10", "write_file", "write_file out.txt", time.GetUtcNow().AddMinutes(5)) { At = time.GetUtcNow(), CallId = "c9.1" });

        var card = session.Blocks.OfType<ApprovalCardBlock>().Last();

        Assert.True(card.IsNested);
        Assert.Equal("Approval · write_file " + ApprovalCardBlock.NestedNote, card.Title);
        Assert.Contains("code run", card.AutomationName, StringComparison.Ordinal);
        Assert.Equal("write_file out.txt", card.CommandText);
    }
}
