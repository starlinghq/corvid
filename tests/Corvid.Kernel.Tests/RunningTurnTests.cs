using System.Collections.Concurrent;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Fabric;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>
/// The running turn (A4): steering at the step checkpoint (K-3), the clarifying question as a structured result (K-6),
/// turn progress, and running calls' output as a bounded tail (K-2).
/// </summary>
public sealed class RunningTurnTests
{
    private const string Token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8"; // fictitious, in GitHub's classic shape

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A read-only tool the keyword embedder ranks first for "weather"; it runs <paramref name="during"/> while it works.</summary>
    private static TestTool Weather(Func<AIFunctionArguments, Task> during) => TestTool.Create("check_weather", "Check the weather forecast.",
        new ToolRisk(SideEffects.None, Network: false, Destructive: false), """{"type":"object","properties":{}}""",
        async (arguments, _) =>
        {
            await during(arguments);
            return "sunny";
        });

    private static TestTool FetchPage(Func<Task> during) => TestTool.Create("fetch_page", "Fetch a web page and return its text.",
        new ToolRisk(SideEffects.None, Network: true, Destructive: false),
        """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""",
        async (_, _) =>
        {
            await during();
            return "<p>the page</p>";
        },
        new ToolParameter("url", null, true, false));

    // ---- K-3: steering ----

    [Fact]
    public async Task A_steer_sent_while_a_call_runs_is_taken_at_the_step_checkpoint_before_the_decider_and_the_generator_read_on()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        var generator = new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny today, and tomorrow too."));
        using var harness = new TurnHarness(generator, tools: registry => registry.Add(Weather(_ =>
        {
            steer = runner!.SteerAsync("  also check tomorrow  ");
            Assert.False(steer.IsCompleted);
            return Task.CompletedTask;
        })));
        runner = harness.Runner();

