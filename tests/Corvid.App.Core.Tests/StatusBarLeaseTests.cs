using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class StatusBarLeaseTests
{
    [Fact]
    public void AnAreaThatLetsGoPutsBackWhatItReplacedButNotWhatOthersWrote()
    {
        var bar = new StatusBarViewModel { Engine = "", Trailing = "turn 9 s", Scheduler = "here" };
        var board = new StatusBarLease(bar);

        board.Take();
        board.Engine = "in-process · 2 workspaces";
        board.Trailing = "2 running · 1 waiting";
        bar.Scheduler = "elsewhere";
        board.Release();

        Assert.Equal(("", "turn 9 s", "elsewhere"), (bar.Engine, bar.Trailing, bar.Scheduler));
    }

    [Fact]
    public void ANextAreaThatTakesTheBarBeforeTheLastLetsGoStillPutsBackTheBarFromBeforeBoth()
    {
        var bar = new StatusBarViewModel { Engine = "", Trailing = "turn 9 s" };
        var board = new StatusBarLease(bar);
        var models = new StatusBarLease(bar);

        board.Take();
        board.Engine = "in-process · 2 workspaces";
        board.Trailing = "2 running · 1 waiting";
        // Models hears the navigation first: it takes the bar while the Board still holds it.
        models.Take();
        models.Engine = "models";
        board.Release();
        Assert.Equal("models", bar.Engine);

        models.Release();
        Assert.Equal(("", "turn 9 s"), (bar.Engine, bar.Trailing));
        Assert.False(board.IsHeld);
    }
}
