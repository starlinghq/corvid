using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// A7 where the groups meet: a script's generated client (codeclients) calling the real <c>execute_code</c> server
/// (codehost) through the turn's own path; an architect/editor turn (architect) whose editor runs a script; code mode
/// withheld from an in-process editor; and the sleep-time memory pass (consolidate) while a code run waits on its
/// script's approval.
/// </summary>
public sealed class A7MeetingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recording : IApprovalPrompter
    {
        public List<ApprovalPrompt> Prompts { get; } = [];

        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            lock (Prompts)
                Prompts.Add(prompt);
            return ValueTask.FromResult(ApprovalAnswer.Approve());
        }
    }

    /// <summary>Approves the run at once; holds its script's call until <see cref="Release"/>.</summary>
    private sealed class Holding : IApprovalPrompter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ApprovalPrompt> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            if (prompt.Analysis.Kind == "code")
                return ApprovalAnswer.Approve();
            Held.TrySetResult(prompt);
            await _release.Task.WaitAsync(cancellationToken);
            return ApprovalAnswer.Approve();
        }
    }

    private sealed class Tools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    // fake-gen as a local-server model, so code mode is offered; an embedder that ranks execute_code first for a program.
    private static FakeModels Models(FakeGenerator generator)
    {
        var models = new FakeModels(generator, generatorPrivacy: PrivacyClass.LocalServer);
        models.Provider.Add("code-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("program", "script"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    private static Dictionary<string, string?> CodeSettings(string? editor = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:Tools:Code:Enabled"] = "true",
            ["Corvid:Tools:Code:Tools:0"] = "add_note",
            ["Corvid:Tools:Code:Tools:1"] = "list_notes",
            ["Corvid:Models:Slots:embedder.text:Model"] = "code-embed",
        };
        if (editor is not null)
            settings["Corvid:Turn:EditorModel"] = editor;
        return settings;
    }

    private static void RequireSandbox(CodeLanguage language)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !new[] { "/usr/bin/bwrap", "/bin/bwrap", "/usr/local/bin/bwrap" }.Any(File.Exists),
            "bubblewrap is not installed, so scripts cannot run.");
        var runtimes = CodeRuntimes.Discover();
        Assert.SkipWhen(language switch
        {
            CodeLanguage.Python => runtimes.Python,
            CodeLanguage.JavaScript => runtimes.Node,
            _ => runtimes.Dotnet,
        } is null, $"no {language} runtime was found, so the script cannot run.");
    }

    public static TheoryData<string, string> Scripts => new()
    {
        // Each calls one tool by position and one with nothing, through the client generated for this turn's tools.
        { "python", "corvid.add_note('from python')\nprint(corvid.list_notes())" },
        { "javascript", "await corvid.add_note('from javascript');\nconsole.log(await corvid.list_notes());" },
        { "csharp", "await CorvidTools.AddNoteAsync(\"from csharp\");\nSystem.Console.WriteLine((await CorvidTools.ListNotesAsync()).GetString());" },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task A_generated_client_calls_the_real_execute_code_server_through_the_turns_path(string language, string code)
    {
        RequireSandbox(language switch { "python" => CodeLanguage.Python, "javascript" => CodeLanguage.JavaScript, _ => CodeLanguage.CSharp });
        var generator = new FakeGenerator(
            Reply.Call(CodeTool.ToolName, JsonSerializer.Serialize(new { language, code })),
            Reply.Say("The program ran."));
        await using var host = TestHost.Create(Models(generator), CodeSettings());
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var prompter = new Recording();

        using (runtime.AttachPrompter(session.Id, prompter))
            await session.SendAsync("write a program that adds a note", Ct);

        var events = session.Snapshot().Transcript.Select(entry => entry.Content).ToList();
        var run = events.OfType<ToolCallIssued>().Single(call => call.ToolName == CodeTool.ToolName);
        var result = events.OfType<ToolResult>().Single(item => item.CallId == run.CallId);
        Assert.False(result.IsError, result.Result.GetRawText());
        Assert.True(result.Result.GetProperty("exit_code").GetInt32() == 0, result.Result.GetProperty("stderr").GetString());
        Assert.Contains($"from {language}", result.Result.GetProperty("stdout").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, result.Result.GetProperty("tool_calls").GetInt32());
        // The client's calls are the turn's own: logged under the run, the side effect asked on its own after the script.
        var nested = events.OfType<ToolCallIssued>().Where(call => call.ParentCallId == run.CallId).ToList();
        Assert.Equal(["add_note", "list_notes"], nested.Select(call => call.ToolName));
        Assert.Equal(["code", "tool"], prompter.Prompts.Select(prompt => prompt.Analysis.Kind));
        Assert.Equal($"{run.CallId}.1", prompter.Prompts[1].CallId);
        Assert.Contains($"from {language}", host.Models.Tools.Notes);
    }

    [Fact]
    public async Task An_architect_turns_editor_runs_a_script_after_the_plan_and_its_calls_take_the_turns_path()
    {
        RequireSandbox(CodeLanguage.Python);
        const string code = "corvid.add_note(text='from the editor')\nprint(corvid.list_notes())";
        var generator = new FakeGenerator(
            Reply.Call(CodeTool.ToolName, JsonSerializer.Serialize(new { language = "python", code })),
            Reply.Say("Applied the plan."));
        await using var host = TestHost.Create(Models(generator), CodeSettings());
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        await commands.ExecuteAsync("/architect", new SlashCommandContext(runtime, session, commands), Ct);
        var prompter = new Recording();

        Delivery delivery;
        using (runtime.AttachPrompter(session.Id, prompter))
            delivery = await session.SendAsync("write a program that adds a note", Ct);

        // The planner (fake-gen-large, offered no tools) planned; the editor (the session's own fake-gen) ran the script.
        var plan = Assert.Single(delivery.Result!.Plans);
        Assert.StartsWith("fake-gen-large", plan.Planner, StringComparison.Ordinal);
        Assert.StartsWith("fake-gen@", plan.Editor, StringComparison.Ordinal);
        Assert.NotEmpty(host.Models.Larger.Calls);
        Assert.Contains(CodeTool.ToolName, generator.OfferedTools[0]);
        var events = session.Snapshot().Transcript.Select(entry => entry.Content).ToList();
        var planned = events.FindIndex(item => item is AssistantMessage { Plan: not null });
        var run = events.OfType<ToolCallIssued>().Single(call => call.ToolName == CodeTool.ToolName);
        Assert.True(planned >= 0 && planned < events.IndexOf(run));
        Assert.StartsWith("fake-gen@", run.Generator, StringComparison.Ordinal);
        // The editor's script is approved as code and its side effect asked under the run, as in any turn.
        var result = events.OfType<ToolResult>().Single(item => item.CallId == run.CallId);
        Assert.False(result.IsError, result.Result.GetRawText());
        Assert.Equal(["code", "tool"], prompter.Prompts.Select(prompt => prompt.Analysis.Kind));
        Assert.Equal($"{run.CallId}.1", prompter.Prompts[1].CallId);
        Assert.Contains("from the editor", host.Models.Tools.Notes);
        Assert.Equal("Applied the plan.", delivery.Result.Text);
    }

    [Fact]
    public async Task Code_mode_is_withheld_when_the_editor_model_runs_in_process()
    {
        // The session's generator is a local server, so code mode would be offered; its configured editor is in process.
        var withheld = new FakeGenerator(Reply.Say("ok"));
        await using (var host = TestHost.Create(Models(withheld), CodeSettings(editor: "fake-gen-large")))
        await using (var runtime = await host.StartAsync())
            await runtime.OpenSession(surface: "test").SendAsync("write a program that adds a note", Ct);
        Assert.DoesNotContain(CodeTool.ToolName, withheld.OfferedTools[0]);

        // The same turn with the editor the session's own model (a local server): offered.
        var offered = new FakeGenerator(Reply.Say("ok"));
        await using (var host = TestHost.Create(Models(offered), CodeSettings(editor: "fake-gen")))
        await using (var runtime = await host.StartAsync())
            await runtime.OpenSession(surface: "test").SendAsync("write a program that adds a note", Ct);
        Assert.Contains(CodeTool.ToolName, offered.OfferedTools[0]);
    }

    private const string Full =
        "Prefers metric units and short answers.\nPrefers metric units, always.\nWorks on corvid in the evening; CI first.\n"
        + "Likes answers short, no preamble at all.\nShort answers please, thanks.";

    private const string Rewrite = "Prefers metric units and short answers, no preamble.\nWorks on corvid in the evening; CI first.";

    [Fact]
    public async Task The_sleep_time_pass_waits_while_a_code_run_waits_on_its_scripts_approval_then_runs()
    {
        var rewrites = new List<IReadOnlyList<ChatMessage>>();
        // The consolidator's requests get the rewrite; the turn calls the run once, then answers after its result.
        var generator = new FakeGenerator([.. Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(messages =>
        {
            if (messages[0].Text == BlockConsolidator.SystemPrompt)
            {
                lock (rewrites)
                    rewrites.Add(messages);
                return Reply.Text(Rewrite);
            }
            return messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any())
                ? Reply.Text("The script added the note.")
                : Reply.Call("execute_code", """{"language":"python","code":"corvid.add_note('while consolidating')"}""")(messages);
        }, 16)]);
        var code = new FakeCodeTool(("add_note", """{"text":"while consolidating"}"""));
        var host = TestHost.Create(new FakeModels(generator, generatorPrivacy: PrivacyClass.LocalServer), new Dictionary<string, string?>
        {
            ["Corvid:Memory:Consolidation:IdleSeconds"] = "0",
            ["Corvid:Memory:Consolidation:IntervalMinutes"] = "0",
        }, services => services.AddSingleton<IToolProvider>(new Tools(code)));
        var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        await using (host)
        await using (runtime)
        {
            var job = runtime.Memory!.Consolidator!;
            var processed = new TaskCompletionSource<BlockRewrite>(TaskCreationOptions.RunContinuationsAsynchronously);
            job.Processed += rewrite => processed.TrySetResult(rewrite);
            var prompter = new Holding();
            using var attached = runtime.AttachPrompter(session.Id, prompter);

            var turn = session.SendAsync("run the weather script over my notes", Ct);
            var held = await prompter.Held.Task.WaitAsync(Ct);
            Assert.EndsWith(".1", held.CallId, StringComparison.Ordinal);

            // While the run waits on its script's call the session is busy: a full block and a nudge stage nothing.
            Assert.False(runtime.SessionsIdle());
            runtime.Memory.Blocks.SetBudget("agent", 50);
            Assert.Equal(BlockWriteStatus.Applied, runtime.Memory.Blocks.Set("agent", Full).Status);
            job.Nudge();
            Assert.False(runtime.SessionsIdle());
            Assert.Empty(runtime.Memory.Writer!.ListPending());
            lock (rewrites)
                Assert.Empty(rewrites);

            // Released, the run finishes through the turn's path; the turn's end nudges the pass, which stages the rewrite.
            prompter.Release();
            var delivery = await turn;
            Assert.Equal("The script added the note.", delivery.Result!.Text);
            Assert.False(Assert.Single(code.Results).IsError);
            Assert.Contains("while consolidating", host.Models.Tools.Notes);
            var rewrite = await processed.Task.WaitAsync(Ct);
            Assert.Equal(BlockRewriteStatus.Staged, rewrite.Status);
            var pending = Assert.Single(runtime.Memory.Writer.ListPending());
            Assert.Equal(MemoryJobs.Session, pending.SessionId);
            // The pass never touched the conversation's log, and the block is unchanged until a person approves.
            Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is MemoryJobRan);
            Assert.Equal(Full, runtime.Memory.Blocks.Get("agent").Content);
        }
    }
}
