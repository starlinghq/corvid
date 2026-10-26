using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

public sealed class CommandRegistryTests
{
    private static SlashCommand Noop(string name) => new(name, "test", "", (_, _, _) => ValueTask.FromResult(CommandOutput.Text("ok")));

    [Theory]
    [InlineData("/help", true)]
    [InlineData("  /model generator fake-gen", true)]
    [InlineData("/goal ship it/today", true)]
    [InlineData("/memory-pending", true)]
    [InlineData("/tmp/notes.txt", false)]
    [InlineData("/", false)]
    [InlineData("/1st", false)]
    [InlineData("what is /help?", false)]
    [InlineData("hello", false)]
    public void RecognisesCommandLines(string line, bool command) => Assert.Equal(command, SlashCommandRegistry.IsCommand(line));

    [Fact]
    public void ParsesNamesArgumentsQuotesAndRawText()
    {
        var invocation = SlashCommandRegistry.Parse("/MODEL generator \"fake gen\"  --pin");
        Assert.Equal("model", invocation.Name);
        Assert.Equal(["generator", "fake gen", "--pin"], invocation.Arguments);
        Assert.Equal("generator \"fake gen\"  --pin", invocation.Text);
        Assert.Empty(SlashCommandRegistry.Parse("/help").Arguments);
        Assert.Equal([""], SlashCommandRegistry.Parse("/goal \"\"").Arguments);
    }

    [Fact]
    public void NamesAndAliasesAreUniqueAndWellFormed()
    {
        var registry = new SlashCommandRegistry().Add(Noop("one") with { Aliases = ["uno"] });
        Assert.Throws<ArgumentException>(() => registry.Add(Noop("uno")));
        Assert.Throws<ArgumentException>(() => registry.Add(Noop("One")));
        Assert.Throws<ArgumentException>(() => registry.Add(Noop("two words")));
        Assert.True(registry.TryGet("/UNO", out var found));
        Assert.Equal("one", found.Name);
    }

    [Fact]
    public void TheCoreSetIsRegistered()
    {
        var registry = CoreCommands.Register(new SlashCommandRegistry());
        string[] core = ["model", "status", "goal", "approvals", "memory", "skills", "help", "exit"];
        Assert.All(core, name => Assert.True(registry.TryGet(name, out _), name));
        Assert.True(registry.TryGet("quit", out var quit) && quit.Name == "exit");
    }

    [Fact]
    public void PlainTextAlignsTables()
    {
        var output = new CommandOutput
        {
            Title = "Slots",
            Lines = ["two slots"],
            Table = CommandTable.Of(["slot", "model"], [["generator", "a"], ["embedder.text", "bb"]]),
        };
        Assert.Equal("""
            Slots
            two slots
            slot           model
            generator      a
            embedder.text  bb
            """.ReplaceLineEndings(), output.ToPlainText().ReplaceLineEndings());
    }
}

public sealed class CoreCommandTests
{
    private static async Task<(TestHost Host, CorvidRuntime Runtime, CorvidSession Session, SlashCommandRegistry Commands)> StartAsync(FakeModels? models = null)
    {
        var host = TestHost.Create(models);
        var runtime = await host.StartAsync();
        return (host, runtime, runtime.OpenSession(), host.Services.GetRequiredService<SlashCommandRegistry>());
    }

