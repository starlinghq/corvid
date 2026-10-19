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
        string[] core = ["model", "status", "goal", "approvals", "memory", "help", "exit"];
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
            var list = await Run(runtime, session, commands, "/model");
            Assert.Contains(list.Table!.Rows, row => row[0] == ModelSlots.Generator && row[1] == "fake-gen@cpu" && row[3] == "fake-gen-large@cpu");
            Assert.Contains(list.Table.Rows, row => row[0] == ModelSlots.Decider && row[1] == "degraded tier");

            var switched = await Run(runtime, session, commands, "/model fake-gen-large --pin");
            Assert.Equal(OutputTone.Success, switched.Tone);
            Assert.Equal("fake-gen-large@cpu", session.Slots.Current(ModelSlots.Generator).Profile.Id);
            Assert.True(session.Slots.IsPinned(ModelSlots.Generator));
            Assert.Contains(runtime.Log.Read(session.Id, cancellationToken: TestContext.Current.CancellationToken), item => item.Payload is ModelSwitched { Slot: ModelSlots.Generator, Reason: "user" });

            var delivery = await session.SendAsync("hi", TestContext.Current.CancellationToken);
            Assert.Equal("Hello from the larger model.", delivery.Result!.Text);

            Assert.Contains("already uses", (await Run(runtime, session, commands, "/model generator fake-gen-large")).Lines[0], StringComparison.Ordinal);
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
