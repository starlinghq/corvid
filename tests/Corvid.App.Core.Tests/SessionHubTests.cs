using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>The window's sessions in the rail.</summary>
public sealed class SessionHubTests
{
    [Fact]
    public async Task AnIdleSessionsTimeInTheRailFollowsTheClock()
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        using var sessions = new DemoSessionService(dispatcher, time, DemoScene.Main);
        using var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        await hub.StartAsync(null, null, TestContext.Current.CancellationToken);
        var idle = shell.SessionGroups.SelectMany(group => group.Sessions).Single(session => session.State == SessionState.Idle);
        Assert.Equal("1 h", idle.When);

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal("2 h", idle.When);
    }

    [Fact]
    public async Task SendIsDisabledWithNoSessionOpenAndSendsTheOpenSessionsDraft()
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);

        // No engine, no session: the Send buttons have nothing to send to.
        var empty = new SessionsViewModel(shell);
        Assert.False(empty.HasSession);
        Assert.False(empty.SendCommand.CanExecute(null));

        using var sessions = new DemoSessionService(dispatcher, time, DemoScene.Main);
        using var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        var area = new SessionsViewModel(shell, hub);
        var changes = 0;
        area.SendCommand.CanExecuteChanged += (_, _) => changes++;
        await hub.StartAsync("cli-9f2e", null, TestContext.Current.CancellationToken);

        Assert.True(area.HasSession);
        Assert.True(changes > 0);
        Assert.True(area.SendCommand.CanExecute(null));
        area.Session!.Draft = "also check the hosts";
        area.SendCommand.Execute(null);
        Assert.Equal("", area.Session.Draft);
    }
}
