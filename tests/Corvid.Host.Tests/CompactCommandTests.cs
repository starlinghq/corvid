using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Context;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary><c>/compact</c> (C-035) in the host, with the fake generator standing in as summarizer (C-016).</summary>
public sealed class CompactCommandTests
{
    private const string Record = """{"goal":"ship the report","decisions":["drafted section one"],"next_step":"draft section two"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<CommandOutput> Run(TestHost host, CorvidRuntime runtime, CorvidSession session, string line)
    {
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        return commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct);
    }

    private static FakeModels Summarizing() => new(new FakeGenerator([.. Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(
        messages => Reply.Text(messages[0].Text == ContextCompactor.Instructions ? Record : "Done."), 4)]));

    [Fact]
    public async Task Compact_summarizes_the_earlier_turns_and_undo_still_finds_the_latest()
    {
        await using var host = TestHost.Create(Summarizing());
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        Assert.Equal("Nothing before the latest turn needs compacting.", (await Run(host, runtime, session, "/compact")).Lines[0]);

        runtime.Log.Append(session.Id, 0, [
            EventData.Create(new UserMessage("draft section one")), EventData.Create(new AssistantMessage("Section one is drafted.")),
            EventData.Create(new UserMessage("now section two")), EventData.Create(new AssistantMessage("Working on it.")),
        ], Ct);

        var output = await Run(host, runtime, session, "/compact");
        Assert.Equal(OutputTone.Success, output.Tone);
        Assert.Equal(["compacted events 1–2 by fake-gen@cpu in 1 chunk(s).", "goal: ship the report", "next step: draft section two"], output.Lines);
        var transcript = session.Snapshot().Transcript;
        var compacted = Assert.IsType<Compacted>(transcript[0].Content);
        Assert.Equal((1L, 2L), (compacted.FromSequence, compacted.ThroughSequence));
        Assert.IsType<UserMessage>(transcript[1].Content);

        // Compacting again finds a lone record, which is as short as it gets.
        Assert.Equal(OutputTone.Normal, (await Run(host, runtime, session, "/compact")).Tone);
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, "/compact everything")).Tone);

        var undone = await Run(host, runtime, session, "/undo");
        Assert.NotEqual("Nothing to undo: the conversation has no turn left.", undone.Lines[0]);
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is UserMessage { Text: "now section two" });
        // Undo goes back to just before the latest turn's message, and the compaction was written after it, so the
        // earlier turn is raw again: nothing was lost, and /compact can run again.
        Assert.Equal([1L, 2L], session.Snapshot().Transcript.Select(entry => entry.FromSequence));
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is Compacted);
    }

    [Fact]
    public async Task An_unfit_summary_is_a_warning_and_leaves_the_history_raw()
    {
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Say("not a record"))));
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        runtime.Log.Append(session.Id, 0, [
            EventData.Create(new UserMessage("draft section one")), EventData.Create(new AssistantMessage("Section one is drafted.")),
            EventData.Create(new UserMessage("now section two")), EventData.Create(new AssistantMessage("Working on it.")),
        ], Ct);

        var output = await Run(host, runtime, session, "/compact");

        Assert.Equal(OutputTone.Warning, output.Tone);
        var line = Assert.Single(output.Lines);
        Assert.StartsWith("kept events 1–2 uncompacted: fake-gen@cpu: ", line, StringComparison.Ordinal);
        Assert.EndsWith(".", line, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is Compacted);
    }
}
