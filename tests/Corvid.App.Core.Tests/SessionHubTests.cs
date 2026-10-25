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
}
