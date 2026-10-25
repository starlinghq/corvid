using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// The running turn on the session update stream (A4): each step as it begins, a steer the step checkpoint took (K-3), a
/// running call's tail (K-2) and a clarifying question (K-6), live and, for what the log holds, on replay.
/// </summary>
public sealed class RunningTurnStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Tools a test adds beside the note tools.</summary>
    private sealed class ExtraTools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    private static async Task<(TestHost Host, CorvidRuntime Runtime)> StartAsync(FakeGenerator generator, params ToolFunction[] tools)
    {
        var host = TestHost.Create(new FakeModels(generator), services: services => services.AddSingleton<IToolProvider>(new ExtraTools(tools)));
        return (host, await host.StartAsync());
    }

    private static async Task<List<SessionStreamUpdate>> RunAsync(CorvidRuntime runtime, CorvidSession session, string prompt)
    {
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, new ToolPresentation(runtime), runtime.Log.GetHead(session.Id, Ct), update =>
        {
            lock (live)
                live.Add(update);
        });
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync(prompt, Ct);
            Assert.Null(delivery.Error);
            await reporter.FinishAsync(delivery.Result?.Text);
        }
        lock (live)
            return [.. live];
    }

    [Fact]
    public async Task A_live_turn_reports_its_step_a_running_tail_and_a_steer_and_the_replay_keeps_what_the_log_holds()
    {
        CorvidSession? session = null;
        Task<bool>? steer = null;
        var weather = new TestTool(new ToolDescriptor("check_weather", "Check the weather forecast.", """{"type":"object","properties":{}}""",
            new ToolRisk(SideEffects.None, Network: false, Destructive: false), []), arguments =>
        {
            // What the forecast service printed while it worked, and a note the person steered in meanwhile.
            ToolCallContext.From(arguments)!.Output!("asking the forecast service\n");
            steer = session!.SteerAsync("and tomorrow?");
            return "sunny";
        });
        var (host, runtime) = await StartAsync(new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny today and tomorrow.")), weather);
        await using var _ = host;
        await using var __ = runtime;
        session = runtime.OpenSession(surface: "test");

        var live = await RunAsync(runtime, session, "what's the weather?");

        Assert.True(await steer!);
        var step = Assert.Single(live.OfType<TurnStepped>());
        Assert.Equal((1, runtime.Options.Turn.MaxSteps), (step.Step, step.MaxSteps));
        var started = Assert.Single(live.OfType<ToolCallStarted>());
        var tail = Assert.Single(live.OfType<ToolCallChanged>(), changed => changed.Tail is not null);
        Assert.Equal((started.CallId, ToolCallState.InProgress, "asking the forecast service"), (tail.CallId, tail.Status, tail.Tail));
        var finished = Assert.Single(live.OfType<ToolCallChanged>(), changed => changed.Tail is null);
        var steered = Assert.Single(live.OfType<SteerSent>());
        Assert.Equal("and tomorrow?", steered.Text);
        // The order the contract documents: the step before its call, the tail inside the call, the steer before the answer
        // it led to (it is read from the log, so it comes wherever the report next reads it after it was taken).
        Assert.True(live.IndexOf(step) < live.IndexOf(started));
        Assert.True(live.IndexOf(started) < live.IndexOf(tail) && live.IndexOf(tail) < live.IndexOf(finished));
        Assert.True(live.IndexOf(steered) < live.FindIndex(update => update is AnswerChunk));

        // The log holds the steer and the call's step; a tail is live only. (The checkpoints' decisions replay too, A4 parity;
        // and each generator response's tokens, A5.)
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        Assert.Equal(["PromptSent", "UsageReported", "TurnStepped", "ToolCallStarted", "ToolCallChanged", "SteerSent", "UsageReported", "AnswerChunk"],
            replay.Updates.Where(update => update is not DecisionsMade).Select(update => update.GetType().Name));
        Assert.Equal(new TurnStepped(1, null), replay.Updates.OfType<TurnStepped>().Single() with { At = default });
        Assert.Equal(steered with { At = default }, replay.Updates.OfType<SteerSent>().Single() with { At = default });
        Assert.DoesNotContain(replay.Updates, update => update is ToolCallChanged { Tail: not null });
    }

    [Fact]
    public async Task A_clarifying_turn_reports_its_question_with_its_answer_live_and_on_replay()
    {
        var ask = Reply.Call("ask_user", """{"question":"Which version: 0.2.0 or 0.2.0-rc1?","options":["0.2.0","0.2.0-rc1"]}""");
        var generator = new FakeGenerator(ask);
        var (host, runtime) = await StartAsync(generator, new AskUserTool());
        await using var _ = host;
        await using var __ = runtime;
        var session = runtime.OpenSession(surface: "test");

        var live = await RunAsync(runtime, session, "tag the release");

        Assert.Contains("ask_user", generator.OfferedTools[0]);
        var question = Assert.Single(live.OfType<QuestionRaised>());
        Assert.Equal(("Which version: 0.2.0 or 0.2.0-rc1?", 2), (question.Text, question.Options.Length));
        Assert.Equal(["0.2.0", "0.2.0-rc1"], question.Options);
        // The turn's answer is the question in words, for a surface that knows only answers (ACP).
        Assert.Equal(question.Text, string.Concat(live.OfType<AnswerChunk>().Select(chunk => chunk.Text)));

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        var replayed = Assert.IsType<QuestionRaised>(replay.Updates[^1]);
        Assert.Equal((question.QuestionId, question.Text), (replayed.QuestionId, replayed.Text));
        Assert.Equal(question.Options, replayed.Options);
        // The conversation alone (ACP's session/load) leaves it out, as it leaves out the steps.
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), ReplayOptions.Transcript, cancellationToken: Ct);
        Assert.DoesNotContain(transcript.Updates, update => update is QuestionRaised or TurnStepped);
    }
}
