using System.Text.Json.Nodes;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>A5, the switch surfaces: <c>/model</c>'s "this session" and "default for new sessions" choices, reset, and a switch mid-turn.</summary>
public sealed class ModelSwitchCommandTests
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
    public async Task DefaultForNewSessionsWritesTheFileAndLeavesThisSessionAlone()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.NotNull(runtime.ConfigWriter);
            var output = await Run(runtime, session, commands, "/model summarizer fake-gen --default");
            Assert.Equal(OutputTone.Success, output.Tone);
            Assert.Contains("default for new sessions", output.Lines[0], StringComparison.Ordinal);
            var written = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(host.Root, "corvid.json"), TestContext.Current.CancellationToken))!;
            Assert.Equal("fake-gen", (string?)written["Corvid"]?["Models"]?["Slots"]?["summarizer"]?["Model"]
                ?? (string?)written["corvid"]?["Models"]?["Slots"]?["summarizer"]?["Model"]);

            // The decider (the degraded tier here): new sessions start on it now; this one made no choice of its own.
            output = await Run(runtime, session, commands, "/model decider fake-kev --default");
            Assert.Equal(OutputTone.Success, output.Tone);
            Assert.Contains("New sessions start on it now", output.Lines[0], StringComparison.Ordinal);
            Assert.False(session.Slots.HasSessionChoice(ModelSlots.Decider));
            var next = runtime.OpenSession();
            Assert.Equal("fake-kev", next.Slots.Current(ModelSlots.Decider).Profile.Id.Split('@')[0]);

            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/model 2 --pin --default")).Tone);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/model extractor fake-gen --default")).Tone);
        }
    }

    [Fact]
    public async Task ResetForgetsThisSessionsChoice()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.Contains("made no choice", (await Run(runtime, session, commands, "/model reset generator")).Lines[0], StringComparison.Ordinal);
            Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, "/model 2")).Tone);
            Assert.True(session.Slots.HasSessionChoice(ModelSlots.Generator));
            var reset = await Run(runtime, session, commands, "/model reset generator");
            Assert.Equal(OutputTone.Success, reset.Tone);
            Assert.False(session.Slots.HasSessionChoice(ModelSlots.Generator));
            Assert.Equal("fake-gen@cpu", session.Slots.Current(ModelSlots.Generator).Profile.Id);
        }
    }

    [Fact]
    public async Task ASwitchWhileATurnRunsSaysTheTurnTakesItAtItsNextStep()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("note_list"), Reply.Say("Done.")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        models.Generator.Hold = (step, _) =>
        {
            if (step != 0)
                return null;
            started.TrySetResult();
            return release.Task;
        };
        var (host, runtime, session, commands) = await StartAsync(models);
        await using (host)
        await using (runtime)
        {
            var turn = session.SendAsync("list my notes", TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(runtime.IsTurnRunning(session.Id));
            var output = await Run(runtime, session, commands, "/model 2");
            Assert.Equal(OutputTone.Success, output.Tone);
            Assert.Contains("takes it at its next step", output.Lines[0], StringComparison.Ordinal);
            release.SetResult();
            var delivery = await turn;
            // The second step ran on the session's new generator.
            Assert.Equal("Hello from the larger model.", delivery.Result!.Text);
            Assert.Equal("fake-gen-large@cpu", session.Slots.Current(ModelSlots.Generator).Profile.Id);
        }
    }

    [Fact]
    public async Task StatusAndTheDeciderListSayThisSessionsDecider()
    {
        var (host, runtime, session, commands) = await StartAsync();
        await using (host)
        await using (runtime)
        {
            Assert.Contains((await Run(runtime, session, commands, "/status")).Lines, line => line == $"decider: {runtime.DeciderState}");
            Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, "/model decider fake-kev")).Tone);
            var status = await Run(runtime, session, commands, "/status");
            Assert.Contains(status.Lines, line => line.StartsWith("decider: fake-kev", StringComparison.Ordinal) && line.Contains("for this session", StringComparison.Ordinal));
            Assert.Contains((await Run(runtime, session, commands, "/model decider")).Lines, line => line.Contains("for this session", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheSharedDefaultWriteRefusesWithoutWriting()
    {
        await using var host = TestHost.Create();
        var writer = host.Services.GetRequiredService<CorvidConfigWriter>();
        var catalogue = host.Services.GetRequiredService<Corvid.Host.Models.ModelCatalogue>();
        Assert.False(ModelDefaults.Write(writer, catalogue, "nonslot", "fake-gen").Written);
        Assert.False(ModelDefaults.Write(writer, catalogue, ModelSlots.Generator, "qwen-9000").Written);
        var refused = ModelDefaults.Write(writer, catalogue, ModelSlots.Extractor, "fake-gen");
        Assert.False(refused.Written);
        Assert.Contains("cannot serve", refused.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Root, "corvid.json")));
        var written = ModelDefaults.Write(writer, catalogue, ModelSlots.Summarizer, "fake-gen-large");
        Assert.False(written.Written, "fake-gen-large serves the generator only");
        Assert.True(ModelDefaults.Write(writer, catalogue, ModelSlots.Summarizer, "fake-gen").Written);
    }
}
