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
        Assert.Equal(["compacted events 1–2 by fake-gen@cpu in 1 chunk(s).", "goal: ship the report", "next step: draft section two"], output.Lines[..3]);
        Assert.Matches(Figures, output.Lines[3]);
        var transcript = session.Snapshot().Transcript;
        var compacted = Assert.IsType<Compacted>(transcript[0].Content);
        Assert.Equal((1L, 2L), (compacted.FromSequence, compacted.ThroughSequence));
        Assert.IsType<UserMessage>(transcript[1].Content);

        // Compacting again finds a lone record, which is as short as it gets.
        var again = await Run(host, runtime, session, "/compact");
        Assert.Equal(OutputTone.Normal, again.Tone);
        Assert.Matches(@"^history [\d,]+ tokens \(generator's counter; system prompt, tools and notes not counted\)\.$", again.Lines[1]);
        // C-041: the rest of the line is a focus, not an error; here there is still nothing to compact.
        Assert.Equal("Nothing before the latest turn needs compacting.", (await Run(host, runtime, session, "/compact everything")).Lines[0]);

        var undone = await Run(host, runtime, session, "/undo");
        Assert.NotEqual("Nothing to undo: the conversation has no turn left.", undone.Lines[0]);
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is UserMessage { Text: "now section two" });
        // Undo goes back to just before the latest turn's message, and the compaction was written after it, so the
        // earlier turn is raw again: nothing was lost, and /compact can run again.
        Assert.Equal([1L, 2L], session.Snapshot().Transcript.Select(entry => entry.FromSequence));
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is Compacted);
    }

    // "history 1,234 → 56 tokens (…)." with the before figure larger than the after one.
    private const string Figures = @"^history [\d,]+ → [\d,]+ tokens \(generator's counter; system prompt, tools and notes not counted\)\.$";

    [Fact]
    public async Task A_focus_reaches_the_summarizer_and_the_event_and_the_figures_shrink()
    {
        var seen = new List<string>();
        var models = new FakeModels(new FakeGenerator([.. Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(messages =>
        {
            if (messages[0].Text != ContextCompactor.Instructions)
                return Reply.Text("Done.");
            lock (seen)
                seen.Add(messages[1].Text);
            return Reply.Text(Record);
        }, 4)]));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var answer = string.Join(" ", Enumerable.Repeat("Section one covers the revenue table and the churn chart.", 20));
        runtime.Log.Append(session.Id, 0, [
            EventData.Create(new UserMessage("draft section one")), EventData.Create(new AssistantMessage(answer)),
            EventData.Create(new UserMessage("now section two")), EventData.Create(new AssistantMessage("Working on it.")),
        ], Ct);

        var output = await Run(host, runtime, session, "/compact   the churn numbers  ");

        Assert.Equal(OutputTone.Success, output.Tone);
        Assert.Equal("kept in full: the churn numbers", output.Lines[1]);
        var figures = System.Text.RegularExpressions.Regex.Match(output.Lines[^1], Figures.Replace(@"[\d,]+", @"([\d,]+)", StringComparison.Ordinal));
        Assert.True(figures.Success, output.Lines[^1]);
        int Number(int group) => int.Parse(figures.Groups[group].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Number(1) > Number(2), output.Lines[^1]);
        var content = Assert.Single(seen);
        Assert.Equal("The user asked this summary to keep, in full: the churn numbers", content.Split('\n')[1]);
        Assert.Equal("the churn numbers", Assert.IsType<Compacted>(session.Snapshot().Transcript[0].Content).Focus);
    }

    [Fact]
    public async Task A_focus_over_the_cap_is_a_usage_error_and_nothing_is_compacted()
    {
        await using var host = TestHost.Create(Summarizing());
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        runtime.Log.Append(session.Id, 0, [
            EventData.Create(new UserMessage("draft section one")), EventData.Create(new AssistantMessage("Section one is drafted.")),
            EventData.Create(new UserMessage("now section two")), EventData.Create(new AssistantMessage("Working on it.")),
        ], Ct);

        var output = await Run(host, runtime, session, "/compact " + new string('x', CoreCommands.MaxCompactFocusChars + 1));

        Assert.Equal(OutputTone.Error, output.Tone);
        Assert.StartsWith("Usage: /compact [<what to keep>]", output.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is Compacted);
        Assert.Equal(OutputTone.Success, (await Run(host, runtime, session, "/compact " + new string('x', CoreCommands.MaxCompactFocusChars))).Tone);
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
