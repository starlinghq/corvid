using Corvid.App.Core.Review;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;
using AppScope = Corvid.App.Core.Sessions.RewindScope;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// G-12 in the window: a session another process drives opens read-only with that process named, is followed as it
/// grows, turns writable once the other process lets go, and refuses Rewind's restore and Review's revert meanwhile. A
/// second runtime over the same data directory stands in for the other process (<c>corvid chat --session</c>).
/// </summary>
public sealed class EngineDriverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<CorvidRuntime> OtherProcessAsync(TestHost host) =>
        host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Ct);

    [Fact]
    public async Task ASessionAnotherProcessDrivesIsFollowedReadOnlyUntilItLetsGo()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(
            Reply.Say("One."), Reply.Say("Two."), Reply.Say("Three."))));
        var other = await OtherProcessAsync(fixture.Host);
        var terminal = other.OpenSession("cli-held");
        await terminal.SendAsync("first", Ct);
        using var sessions = fixture.Sessions();
        var waiter = new SessionWaiter();

        using var subscription = await sessions.OpenAsync("cli-held", waiter.Add, Ct);

        var driver = Assert.IsType<DriverChanged>(subscription.History.First());
        Assert.True(driver.ReadOnly);
        Assert.Contains("driven by", driver.Holder, StringComparison.Ordinal);
        Assert.Contains($"(pid {Environment.ProcessId})", driver.Holder, StringComparison.Ordinal);
        var listed = (await sessions.ListAsync(Ct)).Single(summary => summary.Id == "cli-held");
        Assert.True(listed.ReadOnly);
        Assert.Equal(driver.Holder, listed.Holder);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.SendAsync("cli-held", "from the window", Ct));
        Assert.Contains("read-only here: driven by", refused.Message, StringComparison.Ordinal);
        Assert.Equal(LockState.HeldElsewhere, fixture.Runtime.Driver("cli-held").State);

        // The terminal goes on: the window follows at its next look.
        await terminal.SendAsync("second", Ct);
        fixture.Time.Advance(EngineSessionService.TailInterval);
        await sessions.IdleAsync();
        Assert.Contains(waiter.Updates, update => update is UserMessage { Text: "second" });
        Assert.Contains(waiter.Updates, update => update is AgentMessageChunk { Text: "Two." });
        Assert.Contains(waiter.Updates, update => update is TurnEnded { Turn: 2 });

        // The terminal exits: the window can drive it now, and its message makes it this process's.
        await other.DisposeAsync();
        fixture.Time.Advance(EngineSessionService.TailInterval);
        await sessions.IdleAsync();
        Assert.Contains(waiter.Updates, update => update is DriverChanged { ReadOnly: false });
        Assert.False((await sessions.ListAsync(Ct)).Single(summary => summary.Id == "cli-held").ReadOnly);
        await sessions.SendAsync("cli-held", "third", Ct);
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 3);
        await sessions.IdleAsync();
        Assert.Contains(waiter.Updates, update => update is AgentMessageChunk { Text: "Three." });
        Assert.Equal(LockState.HeldHere, fixture.Runtime.Driver("cli-held").State);
        // Each message is shown once: the tail stopped when this process took the session.
        Assert.Single(waiter.Updates.Concat(subscription.History), update => update is UserMessage { Text: "second" });
    }

    [Fact]
    public async Task ASessionAnotherProcessTakesWhileItIsOpenTurnsReadOnly()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("One."), Reply.Say("Two."))));
        await using (var earlier = await OtherProcessAsync(fixture.Host))
            await earlier.OpenSession("cli-free").SendAsync("first", Ct);
        using var sessions = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await sessions.OpenAsync("cli-free", waiter.Add, Ct);
        Assert.DoesNotContain(subscription.History, update => update is DriverChanged);

        await using var other = await OtherProcessAsync(fixture.Host);
        other.OpenSession("cli-free");
        fixture.Time.Advance(EngineSessionService.TailInterval);
        await sessions.IdleAsync();

        var changed = Assert.Single(waiter.Updates.OfType<DriverChanged>());
        Assert.True(changed.ReadOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.SendAsync("cli-free", "from the window", Ct));
    }

    [Fact]
    public async Task ATurnTheOtherProcessRunsEndsWithItsAnswerNotAtAReadThatShowsNothing()
    {
        await using var fixture = await EngineFixture.StartAsync();
        await using var other = await OtherProcessAsync(fixture.Host);
        other.OpenSession("cli-steps");
        using var sessions = fixture.Sessions();
        var waiter = new SessionWaiter();
        void Append(params Abstractions.Events.SessionEvent[] events) =>
            other.Log.Append("cli-steps", other.Log.GetHead("cli-steps", Ct), [.. events.Select(Abstractions.Events.EventData.Create)], Ct);
        async Task LookAsync()
        {
            fixture.Time.Advance(EngineSessionService.TailInterval);
            await sessions.IdleAsync();
        }
        Append(new Abstractions.Events.UserMessage("first"), new Abstractions.Events.AssistantMessage("One."));
        using var subscription = await sessions.OpenAsync("cli-steps", waiter.Add, Ct);

        // The other process's next turn, a step at a time: its prompt, an event no replay shows, then its answer.
        Append(new Abstractions.Events.UserMessage("second"));
        await LookAsync();
        Append(new Abstractions.Events.GoalSet("tidy up"));
        await LookAsync();
        Assert.DoesNotContain(waiter.Updates, update => update is TurnEnded { Turn: 2 });
        Append(new Abstractions.Events.AssistantMessage("Two."));
        await LookAsync();

        var ended = Assert.Single(waiter.Updates.OfType<TurnEnded>(), update => update.Turn == 2);
        Assert.Equal(TurnOutcome.Answered, ended.Outcome);
    }

    [Fact]
    public async Task TheSessionShownStaysInTheRailWhenAnotherProcessTakesIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("One."))));
        await using (var earlier = await OtherProcessAsync(fixture.Host))
            await earlier.OpenSession("cli-shown").SendAsync("first", Ct);
        var dispatcher = new Core.Mvvm.InlineDispatcher();
        var bar = new Core.Shell.StatusBarViewModel();
        var shell = new Core.Shell.ShellViewModel(bar);
        using var sessions = fixture.Sessions(dispatcher);
        using var hub = new SessionHub(sessions, dispatcher, fixture.Time, shell, bar);
        await hub.StartAsync("cli-shown", null, Ct);
        Assert.Equal("cli-shown", shell.CurrentSession?.Id);
        var view = hub.Get("cli-shown");
        // The channel the view shares, loaded (and followed) before the other process takes the session.
        using var loaded = await sessions.OpenAsync("cli-shown", _ => { }, Ct);

        await using var other = await OtherProcessAsync(fixture.Host);
        other.OpenSession("cli-shown");
        fixture.Time.Advance(EngineSessionService.TailInterval);
        await sessions.IdleAsync();

        // Read-only, and still the session shown: the rail keeps it rather than leaving the page with none.
        Assert.True(view.IsReadOnly);
        Assert.Equal("cli-shown", shell.CurrentSession?.Id);
        Assert.Contains(shell.SessionGroups.SelectMany(group => group.Sessions), item => item.Id == "cli-shown");
    }

    [Fact]
    public async Task AMessageToASessionAnotherProcessJustTookIsRefusedAndTheViewTurnsReadOnly()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("One."))));
        await using (var earlier = await OtherProcessAsync(fixture.Host))
            await earlier.OpenSession("cli-race").SendAsync("first", Ct);
        using var sessions = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await sessions.OpenAsync("cli-race", waiter.Add, Ct);
        await using var other = await OtherProcessAsync(fixture.Host);
        other.OpenSession("cli-race");

        // Before the next look: the runtime refuses to open it, naming the other process.
        var refused = await Assert.ThrowsAsync<SessionHeldException>(() => sessions.SendAsync("cli-race", "from the window", Ct));
        Assert.Equal("cli-race", refused.SessionId);
        await sessions.IdleAsync();
        Assert.Contains(waiter.Updates, update => update is DriverChanged { ReadOnly: true });
        Assert.False(fixture.Runtime.TryGetSession("cli-race", out _));
    }

    [Fact]
    public async Task RewindAndReviewRefuseToRestoreASessionAnotherProcessDrives()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        fixture.Write("story.txt", "one\n");
        // Written to the log by "the terminal" (not opened by this runtime), then held by it.
        await fixture.WriteTurnAsync("cli-busy", "call-1", "story.txt", "ONE\n", "capitalise");
        await using var other = await fixture.Host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Ct);
        other.OpenSession("cli-busy");
        using var rewind = fixture.Rewind();
        using var review = fixture.Review();
        var entry = (await rewind.GetTimelineAsync("cli-busy", Ct)).Entries.Single();

        var restored = await rewind.RestoreAsync("cli-busy", entry.CheckpointId, AppScope.FilesAndConversation, Ct);

        Assert.True(restored.Refused);
        Assert.Contains("is driven by", restored.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", restored.Detail, StringComparison.Ordinal);
        Assert.Equal("ONE\n", fixture.Read("story.txt"));

        var file = (await review.GetChangesAsync("cli-busy", ReviewScope.ThisSession, Ct)).Files.Single();
        var reverted = await review.RevertFileAsync("cli-busy", file.Path, file.CheckpointId!, Ct);
        Assert.True(reverted.Refused);
        Assert.Contains("is driven by", reverted.Reason, StringComparison.Ordinal);
        Assert.Equal("ONE\n", fixture.Read("story.txt"));
        Assert.False(fixture.Runtime.TryGetSession("cli-busy", out _));
    }
}
