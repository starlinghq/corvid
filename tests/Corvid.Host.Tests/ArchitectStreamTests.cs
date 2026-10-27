using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// A7, architect/editor turns on the host: <c>/architect</c> asks for one; the plan is not streamed as the answer; the
/// stream reports it as <see cref="TurnPlanned"/> live and on replay, before the editor's calls; the configuration reaches
/// the turn.
/// </summary>
public sealed class ArchitectStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Plan = "1. List the notes with list_notes.";

    private static FakeModels Models() => new(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("Two notes.")));

    private static ValueTask<CommandOutput> Run(CorvidRuntime runtime, CorvidSession session, string line, TestHost host)
    {
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        return commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct);
    }

    [Fact]
    public async Task Architect_plans_the_next_turn_and_the_stream_shows_the_plan_live_before_the_editors_call_and_on_replay()
    {
        var models = Models();
        await using var host = TestHost.Create(models);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");

        var asked = await Run(runtime, session, "/architect", host);
        Assert.Equal(OutputTone.Success, asked.Tone);
        Assert.Contains("fake-gen-large@cpu plans, fake-gen@cpu edits", asked.ToPlainText(), StringComparison.Ordinal);
        Assert.Equal(ArchitectRequest.NextTurn, session.Runner.Architect);

        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, new ToolPresentation(runtime), 0, update =>
        {
            lock (live)
                live.Add(update);
        });
        Delivery delivery;
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            delivery = await session.SendAsync("list my notes", Ct);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        var result = delivery.Result!;
        Assert.Equal("Two notes.", result.Text);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(TurnRunner.ArchitectTrigger, plan.Trigger);
        Assert.StartsWith("fake-gen-large", plan.Planner, StringComparison.Ordinal);
        Assert.StartsWith("fake-gen", plan.Editor, StringComparison.Ordinal);
        Assert.Equal(ArchitectRequest.None, session.Runner.Architect);

        // The planner's text is the plan, not the answer: nothing of it streamed as answer text, and the planner's call was
        // not streamed at all.
        Assert.Equal(0, models.Larger.StreamedCalls);
        Assert.Equal("Two notes.", string.Concat(live.OfType<AnswerChunk>().Select(chunk => chunk.Text)));
        var planned = Assert.Single(live.OfType<TurnPlanned>());
        Assert.Equal("Hello from the larger model.", planned.Text);
        Assert.Equal(plan, planned.Plan);
        Assert.True(live.IndexOf(planned) < live.FindIndex(update => update is ToolCallStarted));

        // The replay: the plan as a plan, never as answer text, before the editor's call.
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        var updates = replay.Updates.ToList();
        var replayed = Assert.Single(updates.OfType<TurnPlanned>());
        Assert.Equal(planned.Sequence, replayed.Sequence);
        Assert.Equal(plan, replayed.Plan);
        Assert.Equal(["Two notes."], updates.OfType<AnswerChunk>().Select(chunk => chunk.Text));
        Assert.True(updates.IndexOf(replayed) < updates.FindIndex(update => update is ToolCallStarted));
    }

    [Fact]
    public async Task Review_M3_the_planners_tokens_are_published_though_its_call_is_not_streamed()
    {
        // A child's token budget counts what the feed publishes (Usage, UsageEstimated): the planner's unstreamed call must be
        // in it, without a generation start or text of its own.
        var models = Models();
        await using var host = TestHost.Create(models);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        await Run(runtime, session, "/architect", host);
        var usage = new UsageCounter();

        using (runtime.Feed.Subscribe(session.Id, usage))
            Assert.Single((await session.SendAsync("list my notes", Ct)).Result!.Plans);

        Assert.Single(models.Larger.Calls);
        Assert.Equal(0, models.Larger.StreamedCalls);
        Assert.DoesNotContain("larger model", usage.Text.ToString(), StringComparison.Ordinal);
        // Every streamed call started a generation and published its tokens; the plan published its tokens too.
        Assert.True(usage.Started > 0);
        Assert.Equal(usage.Started + 1, usage.Calls);
        Assert.Equal((usage.Started + 1) * (42 + 7), usage.Tokens);
    }

    [Fact]
    public async Task Review_L5_architect_refuses_with_a_reason_when_the_session_may_not_escalate()
    {
        await using var host = TestHost.Create(Models());
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var opened = runtime.OpenSession(surface: "test");
        var runner = new TurnRunner(opened.Id, new TurnServices
        {
            Log = runtime.Log,
            Slots = opened.Slots,
            Fabric = runtime.Fabric,
            Tools = new Corvid.Tools.ToolRegistry(),
            Options = new TurnOptions { EscalationAllowed = false },
        });
        var session = new CorvidSession(runtime, opened.Id, opened.Slots, runner, opened.Actor);

        foreach (var line in new[] { "/architect", "/architect on" })
        {
            var refused = await Run(runtime, session, line, host);
            Assert.Equal(OutputTone.Error, refused.Tone);
            Assert.Contains("may not escalate", refused.ToPlainText(), StringComparison.Ordinal);
            Assert.Equal(ArchitectRequest.None, runner.Architect);
        }
        Assert.Contains("may not escalate", (await Run(runtime, session, "/architect status", host)).ToPlainText(), StringComparison.Ordinal);
    }

    private sealed class UsageCounter : ITurnObserver
    {
        public int Started;
        public int Calls;
        public long Tokens;
        public System.Text.StringBuilder Text { get; } = new();

        public void GenerationStarted() => Interlocked.Increment(ref Started);

        void ITurnObserver.Text(string delta)
        {
            lock (Text)
                Text.Append(delta);
        }

        public void Usage(long? inputTokens, long? outputTokens)
        {
            Interlocked.Increment(ref Calls);
            Interlocked.Add(ref Tokens, (inputTokens ?? 0) + (outputTokens ?? 0));
        }
    }

    [Fact]
    public async Task Architect_on_lasts_for_the_session_until_off_and_status_says_so()
    {
        await using var host = TestHost.Create(Models());
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");

        await Run(runtime, session, "/architect on", host);
        Assert.Equal(ArchitectRequest.Session, session.Runner.Architect);
        Assert.Contains("every turn of this session plans first", (await Run(runtime, session, "/architect status", host)).ToPlainText(),
            StringComparison.Ordinal);

        await Run(runtime, session, "/architect off", host);
        Assert.Equal(ArchitectRequest.None, session.Runner.Architect);
        Assert.Contains("Architect/editor: off", (await Run(runtime, session, "/architect status", host)).ToPlainText(), StringComparison.Ordinal);
        Assert.Equal(OutputTone.Error, (await Run(runtime, session, "/architect sideways", host)).Tone);
    }

    [Fact]
    public async Task The_configuration_turns_the_split_on_for_new_sessions_with_its_editor_model()
    {
        await using var host = TestHost.Create(Models(), new Dictionary<string, string?>
        {
            ["Corvid:Turn:ArchitectEditor"] = "true",
            ["Corvid:Turn:EditorModel"] = "fake-gen",
        });
        var runtime = await host.StartAsync();
        await using var _ = runtime;

        Assert.True(runtime.Options.Turn.ArchitectEditor);
        Assert.Equal("fake-gen", runtime.Options.Turn.EditorModel);
        var session = runtime.OpenSession(surface: "test");
        Assert.Contains("a coding turn plans first when it escalates", (await Run(runtime, session, "/architect status", host)).ToPlainText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_logged_plan_replays_as_a_plan_and_an_ordinary_answer_as_answer_text()
    {
        await using var host = TestHost.Create(Models());
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var head = runtime.Log.GetHead(session.Id, Ct);
        runtime.Log.Append(session.Id, head,
        [
            EventData.Create(new UserMessage("fix the bug")),
            EventData.Create(new AssistantMessage(Plan, Plan: new TurnPlan("big@cuda0", "small@cpu", "difficulty"))),
            EventData.Create(new AssistantMessage("Fixed.")),
        ], Ct);

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), ReplayOptions.Transcript, cancellationToken: Ct);

        var planned = Assert.Single(replay.Updates.OfType<TurnPlanned>());
        Assert.Equal((Plan, "big@cuda0", "small@cpu"), (planned.Text, planned.Plan.Planner, planned.Plan.Editor));
        Assert.Equal(["Fixed."], replay.Updates.OfType<AnswerChunk>().Select(chunk => chunk.Text));
    }
}