        var result = await harness.RunAsync(runner, "what's the weather?");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Null(result.Question);
        Assert.Empty(harness.Payloads().OfType<ClarificationAsked>());
        Assert.True(await steer!);
        Assert.Equal(1, result.Steers);
        // Logged after the round's result and before the answer: part of the turn, not a turn of its own.
        var payloads = harness.Payloads().Where(item => item is not (Decision or DecisionOutcomeRecorded)).ToList();
        Assert.Collection(payloads,
            item => Assert.IsType<UserMessage>(item),
            item => Assert.Equal("check_weather", Assert.IsType<ToolCallIssued>(item).ToolName),
            item => Assert.IsType<ToolResult>(item),
            item => Assert.Equal("also check tomorrow", Assert.IsType<UserSteered>(item).Text),
            item => Assert.IsType<AssistantMessage>(item));
        // The step checkpoint's decider saw it in the TurnState; ingress, before it, did not.
        var done = Assert.Single(harness.Decisions("done"));
        Assert.Equal("also check tomorrow", done.State!.Value.GetProperty("steering").GetString());
        Assert.All(harness.Decisions("intent"), decision => Assert.False(decision.State!.Value.TryGetProperty("steering", out _)));
        // The generator read it as the user's words, after the result it followed.
        var messages = generator.Calls[1].Messages;
        var steered = messages.ToList().FindIndex(message => message.Role == ChatRole.User && message.Text == "also check tomorrow");
        Assert.True(steered > messages.ToList().FindIndex(message => message.Role == ChatRole.Tool));
        // One turn: the log's turn boundaries (user messages) are unchanged.
        Assert.Single(harness.Payloads().OfType<UserMessage>());
    }

    [Fact]
    public async Task A_steer_does_not_clear_taint()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        var generator = new FakeGenerator(Reply.Call("fetch_page", """{"url":"https://example.org"}"""), Reply.Say("Read it."));
        using var harness = new TurnHarness(generator, tools: registry => registry.Add(FetchPage(() =>
        {
            steer = runner!.SteerAsync("summarise it");
            return Task.CompletedTask;
        })));
        runner = harness.Runner();

        var result = await harness.RunAsync(runner, "fetch the page example.org");

        Assert.True(await steer!);
        // Outside content came in with the page; the person's steer joins that context and does not vouch for it (plan §10.3).
        Assert.True(result.Tainted);
        Assert.True(runner.Tainted);
    }

    [Fact]
    public async Task A_steer_the_turn_never_reaches_a_checkpoint_for_is_not_taken_and_not_logged()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        var generator = new FakeGenerator(Reply.Say("Hello."))
        {
            BeforeReply = (_, _) =>
            {
                steer ??= runner!.SteerAsync("never mind");
                return Task.CompletedTask;
            },
        };
        using var harness = new TurnHarness(generator);
        runner = harness.Runner();

        var result = await harness.RunAsync(runner, "hello there");

        Assert.Equal(TurnStatus.Answered, result.Status);
        // The surface is told, and sends it as an ordinary message; nothing is lost and nothing logged.
        Assert.False(await steer!);
        Assert.Equal(0, result.Steers);
        Assert.Empty(harness.Payloads().OfType<UserSteered>());
    }

    [Fact]
    public async Task With_no_turn_running_a_steer_is_not_taken_at_once()
    {
        using var harness = new TurnHarness(new FakeGenerator());
        var runner = harness.Runner();

        var steer = runner.SteerAsync("now", Ct);

        Assert.True(steer.IsCompleted);
        Assert.False(await steer);
        Assert.False(runner.TakesSteers);
    }

    [Fact]
    public async Task A_withdrawn_steer_is_not_taken()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        var generator = new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, tools: registry => registry.Add(Weather(_ =>
        {
            using var withdraw = new CancellationTokenSource();
            steer = runner!.SteerAsync("forget it", withdraw.Token);
            withdraw.Cancel();
            // Withdrawn while it still waited: answered at once.
            Assert.True(steer.IsCompleted);
            return Task.CompletedTask;
        })));
        runner = harness.Runner();

        await harness.RunAsync(runner, "what's the weather?");

        Assert.False(await steer!);
        Assert.Empty(harness.Payloads().OfType<UserSteered>());
    }

    [Fact]
    public async Task A_steer_sent_while_an_approval_prompt_waits_is_taken_at_the_checkpoint_after_the_round()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        var prompter = new FakePrompter((_, _) =>
        {
            // A4V2-1: typed while the card waits; the round has not finished, so nothing takes it yet.
            steer = runner!.SteerAsync("send it to the staging hook too");
            Assert.False(steer.IsCompleted);
            return Task.FromResult(Approvals.ApprovalAnswer.Approve());
        });
        var generator = new FakeGenerator(Reply.Call("post_webhook", """{"url":"https://example.org/hook","body":"hi"}"""), Reply.Say("Posted."));
        using var harness = new TurnHarness(generator, prompter: prompter, tools: registry => registry.Add(TestTool.PostWebhook()));
        runner = harness.Runner();

        var result = await harness.RunAsync(runner, "post hi to the webhook");

        Assert.Single(prompter.Prompts);
        Assert.True(await steer!);
        Assert.Equal(1, result.Steers);
        var payloads = harness.Payloads().Where(item => item is ToolCallIssued or ToolResult or UserSteered or AssistantMessage).ToList();
        Assert.Collection(payloads,
            item => Assert.Equal("post_webhook", Assert.IsType<ToolCallIssued>(item).ToolName),
            item => Assert.False(Assert.IsType<ToolResult>(item).IsError),
            item => Assert.Equal("send it to the staging hook too", Assert.IsType<UserSteered>(item).Text),
            item => Assert.IsType<AssistantMessage>(item));
    }

    [Fact]
    public async Task A_steer_taken_where_the_decider_says_done_keeps_the_tools_for_one_more_step()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        var generator = new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny, and windy tomorrow."))
        {
            YesNo = FakeGenerator.YesTo("fully handled"),
        };
        using var harness = new TurnHarness(generator, tools: registry => registry.Add(Weather(_ =>
        {
            steer = runner!.SteerAsync("check tomorrow's wind too");
            return Task.CompletedTask;
        })));
        runner = harness.Runner();

        var result = await harness.RunAsync(runner, "what's the weather?");

        Assert.True(await steer!);
        Assert.Equal("stop", Assert.Single(harness.Decisions("done")).Action);
        // L3: done acted on the round, but the steer asks for more: the next step still offers the tools.
        Assert.Contains("check_weather", generator.Calls[1].ToolNames);
        Assert.Contains(generator.Calls[1].Messages, message => message.Role == ChatRole.User && message.Text == "check tomorrow's wind too");
        Assert.Equal(TurnStatus.Answered, result.Status);
    }

    [Fact]
    public async Task The_actor_hands_a_steer_to_its_running_turn()
    {
        SessionActor? actor = null;
        Task<bool>? steer = null;
        var generator = new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, tools: registry => registry.Add(Weather(_ =>
        {
            steer = actor!.SteerAsync("and the wind?");
            return Task.CompletedTask;
        })));
        var sink = new CollectingSink();
        await using (actor = harness.Actor(sink))
        {
            await actor.SubmitAsync(new Abstractions.Sessions.Envelope(TurnHarness.Session, "cli", "what's the weather?",
                Abstractions.Sessions.EnvelopeOrigin.User), Ct);
            var delivery = await sink.NextAsync();
            Assert.Equal(1, delivery.Result!.Steers);
        }

        Assert.True(await steer!);
        Assert.Equal("and the wind?", Assert.Single(harness.Payloads().OfType<UserSteered>()).Text);
    }

    // ---- K-6: clarifying questions ----

    [Fact]
    public async Task An_ask_user_call_ends_the_turn_clarifying_with_its_question_after_the_rounds_other_calls()
    {
        var generator = new FakeGenerator(_ => Reply.Calls(("list_notes", "{}", null),
            ("ask_user", """{"question":"Which version: 0.2.0 or 0.2.0-rc1?","options":["0.2.0"," 0.2.0-rc1 ","0.2.0",""]}""", null)));
        // Every question below its floor: the generator is offered the whole shortlist.
        using var harness = new TurnHarness(generator, calibrated: new ScriptedDecider((_, _) => null),
            tools: registry => registry.Add(new AskUserTool()));

        var result = await harness.RunAsync("tag the release");

        Assert.Contains(AskUserTool.ToolName, generator.Calls[0].ToolNames);
        Assert.Equal(TurnStatus.Clarifying, result.Status);
        Assert.Equal("Which version: 0.2.0 or 0.2.0-rc1?", result.Text);
        var question = Assert.IsType<ClarifyingQuestion>(result.Question);
        Assert.Equal(["0.2.0", "0.2.0-rc1"], question.Options);
        // The question ends the turn: no generator call after it, and the round's other call ran.
        Assert.Single(generator.Calls);
        Assert.Equal(2, result.ToolCalls);
        var payloads = harness.Payloads().Where(item => item is not (Decision or DecisionOutcomeRecorded)).ToList();
        var asked = Assert.Single(payloads.OfType<ToolCallIssued>(), call => call.ToolName == AskUserTool.ToolName);
        var answer = Assert.Single(payloads.OfType<ToolResult>(), item => item.CallId == asked.CallId);
        Assert.False(answer.IsError);
        Assert.True(answer.Result.GetProperty("asked").GetBoolean());
        Assert.False(Assert.Single(payloads.OfType<ToolResult>(), item => item.CallId != asked.CallId).IsError);
        // The answer and the structured question, last, together.
        Assert.Equal(result.Text, Assert.IsType<AssistantMessage>(payloads[^2]).Text);
        var logged = Assert.IsType<ClarificationAsked>(payloads[^1]);
        Assert.Equal((question.Id, question.Text), (logged.QuestionId, logged.Text));
        Assert.Equal(question.Options, logged.Options);
    }

    [Fact]
    public async Task A_turn_nobody_watches_is_not_offered_ask_user()
    {
        var generator = new FakeGenerator(Reply.Say("Done."));
        using var harness = new TurnHarness(generator, calibrated: new ScriptedDecider((_, _) => null),
            options: new TurnOptions { Interactive = false }, tools: registry => registry.Add(new AskUserTool()));

        await harness.RunAsync("tag the release");

        Assert.NotEmpty(generator.Calls[0].ToolNames);
        Assert.DoesNotContain(AskUserTool.ToolName, generator.Calls[0].ToolNames);
    }

    [Fact]
    public async Task A_calibrated_clarify_logs_its_question_structured()
    {
        var generator = new FakeGenerator(Reply.Say("Which reminder do you mean?"));
        using var harness = new TurnHarness(generator, calibrated: new ScriptedDecider((key, _) => key == "clarify" ? [0.9, 0.1] : null));

        var result = await harness.RunAsync("move it");

        Assert.Equal(TurnStatus.Clarifying, result.Status);
        var question = Assert.IsType<ClarifyingQuestion>(result.Question);
        Assert.Equal(("Which reminder do you mean?", 0), (question.Text, question.Options.Length));
        var logged = Assert.Single(harness.Payloads().OfType<ClarificationAsked>());
        Assert.Equal(question.Id, logged.QuestionId);
        Assert.Empty(logged.Options);
    }

    // ---- turn progress ----

    [Fact]
    public async Task Each_step_is_reported_as_it_begins_with_the_turns_budget()
    {
        var listener = new Listener();
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Say("You have no notes."));
        using var harness = new TurnHarness(generator, options: new TurnOptions { MaxSteps = 5 }) { Listener = listener };

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(2, result.Steps);
        Assert.Equal([(TurnHarness.Session, 1, 5), (TurnHarness.Session, 2, 5)], listener.Steps);
    }

    // ---- K-2: running tails ----

    [Fact]
    public async Task A_calls_output_reaches_the_listener_as_a_scrubbed_bounded_tail_and_the_result_is_unchanged()
    {
        var listener = new Listener();
        var clock = new TestClock();
        var generator = new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, options: new TurnOptions { OutputTailLines = 3 },
            tools: registry => registry.Add(Weather(arguments =>
            {
                var context = ToolCallContext.From(arguments)!;
                context.Output!.Invoke($"one\r\ntwo\nkey {Token}\nthree\nfour\n");
                return Task.CompletedTask;
            })))
        {
            Listener = listener,
            Time = clock,
        };

        await harness.RunAsync("what's the weather?");

        var (session, callId, tool, tail) = Assert.Single(listener.Tails);
        Assert.Equal((TurnHarness.Session, "check_weather"), (session, tool));
        Assert.Equal(Assert.Single(harness.Payloads().OfType<ToolCallIssued>()).CallId, callId);
        // The last three lines, the credential scrubbed as the log scrubs results.
        Assert.Equal(3, tail.Split('\n').Length);
        Assert.DoesNotContain(Token, tail, StringComparison.Ordinal);
        Assert.StartsWith("key ghp_[REDACTED:github-token:", tail, StringComparison.Ordinal);
        Assert.EndsWith("three\nfour", tail, StringComparison.Ordinal);
        Assert.Equal("sunny", JsonValues.Text(Assert.Single(harness.Payloads().OfType<ToolResult>()).Result));
    }

    [Fact]
    public async Task Without_a_listener_a_tool_gets_no_output_sink()
    {
        Action<string>? sink = static _ => { };
        var generator = new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, tools: registry => registry.Add(Weather(arguments =>
        {
            sink = ToolCallContext.From(arguments)!.Output;
            return Task.CompletedTask;
        })));

        await harness.RunAsync("what's the weather?");

        Assert.Null(sink);
    }

    [Fact]
    public void The_tail_is_reported_at_most_once_per_interval_and_output_between_reports_joins_the_next()
    {
        var clock = new TestClock();
        var published = new List<string>();
        using var tail = new ToolOutputTail(clock, TimeSpan.FromMilliseconds(150), 2_000, 12, published.Add);

        tail.Write("first\n");
        tail.Write("second\n");
        tail.Write("third");

        // The first report goes at once; what follows within the interval waits for its end, joined.
        Assert.Equal(["first"], published);
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Single(published);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["first", "first\nsecond\nthird"], published);
        // Nothing new, nothing reported.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, published.Count);
        // A quiet interval later, the next output goes at once.
        tail.Write(" more");
        Assert.Equal("first\nsecond\nthird more", published[^1]);
    }

    [Fact]
    public void The_tail_keeps_the_last_lines_and_characters_and_nothing_is_reported_after_disposal()
    {
        var clock = new TestClock();
        var published = new List<string>();
        var tail = new ToolOutputTail(clock, TimeSpan.FromMilliseconds(100), 10, 2, published.Add);

        tail.Write(string.Concat(Enumerable.Range(0, 1_000).Select(i => $"line {i}\n")));
        Assert.Equal("line 999", Assert.Single(published));
        tail.Write("progress 10%\rprogress 20%");
        tail.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        tail.Write("late");

        // The pending report died with the call. A redraw (\r) ends a line as the terminal shows it, so the tail is the last
        // ten characters of the redrawn line only.
        Assert.Single(published);
        Assert.Equal("ogress 20%", tail.Snapshot());
    }

    [Fact]
    public async Task A_slow_surface_does_not_hold_the_tails_lock()
    {
        var clock = new TestClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var published = new ConcurrentQueue<string>();
        using var tail = new ToolOutputTail(clock, TimeSpan.FromMilliseconds(100), 2_000, 12, text =>
        {
            published.Enqueue(text);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60), Ct));
        });

        // The first write publishes at once, on the tool's thread, and the surface stalls in it.
        var writer = Task.Run(() => tail.Write("first\n"), Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(60), Ct));
        // L5: the buffer is free meanwhile: more output is taken and the tail can be read.
        var other = Task.Run(() =>
        {
            tail.Write("second\n");
            return tail.Snapshot();
        }, Ct);
        Assert.Equal("first\nsecond", await other.WaitAsync(TimeSpan.FromSeconds(60), Ct));
        release.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(["first"], published);
    }

    [Fact]
    public void A_credential_across_the_buffers_cut_never_shows_in_part()
    {
        var clock = new TestClock();
        var published = new List<string>();
        // Twice the shown size is buffered (120 characters); a JWT later in the line scrubs to far fewer characters, so
        // what was buffered from the front of the line comes into the shown tail.
        using var tail = new ToolOutputTail(clock, TimeSpan.FromMilliseconds(100), 60, 4, published.Add);
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXZhbHVlLXNpZ25hdHVyZQ";

        // One line, cut three characters in: the GitHub token loses its "ghp" and is no longer recognisable alone.
        tail.Write("ab " + Token + " " + Jwt + " end");

        var shown = tail.Snapshot();
        Assert.DoesNotContain(Token[^12..^4], shown, StringComparison.Ordinal);
        Assert.DoesNotContain(Token[^8..], shown, StringComparison.Ordinal);
        Assert.DoesNotContain(Jwt, shown, StringComparison.Ordinal);
        Assert.EndsWith(" end", shown, StringComparison.Ordinal);
        Assert.All(published, text => Assert.DoesNotContain(Token[^12..^4], text, StringComparison.Ordinal));
    }

    [Fact]
    public void Output_of_many_lines_is_cut_at_a_lines_start()
    {
        var clock = new TestClock();
        using var tail = new ToolOutputTail(clock, TimeSpan.FromMilliseconds(100), 60, 4, _ => { });
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXZhbHVlLXNpZ25hdHVyZQ";

        tail.Write("ab " + Token + "\n" + Jwt + "\nshort\n");

        var shown = tail.Snapshot();
        Assert.DoesNotContain(Token[^12..^4], shown, StringComparison.Ordinal);
        Assert.EndsWith("short", shown, StringComparison.Ordinal);
    }

    private sealed class Listener : ITurnListener
    {
        public ConcurrentQueue<(string Session, int Step, int MaxSteps)> StepQueue { get; } = new();

        public ConcurrentQueue<(string Session, string CallId, string Tool, string Tail)> TailQueue { get; } = new();

        public IReadOnlyList<(string Session, int Step, int MaxSteps)> Steps => [.. StepQueue];

        public IReadOnlyList<(string Session, string CallId, string Tool, string Tail)> Tails => [.. TailQueue];

        public void StepStarted(string sessionId, int step, int maxSteps) => StepQueue.Enqueue((sessionId, step, maxSteps));

        public void ToolOutput(string sessionId, string callId, string tool, string tail) => TailQueue.Enqueue((sessionId, callId, tool, tail));
    }
}

/// <summary>A clock the test moves: <see cref="Advance"/> fires the timers that fall due, in order, on the calling thread.</summary>
internal sealed class TestClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Timer> _timers = [];
    private long _ticks = new DateTimeOffset(2026, 10, 24, 14, 5, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return new DateTimeOffset(_ticks, TimeSpan.Zero);
    }

    public override long GetTimestamp()
    {
        lock (_gate)
            return _ticks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
            target = _ticks + by.Ticks;
        while (true)
        {
            Timer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    _ticks = target;
                    return;
                }
                _ticks = Math.Max(_ticks, next.Due);
                _timers.Remove(next);
            }
            next.Fire();
        }
    }

    private sealed class Timer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                Due = clock._ticks + dueTime.Ticks;
                clock._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
