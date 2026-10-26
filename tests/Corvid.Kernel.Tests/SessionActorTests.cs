using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Memory;
using Corvid.Models;

namespace Corvid.Kernel.Tests;

public sealed class SessionActorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Envelope Message(string text) => new(TurnHarness.Session, "cli", text);

    [Fact]
    public async Task Turns_run_one_at_a_time_in_arrival_order()
    {
        var generator = new FakeGenerator(Reply.Say("first"), Reply.Say("second"));
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);

        await actor.SubmitAsync(Message("hello there"), Ct);
        await actor.SubmitAsync(Message("hello again"), Ct);

        Assert.Equal("first", (await sink.NextAsync()).Result!.Text);
        Assert.Equal("second", (await sink.NextAsync()).Result!.Text);
        Assert.Equal(["hello there", "hello again"], harness.Payloads().OfType<UserMessage>().Select(message => message.Text));
    }

    [Fact]
    public async Task A_model_switch_runs_while_a_call_is_parked_at_an_approval_prompt()
    {
        var answer = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompter = new FakePrompter((_, token) =>
        {
            asked.TrySetResult();
            return answer.Task.WaitAsync(token);
        });
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"buy milk"}""", "c-park"));
        var large = new FakeGenerator(Reply.Say("Saved, on the large model.")) { Name = "large" };
        using var harness = new TurnHarness(generator, escalation: large, prompter: prompter);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);

        await actor.SubmitAsync(Message("note: buy milk"), Ct);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        // The actor keeps reading commands while the turn waits on the prompt, and the parked call does not block the switch.
        var switched = await actor.SwitchModelAsync(ModelSlots.Generator, harness.EscalationBinding!, cancellationToken: Ct)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);
        answer.SetResult(ApprovalAnswer.Approve());
        var delivery = await sink.NextAsync();

        Assert.Equal(SwitchStatus.Switched, switched.Status);
        Assert.Equal(["c-park"], switched.UnfinishedToolCalls);
        Assert.Single(switched.PendingApprovals);
        Assert.Equal("Saved, on the large model.", delivery.Result!.Text);
        Assert.Equal("buy milk", Assert.Single(harness.Notes.Notes).Text);
        // The prompt is linked to its call in the log, and the result follows the switch.
        var events = harness.Payloads();
        Assert.Equal("c-park", events.OfType<ApprovalRequested>().Single().CallId);
        var switchIndex = events.ToList().FindIndex(item => item is ModelSwitched { Reason: "session" });
        var resultIndex = events.ToList().FindIndex(item => item is ToolResult);
        Assert.True(switchIndex < resultIndex);
    }

    [Fact]
    public async Task Cancelling_mid_turn_ends_that_turn_and_the_next_message_still_runs()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The cancelled call never reaches the script, so the second turn gets its first step.
        var generator = new FakeGenerator(Reply.Say("second answer"));
        generator.BeforeReply = async (call, token) =>
        {
            if (generator.Calls.Count == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);

        await actor.SubmitAsync(Message("hello there"), Ct);
        await actor.SubmitAsync(Message("hello again"), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        actor.CancelTurn();

        var cancelled = await sink.NextAsync();
        Assert.Equal(TurnStatus.Cancelled, cancelled.Result!.Status);
        Assert.Equal("second answer", (await sink.NextAsync()).Result!.Text);
        Assert.Equal(["second answer"], harness.Payloads().OfType<AssistantMessage>().Select(message => message.Text));
    }

    [Fact]
    public async Task CancelTurn_has_cancelled_the_running_turn_when_it_returns()
    {
        // ACP cancels the turn and then lets its permission request end as cancelled; the turn must already see the cancel
        // when it resumes, or it makes its next model call and ends normally ("end_turn" after session/cancel).
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(Reply.Say("answered anyway"));
        generator.BeforeReply = async (_, token) =>
        {
            started.TrySetResult(token);
            await release.Task; // ignores the token, as a model call that is already returning would
        };
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);
        await actor.SubmitAsync(Message("hello there"), Ct);
        var turn = await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        actor.CancelTurn();
        var cancelledOnReturn = turn.IsCancellationRequested;
        release.SetResult();

        Assert.True(cancelledOnReturn);
        Assert.Equal(TurnStatus.Cancelled, (await sink.NextAsync()).Result!.Status);
    }

    [Fact]
    public async Task CancelTurn_does_not_cancel_the_turn_that_starts_after_its_target_finished()
    {
        // The window: CancelTurn has cancelled the running turn but not yet posted its command. The cancelled turn ends,
        // the loop starts the queued message, and only then reads the command: it must leave the new turn alone.
        var startedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The cancelled call never reaches the script, so the second turn gets its first step.
        var generator = new FakeGenerator(Reply.Say("second answer"));
        generator.BeforeReply = async (_, _) =>
        {
            // Both ignore the token; FakeGenerator throws after this if the token is cancelled.
            if (generator.Calls.Count == 1)
            {
                startedFirst.TrySetResult();
                await releaseFirst.Task;
            }
            else
            {
                startedSecond.TrySetResult();
                await releaseSecond.Task;
            }
        };
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);
        await actor.SubmitAsync(Message("hello there"), Ct);
        await actor.SubmitAsync(Message("hello again"), Ct);
        await startedFirst.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        actor.CancelSignalled = () =>
        {
            releaseFirst.SetResult();
            startedSecond.Task.Wait(TimeSpan.FromSeconds(30), Ct);
        };

        actor.CancelTurn();
        Assert.True(startedSecond.Task.IsCompleted);
        // The switch is read after the cancel: once it answers, the loop has handled the cancel.
        await actor.SwitchModelAsync(ModelSlots.Generator, harness.GeneratorBinding, cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        releaseSecond.SetResult();

        Assert.Equal(TurnStatus.Cancelled, (await sink.NextAsync()).Result!.Status);
        var second = (await sink.NextAsync()).Result!;
        Assert.Equal(TurnStatus.Answered, second.Status);
        Assert.Equal("second answer", second.Text);
    }

    [Fact]
    public async Task CancelTurn_cancels_a_message_submitted_before_it_whose_turn_has_not_started()
    {
        var generator = new FakeGenerator(Reply.Say("second answer"));
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first turn may be cancelled before or at its model call; at the call it waits for the cancel.
        generator.BeforeReply = (call, token) => call.LastText.Contains("hello there", StringComparison.Ordinal)
            ? never.Task.WaitAsync(token)
            : Task.CompletedTask;
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);

        // Whether or not the loop has started the turn yet, the cancel is read after the message and targets its turn.
        await actor.SubmitAsync(Message("hello there"), Ct);
        actor.CancelTurn();
        await actor.SubmitAsync(Message("hello again"), Ct);

        Assert.Equal(TurnStatus.Cancelled, (await sink.NextAsync()).Result!.Status);
        Assert.Equal("second answer", (await sink.NextAsync()).Result!.Text);
    }

    [Fact]
    public async Task CancelTurn_with_nothing_running_or_queued_does_not_cancel_a_later_message()
    {
        var generator = new FakeGenerator(Reply.Say("answered"));
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);

        actor.CancelTurn();
        await actor.SubmitAsync(Message("hello there"), Ct);

        var delivery = (await sink.NextAsync()).Result!;
        Assert.Equal(TurnStatus.Answered, delivery.Status);
        Assert.Equal("answered", delivery.Text);
    }

    [Fact]
    public async Task A_full_session_makes_surfaces_wait()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator();
        generator.BeforeReply = (_, token) => release.Task.WaitAsync(token);
        using var harness = new TurnHarness(generator);
        var sink = new CollectingSink();
        await using var host = new SessionHost(_ => harness.Actor(sink, new SessionActorOptions { MaxPendingMessages = 1 }),
            new SessionHostOptions { Capacity = 1 });

        // One running, one waiting for the session, one in the channel; the fourth post waits.
        await host.PostAsync(Message("hello one"), Ct);
        await host.PostAsync(Message("hello two"), Ct);
        await WaitUntil(() => harness.Payloads().OfType<UserMessage>().Any());
        await host.PostAsync(Message("hello three"), Ct);
        var fourth = host.PostAsync(Message("hello four"), Ct).AsTask();
        await Task.Delay(200, Ct);
        Assert.False(fourth.IsCompleted);

        release.SetResult();
        await fourth.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        for (var i = 0; i < 4; i++)
            Assert.NotNull((await sink.NextAsync()).Result);
        Assert.Equal(4, harness.Payloads().OfType<UserMessage>().Count());
    }

    [Fact]
    public async Task A_session_whose_actor_cannot_be_built_does_not_stop_routing_for_others()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("still routing")));
        var sink = new CollectingSink();
        var failures = new List<(Envelope Envelope, Exception Error)>();
        await using var host = new SessionHost(
            id => id == TurnHarness.Session ? harness.Actor(sink) : throw new InvalidOperationException("no such session"),
            new SessionHostOptions { RoutingFailed = (envelope, error) => { lock (failures) failures.Add((envelope, error)); } });

        await host.PostAsync(new Envelope("s-missing", "cli", "hello"), Ct);
        await host.PostAsync(Message("hello there"), Ct);

        Assert.Equal("still routing", (await sink.NextAsync()).Result!.Text);
        lock (failures)
            Assert.Equal("s-missing", Assert.Single(failures).Envelope.SessionId);
    }

    [Fact]
    public async Task Switching_the_text_embedder_starts_the_re_embed_job()
    {
        var generator = new FakeGenerator();
        using var harness = new TurnHarness(generator);
        using var store = new MemoryStore();
        var replacement = new KeywordEmbedder("sister");
        var vectors = new MemoryVectors(store.Database, store.Index,
            [new MemoryEmbedder("keywords", harness.Embedder), new MemoryEmbedder("sister-words", replacement)]);
        vectors.BeginSwap("keywords");
        var fact = store.Facts.Add(new FactInput("Ana is my sister"));
        Assert.True(await vectors.EmbedAsync([(fact.FactId, fact.Text)], Ct));
        await using var job = new EmbedderSwapJob(vectors);
        await using var actor = harness.Actor(new CollectingSink(), new SessionActorOptions { EmbedderSwap = job });

        var target = new ModelBinding(TurnHarness.Profile("sister-words", ToolCallSupport.None, 512), replacement);
        var switched = await actor.SwitchModelAsync(ModelSlots.TextEmbedder, target, cancellationToken: Ct);
        var progress = await job.Current.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(SwitchStatus.Switched, switched.Status);
        Assert.True(progress!.Completed);
        Assert.Equal(new EmbedderState("sister-words", null), vectors.State);
        Assert.Contains(replacement.Batches, batch => batch.Contains("Ana is my sister"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10, Ct);
        Assert.True(condition());
    }
}
