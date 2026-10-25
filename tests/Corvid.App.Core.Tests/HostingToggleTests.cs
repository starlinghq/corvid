using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>U-8 on the Gateway and Jobs pages: Start hosting asks once, naming what it does; Stop stops at once.</summary>
public sealed class HostingToggleTests
{
    private sealed class FakeHosting : IHostingService
    {
        public string? Unavailable { get; init; }

        public HostingState State { get; private set; } = HostingState.None;

        public List<(HostedPart Part, bool On)> Calls { get; } = [];

        public Task<string> SetAsync(HostedPart part, bool on, CancellationToken cancellationToken)
        {
            Calls.Add((part, on));
            State = part == HostedPart.Gateway ? State with { GatewayOn = on, GatewayHere = on } : State with { SchedulerOn = on, SchedulerHere = on };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(on ? "This app hosts it now." : "Stopped.");
        }

        public event EventHandler? Changed;
    }

    [Fact]
    public void StartAsksOnceNamingWhatItDoesAndOnlyTheAnswerStartsIt()
    {
        var hosting = new FakeHosting();
        var said = new List<string>();
        using var toggle = new HostingToggleViewModel(hosting, HostedPart.Gateway, (_, text) => said.Add(text));
        Assert.True(toggle.ShowStart);
        Assert.Contains("token from the Vault", toggle.Question, StringComparison.Ordinal);

        toggle.AskCommand.Execute(null);

        Assert.True(toggle.IsAsking);
        Assert.False(toggle.ShowStart);
        Assert.Empty(hosting.Calls);
        Assert.True(toggle.Close());
        Assert.False(toggle.IsAsking);

        toggle.AskCommand.Execute(null);
        toggle.StartCommand.Execute(null);

        Assert.Equal([(HostedPart.Gateway, true)], hosting.Calls);
        Assert.False(toggle.IsAsking);
        Assert.True(toggle.ShowStop);
        Assert.Equal(["This app hosts it now."], said);

        toggle.StopCommand.Execute(null);
        Assert.Equal((HostedPart.Gateway, false), hosting.Calls[^1]);
        Assert.True(toggle.ShowStart);
    }

    [Fact]
    public void TheSchedulersQuestionNamesItsToolsAndAWindowThatCannotHostShowsNoControl()
    {
        using var scheduler = new HostingToggleViewModel(new FakeHosting(), HostedPart.Scheduler, (_, _) => { });
        Assert.Contains("each with its tools", scheduler.Question, StringComparison.Ordinal);

        using var demo = new HostingToggleViewModel(new NoHostingService("The demo does not host."), HostedPart.Gateway, (_, _) => { });
        Assert.False(demo.ShowStart);
        Assert.False(demo.ShowStop);
        Assert.False(demo.AskCommand.CanExecute(null));
    }

    [Fact]
    public void TheStatusBarNoticeNamesWhatThisAppHosts()
    {
        Assert.Equal("", HostingState.None.Notice);
        Assert.Equal("gateway", new HostingState(true, true, true, false).Notice);
        Assert.Equal("gateway · scheduler", new HostingState(true, true, true, true).Notice);
    }
}
