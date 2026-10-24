using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>A new session from the window (Ctrl+N, the Board, the empty Sessions page) on the engine (A3 end-to-end pass).</summary>
public sealed class EngineNewSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANewSessionIsListedAsNewUntilItsFirstMessageTitlesIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        using var service = fixture.Sessions();
        Assert.True(service.CanCreate);

        var id = await service.CreateAsync(Ct);
        await service.IdleAsync();

        Assert.StartsWith(EngineSessionService.Surface + "-", id, StringComparison.Ordinal);
        Assert.Contains(await service.ListAsync(Ct), summary => summary is { Title: "New session", State: SessionState.Idle } && summary.Id == id);

        await service.SendAsync(id, "hello there", Ct);
        await service.IdleAsync();

        Assert.Contains(await service.ListAsync(Ct), summary => summary is { Title: "hello there", Turns: 1 } && summary.Id == id);
    }

    [Fact]
    public async Task TheHubListsTheNewSessionAtOnceAndOpensIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        using var service = fixture.Sessions();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        using var hub = new SessionHub(service, new InlineDispatcher(), fixture.Time, shell, bar);
        await hub.StartAsync(null, null, Ct);

        var id = await hub.CreateAsync(Ct);

        Assert.Equal(id, shell.CurrentSession?.Id);
        Assert.Equal("New session", shell.CurrentSession?.Title);
        Assert.Equal(Corvid.App.Core.Area.Sessions, shell.CurrentArea);
        Assert.Equal(id, hub.Current?.SessionId);
        await service.IdleAsync();
    }
}

/// <summary>A job run's session reaches the window's session list when the scheduler finishes it (F13's "open the run").</summary>
public sealed class EngineJobRunSessionTests
{
    [Fact]
    public async Task AFinishedRunsSessionIsRaisedSoTheRailAndTheTrayCanOpenIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))),
            new Dictionary<string, string?> { ["Corvid:Scheduler:Enabled"] = "true", ["Corvid:Tools:Builtin"] = "true" });
        using var service = fixture.Sessions();
        var raised = new TaskCompletionSource<SessionSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SessionChanged += (_, summary) =>
        {
            if (summary.Id.StartsWith("job:", StringComparison.Ordinal))
                raised.TrySetResult(summary);
        };
        // Due in a second (a schedule in the past is refused): the scheduler, woken by the store's change, runs it then.
        var due = DateTimeOffset.Now.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

        var job = fixture.Runtime.Builtin.Jobs.Add(new Corvid.Kernel.Jobs.NewJob("soon", "say hello", due));

        var summary = await raised.Task.WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        Assert.Contains(job.JobId, summary.Id, StringComparison.Ordinal);
        Assert.Equal(SessionState.Job, summary.State);
    }
}
