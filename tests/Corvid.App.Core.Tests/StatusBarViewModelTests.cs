using Corvid.App.Core;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class StatusBarViewModelTests
{
    [Fact]
    public void TheDemoStatusBarIsTheMainMockups()
    {
        var bar = new StatusBarViewModel();
        DemoShell.Fill(bar);

        Assert.Equal("claude (escalated) · cuda:0 · q0 · 410 ms", bar.Generator);
        Assert.Equal("Kev-0.8B, calibrated", bar.Decider);
        Assert.Equal("38%", bar.Context);
        Assert.Equal(ApprovalMode.Smart, bar.Mode);
        Assert.Equal(["manual", "smart", "permissive"], bar.Modes.Select(mode => mode.Label));
        Assert.Equal([false, true, false], bar.Modes.Select(mode => mode.IsSelected));
        Assert.Equal(("tainted", Tone.Amber), (bar.Tags.Single().Text, bar.Tags.Single().Tone));
        Assert.Equal("here · 2 jobs", bar.Scheduler);
        Assert.Equal("main", bar.Branch);
        Assert.Equal("turn 48 s", bar.Trailing);
        Assert.Equal("", bar.Engine);
        Assert.Equal("", bar.Gateway);
        Assert.Empty(bar.AreaFields);
    }

    [Fact]
    public void ChoosingAModeAsksAndOnlyTheAnswerChangesIt()
    {
        var bar = new StatusBarViewModel { Mode = ApprovalMode.Smart };
        var requested = new List<ApprovalMode>();
        bar.ModeRequested += (_, mode) => requested.Add(mode);

        bar.Modes[0].Choose.Execute(null);
        bar.RequestModeCommand.Execute(ApprovalMode.Permissive);

        Assert.Equal([ApprovalMode.Manual, ApprovalMode.Permissive], requested);
        Assert.Equal(ApprovalMode.Smart, bar.Mode);

        bar.Mode = ApprovalMode.Manual;
        Assert.Equal([true, false, false], bar.Modes.Select(mode => mode.IsSelected));
        bar.CycleModeCommand.Execute(null);
        Assert.Equal(ApprovalMode.Smart, requested[^1]);
    }

    [Fact]
    public void WithoutASessionThereIsNoModeToChoose()
    {
        var bar = new StatusBarViewModel();
        var requested = 0;
        bar.ModeRequested += (_, _) => requested++;

        bar.Modes[1].Choose.Execute(null);

        Assert.False(bar.HasMode);
        Assert.False(bar.RequestModeCommand.CanExecute(ApprovalMode.Smart));
        Assert.False(bar.CycleModeCommand.CanExecute(null));
        Assert.Equal(0, requested);
        Assert.DoesNotContain(bar.Modes, mode => mode.IsSelected);
    }

    [Fact]
    public void EveryModeHasItsSentence()
    {
        foreach (var mode in Enum.GetValues<ApprovalMode>())
            Assert.StartsWith(mode.ToString() + ":", StatusBarViewModel.Describe(mode), StringComparison.Ordinal);
    }
}