    private static ValueTask<CommandOutput> Run(CorvidRuntime runtime, CorvidSession session, SlashCommandRegistry commands, string line) =>
        commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), TestContext.Current.CancellationToken);

    [Fact]
    public async Task HelpListsEveryCommand()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            var output = await Run(runtime, session, commands, "/help");
            Assert.Equal(commands.Commands.Count, output.Table!.Rows.Length);
            Assert.Contains(output.Table.Rows, row => row[0] == "/exit (/quit)");
        }
    }

    [Fact]
    public async Task UnknownCommandsSuggestNearOnes()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            var output = await Run(runtime, session, commands, "/mod");
            Assert.Equal(OutputTone.Error, output.Tone);
            Assert.Contains("Did you mean /model?", output.Lines[0], StringComparison.Ordinal);
            Assert.Contains("Did you mean /quit?", (await Run(runtime, session, commands, "/qu")).Lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExitEndsTheSession()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
            Assert.True((await Run(runtime, session, commands, "/quit")).Exit);
    }

    [Fact]
    public async Task GoalIsSetShownAndClearedThroughTheLog()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.Contains("No goal", (await Run(runtime, session, commands, "/goal")).Lines[0], StringComparison.Ordinal);
            await Run(runtime, session, commands, "/goal finish the C-013 docs");
            Assert.Equal("finish the C-013 docs", session.Snapshot().Goal);
            Assert.Contains("finish the C-013 docs", (await Run(runtime, session, commands, "/goal")).Lines[0], StringComparison.Ordinal);
            await Run(runtime, session, commands, "/goal clear");
            Assert.Null(session.Snapshot().Goal);
            Assert.Equal(2, runtime.Log.Read(session.Id, cancellationToken: TestContext.Current.CancellationToken).Count(item => item.Payload is GoalSet));
        }
    }

    [Fact]
    public async Task ModelListsSlotsAndSwitchesThroughTheActor()
    {
        var models = new FakeModels();
        var (host, runtime, session, commands) = await StartAsync(models);
        await using (host)
        await using (runtime)
        {
            // The generator's models, numbered by name, with the current one marked; only the others are offered.
            var list = await Run(runtime, session, commands, "/model");
            Assert.Equal("generator: fake-gen@cpu", list.Title);
            Assert.Equal(["#", "model", "runs", "status", "role"], list.Table!.Columns);
            Assert.Equal(["1", "fake-gen", "local", "● current", "default"], list.Table.Rows[0]);
            Assert.Equal(["2", "fake-gen-large", "local", "ready", "escalation"], list.Table.Rows[1]);
            Assert.Equal([new CommandChoice("2", "fake-gen-large", "/model 2")], list.Choices);
            Assert.Contains(list.Lines, line => line.Contains("decider degraded tier", StringComparison.Ordinal));

            // A5: the decider is offered too, per session.
            var decider = await Run(runtime, session, commands, "/model decider");
            Assert.Equal("decider: degraded tier", decider.Title);
            Assert.Equal(["fake-kev", "fake-kev-large"], decider.Choices.Select(choice => choice.Label));
            var embedder = await Run(runtime, session, commands, "/models embedder.text");
            Assert.Equal([new CommandChoice("2", "fake-embed-alt", "/model embedder.text 2")], embedder.Choices);

            var switched = await Run(runtime, session, commands, "/model 2 --pin");
            Assert.Equal(OutputTone.Success, switched.Tone);
            Assert.Equal("fake-gen-large@cpu", session.Slots.Current(ModelSlots.Generator).Profile.Id);
            Assert.True(session.Slots.IsPinned(ModelSlots.Generator));
            Assert.Equal("● current (pinned)", (await Run(runtime, session, commands, "/model")).Table!.Rows[1][3]);
            // A5: the person's /model is this session's choice, logged as such (it was "user", reverted at the turn's end).
            Assert.Contains(runtime.Log.Read(session.Id, cancellationToken: TestContext.Current.CancellationToken), item => item.Payload is ModelSwitched { Slot: ModelSlots.Generator, Reason: "session" });

            var delivery = await session.SendAsync("hi", TestContext.Current.CancellationToken);
            Assert.Equal("Hello from the larger model.", delivery.Result!.Text);

            Assert.Contains("already uses", (await Run(runtime, session, commands, "/model generator fake-gen-large")).Lines[0], StringComparison.Ordinal);
            Assert.Contains("already uses", (await Run(runtime, session, commands, "/model generator 2")).Lines[0], StringComparison.Ordinal);
            Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, "/model unpin generator")).Tone);
            Assert.False(session.Slots.IsPinned(ModelSlots.Generator));
        }
    }

    [Fact]
    public async Task ModelRefusesWhatCannotWork()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.Contains("No model is named", (await Run(runtime, session, commands, "/model qwen-9000")).Lines[0], StringComparison.Ordinal);
            Assert.Contains("cannot serve extractor", (await Run(runtime, session, commands, "/model extractor fake-gen")).Lines[0], StringComparison.Ordinal);
            Assert.Contains("Unknown model slot", (await Run(runtime, session, commands, "/model nonslot fake-gen")).Lines[0], StringComparison.Ordinal);
            Assert.Contains("Usage", (await Run(runtime, session, commands, "/model a b c")).Lines[0], StringComparison.Ordinal);
            Assert.Contains("numbered 1 to 2", (await Run(runtime, session, commands, "/model 9")).Lines[0], StringComparison.Ordinal);
            Assert.Contains("numbered 1 to 1", (await Run(runtime, session, commands, "/model extractor 0")).Lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnEmbedderMemoryCannotUseIsRefused()
    {
        var models = new FakeModels();
        models.Provider.Add("fake-embed-unconfigured", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("x"), 512, Corvid.Models.ToolCallSupport.None);
        var (host, runtime, session, commands) = await StartAsync(models);
        await using (host)
        await using (runtime)
        {
            var output = await Run(runtime, session, commands, "/model embedder.text fake-embed-unconfigured");
            Assert.Equal(OutputTone.Error, output.Tone);
            Assert.Contains("Fallbacks", output.Lines[0], StringComparison.Ordinal);
            // A configured alternative switches, and memory's re-embed job starts for it.
            Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, "/model embedder.text fake-embed-alt")).Tone);
        }
    }

    [Fact]
    public async Task AModelMissingFromTheCacheSaysHowToFetchIt()
    {
        var models = new FakeModels();
        models.Provider.Add("fake-gen-missing", [ModelSlots.Generator], () => new FakeGenerator(), ready: false);
        var (host, runtime, session, commands) = await StartAsync(models);
        await using (host)
        await using (runtime)
        {
            var output = await Run(runtime, session, commands, "/model fake-gen-missing");
            Assert.Equal(OutputTone.Error, output.Tone);
            Assert.Contains("corvid model download fake-gen-missing", output.Lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StatusShowsSlotsDevicesQueuesAndLatency()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            await session.SendAsync("hi", TestContext.Current.CancellationToken);
            // Memory embeds the turn's episodes on the device in the background; until then the queue is legitimately not empty.
            await runtime.MaintenanceIdleAsync();
            var output = await Run(runtime, session, commands, "/status");
            Assert.Equal(["slot", "model", "device", "queue", "p50", "calls"], output.Table!.Columns);
            var generator = Assert.Single(output.Table.Rows, row => row[0] == ModelSlots.Generator);
            Assert.Equal(["generator", "fake-gen@cpu", "cpu", "0"], generator.Take(4));
            Assert.NotEqual("-", generator[4]);
            Assert.Equal("1", generator[5]);
            Assert.Contains(output.Table.Rows, row => row[0] == ModelSlots.Decider && row[1] == "degraded" && row[5] != "0");
            Assert.Contains(output.Lines, line => line.StartsWith($"session {session.Id}:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ApprovalsListsRememberedRules()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added.")));
        var (host, runtime, session, commands) = await StartAsync(models);
        await using (host)
        await using (runtime)
        {
            Assert.Contains("No remembered rules", string.Join("\n", (await Run(runtime, session, commands, "/approvals")).Lines), StringComparison.Ordinal);
            using (runtime.AttachPrompter(session.Id, new CompositionTests.RecordingPrompter(
                       Corvid.Approvals.ApprovalAnswer.Approve(Corvid.Approvals.ApprovalScope.Session))))
                await session.SendAsync("add a note: water plants", TestContext.Current.CancellationToken);
            var output = await Run(runtime, session, commands, "/approvals");
            var rule = Assert.Single(output.Table!.Rows);
            Assert.Equal(["session", "allow", "add_note"], rule.Take(3));
        }
    }

    [Fact]
    public async Task MemoryReviewsStagedWrites()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.Contains("Nothing is waiting", (await Run(runtime, session, commands, "/memory")).Lines[0], StringComparison.Ordinal);
            // Without Kev the memory_op gate never passes, so every write is staged for review.
            var staged = await runtime.Memory!.Writer!.WriteAsync(new Corvid.Memory.MemoryWriteRequest(session.Id, "The boat is blue."),
                TestContext.Current.CancellationToken);
            Assert.Equal(Corvid.Memory.MemoryWriteStatus.Staged, staged.Status);

            var pending = await Run(runtime, session, commands, "/memory pending");
            var row = Assert.Single(pending.Table!.Rows);
            Assert.Contains("The boat is blue.", row[2], StringComparison.Ordinal);

            var rejected = await Run(runtime, session, commands, $"/memory reject {row[0]}");
            Assert.Equal(OutputTone.Success, rejected.Tone);
            Assert.Empty(runtime.Memory.Writer.ListPending());
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/memory approve x bogus")).Tone);
        }
    }

    [Fact]
    public async Task MemoryLessonsReviewsThisProjectsCandidates()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            // The test workspace is no repository, so its path is the project.
            var project = runtime.Project!;
            Assert.Equal(Corvid.Memory.ProjectSources.Path, project.Source);
            Assert.Contains((await Run(runtime, session, commands, "/status")).Lines,
                line => line == $"project: {project.Id[..12]} (path)");
            Assert.Contains("No lesson is waiting", (await Run(runtime, session, commands, "/memory lessons")).Lines[0], StringComparison.Ordinal);

            Corvid.Memory.LessonContent Content(string kind) => new(kind, "Pass a limit to list_notes", "when listing notes",
                "Call list_notes with a limit.\nWithout one it fails (event 3).\nConfirm notes come back.", ["notes", "tools"],
                [new Corvid.Memory.LessonEvidence(session.Id, [3], "list_notes failed without a limit", new string('a', 64))]);
            var lessons = runtime.Memory!.Lessons!;
            var first = await lessons.FileAsync(new Corvid.Memory.LessonDraft(project.Id, Content("gotcha")), session.Id, false, TestContext.Current.CancellationToken);
            var second = await lessons.FileAsync(new Corvid.Memory.LessonDraft(project.Id, Content("convention")), session.Id, false, TestContext.Current.CancellationToken);
            // Without Kev the gate never passes, so both wait for review.
            Assert.Equal(Corvid.Memory.LessonFileStatus.Staged, first.Status);
            Assert.Contains("2 lesson candidate(s): /memory lessons pending", (await Run(runtime, session, commands, "/memory pending")).Lines[0],
                StringComparison.Ordinal);

            var pending = await Run(runtime, session, commands, "/memory lessons pending");
            Assert.Equal(2, pending.Table!.Rows.Length);
            var id = first.Lesson!.LessonId[7..19];
            Assert.Contains(pending.Table.Rows, row => row[0] == id && row[1] == "gotcha");

            Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, $"/memory lessons approve {id}")).Tone);
            Assert.Equal(Corvid.Memory.LessonStates.Approved, lessons.Store.Get(first.Lesson.LessonId)!.State);
            var rejected = await Run(runtime, session, commands, $"/memory lessons reject {second.Lesson!.LessonId} too narrow");
            Assert.Equal(OutputTone.Success, rejected.Tone);
            Assert.Equal("too narrow", lessons.Store.Get(second.Lesson.LessonId)!.ReviewReason);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, $"/memory lessons approve {id}")).Tone);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/memory lessons approve ffffffffffff")).Tone);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/memory lessons bogus")).Tone);
            Assert.Contains("1 approved lesson(s)", (await Run(runtime, session, commands, "/memory lessons")).Lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SkillsListImportShowHistoryAndRevert()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.Contains("0 skill(s)", (await Run(runtime, session, commands, "/skills")).Lines[0], StringComparison.Ordinal);
            var hub = Path.Combine(host.Root, "hub", "weather");
            Directory.CreateDirectory(hub);
            File.WriteAllText(Path.Combine(hub, "SKILL.md"), "---\nname: weather\ndescription: Check the weather.\n---\n1. Ask for the city.\n");

            var imported = await Run(runtime, session, commands, $"/skills import {Path.Combine(host.Root, "hub")} test-hub");
            Assert.Equal(OutputTone.Success, imported.Tone);
            Assert.Contains("hub:test-hub", imported.Lines[0], StringComparison.Ordinal);
            var list = await Run(runtime, session, commands, "/skills list");
            var row = Assert.Single(list.Table!.Rows);
            Assert.Equal(["weather", "hub:test-hub, outside content"], row[..2]);
            Assert.Contains("1. Ask for the city.", (await Run(runtime, session, commands, "/skills show weather")).Lines, StringComparer.Ordinal);
            Assert.Contains(runtime.Log.Read(session.Id, cancellationToken: TestContext.Current.CancellationToken),
                item => item.Payload is SkillChanged { Author: "hub:test-hub" });
            Assert.Empty(session.Snapshot().Issues);

            var log = await Run(runtime, session, commands, "/skills log weather");
            var commit = Assert.Single(log.Table!.Rows);
            Assert.Equal("hub:test-hub", commit[1]);
            Assert.Contains("+1. Ask for the city.", (await Run(runtime, session, commands, $"/skills diff {commit[0]}")).Lines[0], StringComparison.Ordinal);
            Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, $"/skills revert {commit[0]}")).Tone);
            Assert.Contains("0 skill(s)", (await Run(runtime, session, commands, "/skills")).Lines[0], StringComparison.Ordinal);

            Assert.Equal("No drafted skill is waiting for review.", (await Run(runtime, session, commands, "/skills pending")).Lines[0]);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/skills revert --hard")).Tone);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/skills approve nope")).Tone);
            Assert.Contains("skills:", string.Join("\n", (await Run(runtime, session, commands, "/status")).Lines), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ToolsAndTodosReadTheRegistryAndTheLog()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            var tools = await Run(runtime, session, commands, "/tools");
            Assert.Contains(tools.Table!.Rows, row => row[0] == "add_note" && row[1] == "side effects");
            Assert.Contains(tools.Table.Rows, row => row[0] == "list_notes" && row[1] == "read-only");
            Assert.Equal("No todos.", (await Run(runtime, session, commands, "/todos")).Lines[0]);
            var token = TestContext.Current.CancellationToken;
            runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, token),
                [EventData.Create(new TodosSet([new TodoItem("1", "write tests", true)]))], token);
            Assert.Equal("[x] write tests", (await Run(runtime, session, commands, "/todos")).Lines[0]);
        }
    }
}
