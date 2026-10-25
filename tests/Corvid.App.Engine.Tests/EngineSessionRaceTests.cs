using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SessionState = Corvid.App.Core.Shell.SessionState;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// The session service where two requests meet (the A3 review): a card against an interrupt, a failed start against the
/// queue, an interrupt before the session begins the window's message, two answers to one card, a restore during a load,
/// two change-list reads; and what it keeps (streamed chunks joined, idle channels let go). Each race is made with the
/// service's test pause at the step where the other request can come, never with the clock.
/// </summary>
public sealed class EngineSessionRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeModels AddNote(params Func<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>, Microsoft.Extensions.AI.ChatResponse>[] more) =>
        new(new FakeGenerator([Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added."), .. more]));

    private static List<string> Sent(SessionWaiter waiter) => [.. waiter.Updates.OfType<UserMessage>().Select(message => message.Text)];

    [Fact]
    public async Task AnInterruptBeforeTheCardIsRegisteredLeavesNoCardOpen()
    {
        var models = AddNote();
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        // The interrupt comes after the prompt reached the prompter and before its card is on the stream.
        service.Pause = async point =>
        {
            if (point == "card")
                await service.InterruptAsync(id, Ct);
        };

        await service.SendAsync(id, "add a note: water plants", Ct);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        // No card is put up after the interrupt, so none is left that nothing closes; nothing was approved.
        Assert.Empty(waiter.Updates.OfType<ApprovalRequested>());
        var view = SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]);
        Assert.Empty(view.Approvals);
        Assert.DoesNotContain("water plants", models.Tools.Notes);
        Assert.Equal(TurnOutcome.Cancelled, waiter.Updates.OfType<TurnEnded>().Single().Outcome);
        var summary = Assert.Single(await service.ListAsync(Ct), item => item.Id == id);
        Assert.Equal((SessionState.Idle, 0), (summary.State, summary.PendingApprovals));
    }

    [Fact]
    public async Task AStartThatFailsHandsTheSessionToWhatQueuedBehindIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("B's answer."))));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        var starts = 0;
        // "b" is queued while "a" starts, then "a"'s start fails.
        service.Pause = async point =>
        {
            if (point != "start" || Interlocked.Increment(ref starts) != 1)
                return;
            await service.QueueAsync(id, "b", Ct);
            throw new InvalidOperationException("the start failed");
        };

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(id, "a", Ct));
        Assert.Equal("the start failed", failed.Message);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        Assert.Equal(["b"], Sent(waiter));
        Assert.Empty(waiter.Updates.OfType<QueueChanged>().Last().Items);
        Assert.Equal(TurnOutcome.Answered, waiter.Updates.OfType<TurnEnded>().Single().Outcome);
    }

    [Fact]
    public async Task AQueuedMessageThatCouldNotStartGoesBeforeTheNextOne()
    {
        var models = AddNote(Reply.Say("B's answer."), Reply.Say("C's answer."));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        var starts = 0;
        // The second start, "b"'s from the queue, fails once.
        service.Pause = point => point == "start" && Interlocked.Increment(ref starts) == 2
            ? Task.FromException(new InvalidOperationException("the start failed"))
            : Task.CompletedTask;

        await service.SendAsync(id, "a", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        await service.QueueAsync(id, "b", Ct);
        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Once, Ct);
        await waiter.WaitAsync<Notice>(notice => notice.Kind == NoticeKind.Warning);
        Assert.Equal(["b"], waiter.Updates.OfType<QueueChanged>().Last().Items.Select(item => item.Text));

        // The next message queues behind the one put back, which goes first.
        await service.QueueAsync(id, "c", Ct);
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 3);
        await service.IdleAsync();

        Assert.Equal(["a", "b", "c"], Sent(waiter));
        Assert.Empty(waiter.Updates.OfType<QueueChanged>().Last().Items);
    }

    [Fact]
    public async Task AnInterruptBeforeTheSessionBeginsTheMessageCancelsItsTurnNotAnotherSurfaces()
    {
        using var otherEntered = new ManualResetEventSlim();
        using var otherGo = new ManualResetEventSlim();
        using var mineGo = new ManualResetEventSlim();
        var generator = new FakeGenerator(
            // Another surface's turn holds the session's actor until the test lets it go.
            _ =>
            {
                otherEntered.Set();
                Assert.True(otherGo.Wait(TimeSpan.FromSeconds(60)));
                return Reply.Text("The other surface's answer.");
            },
            // The window's turn begins once that one ends; it calls a tool, so it reaches the generator again.
            _ =>
            {
                Assert.True(mineGo.Wait(TimeSpan.FromSeconds(60)));
                return Reply.Call("list_notes")([]);
            },
            Reply.Say("The window's answer."));
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(generator));
        var session = fixture.Runtime.OpenSession(surface: "app");
        var id = session.Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        var other = session.SendAsync("the other surface's message", Ct);
        Assert.True(otherEntered.Wait(TimeSpan.FromSeconds(60), Ct));
        // The window's message waits behind it in the actor; Esc now must not reach the other surface's turn.
        await service.SendAsync(id, "the window's message", Ct);
        await service.InterruptAsync(id, Ct);
        otherGo.Set();

        var delivered = await other.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(Corvid.Kernel.Turns.TurnStatus.Answered, delivered.Result?.Status);
        // The window's message begins: the interrupt asked for before it is carried out.
        await waiter.WaitAsync<UserMessage>(message => message.Text == "the window's message");
        mineGo.Set();
        var ended = await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        Assert.Equal(TurnOutcome.Cancelled, ended.Outcome);
        // The window's turn stopped before its tool's result went back to the generator (as it begins, or at its first step).
        Assert.InRange(generator.Calls.Count, 1, 2);
    }

    [Fact]
    public async Task AnInterruptPendingWhenAnotherSurfacePostsBeforeTheWindowDoesNotCancelThatTurn()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var firstGo = new ManualResetEventSlim();
        var generator = new FakeGenerator(
            // Another surface's first turn holds the actor while the window reads its head and posts.
            _ =>
            {
                firstEntered.Set();
                Assert.True(firstGo.Wait(TimeSpan.FromSeconds(60)));
                return Reply.Text("The first answer.");
            },
            // The other surface's second message, posted after the window read its head and before it posted.
            Reply.Say("The second answer."),
            Reply.Say("The window's answer."));
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(generator));
        var session = fixture.Runtime.OpenSession(surface: "app");
        var id = session.Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        var first = session.SendAsync("the other surface's first message", Ct);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(60), Ct));
        Task<Corvid.Kernel.Sessions.Delivery>? second = null;
        service.Pause = async point =>
        {
            if (point == "post" && second is null)
                second = await session.PostAsync("the other surface's second message", Corvid.Abstractions.Sessions.EnvelopeOrigin.User, Ct);
        };

        await service.SendAsync(id, "the window's message", Ct);
        await service.InterruptAsync(id, Ct);
        firstGo.Set();

        Assert.Equal(Corvid.Kernel.Turns.TurnStatus.Answered, (await first.WaitAsync(TimeSpan.FromSeconds(60), Ct)).Result?.Status);
        // The other surface's second turn began while the window's interrupt waited for the window's own: it runs to its answer.
        Assert.NotNull(second);
        Assert.Equal(Corvid.Kernel.Turns.TurnStatus.Answered, (await second.WaitAsync(TimeSpan.FromSeconds(60), Ct)).Result?.Status);
        await waiter.WaitAsync<UserMessage>(message => message.Text == "the window's message");
        var ended = await waiter.WaitAsync<TurnEnded>(turn => turn.Outcome == TurnOutcome.Cancelled);
        await service.IdleAsync();

        // The window's turn is the one cancelled.
        Assert.Equal("interrupted", ended.Error);
    }

    [Fact]
    public async Task ASecondAnswerThatLosesTheRaceChangesNothingOfTheFirsts()
    {
        var models = AddNote(Reply.Say("Never sent."));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        // A denial with a reason arrives after the approval won, before the card closes.
        service.Pause = async point =>
        {
            if (point == "answered")
                await service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Deny, "too late"), Ct);
        };
        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Once, Ct);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        var resolved = Assert.Single(waiter.Updates.OfType<ApprovalResolved>());
        Assert.Equal((ApprovalOutcome.Approved, ApprovalScope.Once, (string?)null, (string?)null), (resolved.Outcome, resolved.Scope, resolved.Reason, resolved.Rule));
        Assert.Contains("water plants", models.Tools.Notes);
        Assert.Equal(["add a note: water plants"], Sent(waiter));
    }

    [Fact]
    public async Task WhatIsPublishedWhileTheLogIsReadFollowsTheReplayedHistory()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        var session = fixture.Runtime.OpenSession(surface: "app");
        await session.SendAsync("hello", Ct);
        using var service = fixture.Sessions();
        var rewound = new SessionRewound(1, RewindScope.FilesOnly, "a restore while the session loads") { Refused = true };
        service.Pause = point =>
        {
            if (point == "replay")
                service.PublishRewound(session.Id, rewound);
            return Task.CompletedTask;
        };

        using var subscription = await service.OpenAsync(session.Id, _ => { }, Ct);

        var history = subscription.History.ToList();
        var restore = history.FindIndex(update => update is SessionRewound);
        Assert.True(restore > history.FindIndex(update => update is UserMessage { Text: "hello" }), "the restore comes after the replayed history");
        Assert.True(restore > history.FindIndex(update => update is TurnEnded));
    }

    [Fact]
    public async Task AnEarlierChangeListReadThatFinishesLastIsNotPublished()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        var session = fixture.Runtime.OpenSession(surface: "app");
        await session.SendAsync("hello", Ct);
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(session.Id, waiter.Add, Ct);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        // The first read is held after it read the ledger, until the second has been published.
        service.Pause = point =>
        {
            if (point != "changes" || Interlocked.Increment(ref reads) != 1)
                return Task.CompletedTask;
            reached.TrySetResult();
            return release.Task;
        };

        service.PublishRewound(session.Id, new SessionRewound(1, RewindScope.FilesOnly, "first"));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        fixture.Time.Advance(60);
        var later = fixture.Time.GetUtcNow();
        service.PublishRewound(session.Id, new SessionRewound(1, RewindScope.FilesOnly, "second"));
        await waiter.WaitAsync<ChangesUpdate>(changes => changes.At == later);
        release.SetResult();
        await service.IdleAsync();

        Assert.Equal(later, waiter.Updates.OfType<ChangesUpdate>().Last().At);
        Assert.Single(waiter.Updates.OfType<ChangesUpdate>());
    }

    [Fact]
    public async Task AWaitingTurnsActivityIsItsOpenCall()
    {
        await using var fixture = await EngineFixture.StartAsync(AddNote());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        SessionSummary? running = null;
        // Announced, its card not up yet: the call is what runs.
        service.Pause = async point =>
        {
            if (point == "card")
                running = Assert.Single(await service.ListAsync(Ct), item => item.Id == id);
        };

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        service.Pause = null;
        Assert.NotNull(running);
        Assert.Equal(SessionState.Running, running.State);
        Assert.StartsWith("add_note", running.Activity, StringComparison.Ordinal);
        var waiting = Assert.Single(await service.ListAsync(Ct), item => item.Id == id);
        Assert.Equal((SessionState.Waiting, "waiting on you"), (waiting.State, waiting.Activity));

        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Deny, Ct);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();
        var idle = Assert.Single(await service.ListAsync(Ct), item => item.Id == id);
        Assert.Equal((SessionState.Idle, (string?)null), (idle.State, idle.Activity));
    }

    [Fact]
    public async Task AChannelNobodyWatchesIsLetGoAndTheNextOpenReplaysTheLog()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."), Reply.Say("Again."))));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();

        var subscription = await service.OpenAsync(id, _ => { }, Ct);
        await service.SendAsync(id, "hello", Ct);
        await service.IdleAsync();
        Assert.Equal(1, service.ChannelCount);
        subscription.Dispose();
        Assert.Equal(0, service.ChannelCount);

        // A turn sent with nobody watching lets its channel go when it ends.
        await service.SendAsync(id, "again", Ct);
        await service.IdleAsync();
        Assert.Equal(0, service.ChannelCount);

        using var reopened = await service.OpenAsync(id, _ => { }, Ct);
        Assert.Equal(["hello", "again"], reopened.History.OfType<UserMessage>().Select(message => message.Text));
        Assert.Equal(2, reopened.History.OfType<TurnEnded>().Count());
    }

    [Fact]
    public async Task AJobsRunIsReadOnlyAndTheWindowSendsNothingIntoIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Tidied."))));
        const string id = "job:nightly-tidy:r-1";
        var run = fixture.Runtime.OpenSession(id, "job");
        await run.SendAsync("tidy the workspace", Ct);
        using var service = fixture.Sessions();

        var summary = Assert.Single(await service.ListAsync(Ct), item => item.Id == id);
        Assert.Equal((SessionState.Job, true, "nightly-tidy"), (summary.State, summary.ReadOnly, summary.Job));
        Assert.NotNull(summary.Holder);
        using var subscription = await service.OpenAsync(id, _ => { }, Ct);
        Assert.Contains(subscription.History, update => update is DriverChanged { ReadOnly: true });
        Assert.True(SessionFold.Replay(id, subscription.History).ReadOnly);

        // Nothing the window does writes to it: no message (whose turn would attach the window's card to the job's session), no
        // mode, goal, command or compaction.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(id, "and more", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueueAsync(id, "and more", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetApprovalModeAsync(id, Corvid.App.Core.Shell.ApprovalMode.Permissive, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetGoalAsync(id, "a goal", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunCommandAsync(id, "/help", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(id, null, Ct));
        await service.IdleAsync();
        Assert.Equal(1, run.Snapshot().Transcript.Count(entry => entry.Content is Corvid.Abstractions.Events.UserMessage));
        Assert.Equal(Corvid.Approvals.ApprovalMode.Smart, run.ApprovalMode);
    }

    [Fact]
    public async Task AHostThatCannotStartLeavesTheReasonForTheWindow()
    {
        await using var host = EngineHosts.Create(services: services => services.AddHostedService<Refusing>());
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Ct);

        Assert.Equal("Corvid cannot start: the hosted service refused to start", engine.StartFailure);
        var refused = await Assert.ThrowsAsync<Corvid.App.Core.Composition.ServiceUnavailableException>(engine.RuntimeAsync);
        Assert.Equal(engine.StartFailure, refused.Message);
    }

    [Fact]
    public async Task TheServicesReadTheHermesHomeUnderTheHomeTheyAreGiven()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var hermes = Path.Combine(fixture.Host.Root, ".hermes");
        foreach (var skill in (string[])["one", "two", "three"])
        {
            Directory.CreateDirectory(Path.Combine(hermes, "skills", skill));
            await File.WriteAllTextAsync(Path.Combine(hermes, "skills", skill, "SKILL.md"), $"# {skill}\n", Ct);
        }
        using var services = new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null);
        using var composition = new Corvid.App.Core.Composition.AppComposition(new Corvid.App.Core.AppOptions { Demo = false },
            new Corvid.App.Core.Mvvm.InlineDispatcher(), fixture.Time, services);

        var found = await composition.FirstRunService.FindHermesAsync(Ct);

        Assert.Equal(fixture.Host.Root, services.Home);
        Assert.NotNull(found);
        Assert.Equal(("~/.hermes", 3), (found.Path, found.Skills));
    }

    private sealed class Refusing : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("the hosted service refused to start");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
