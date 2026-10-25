using System.Reflection;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class CompositionTests
{
    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(["--demo", .. args], out var options, out var error), error);
        // A manual clock: the demo's running turn would otherwise start real timers that fire on the thread pool.
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("sessions")]
    [InlineData("mcp")]
    public void TheBellReadsTheSameWhicheverPageOpensFirstAndVisitingAnotherChangesNothing(string page)
    {
        using var reference = Compose("--demo", "--page", "board");
        _ = reference.Board;
        var expected = reference.Shell.AttentionCount;

        using var composition = Compose("--demo", "--page", page);
        Assert.Equal(expected, composition.Shell.AttentionCount);
        _ = composition.Mcp;
        _ = composition.Gateway;
        composition.Shell.Navigate(Area.Mcp);
        Assert.Equal(expected, composition.Shell.AttentionCount);
    }

    [Fact]
    public void EveryAreaHasAPropertyNamedAfterItThatGivesItsViewModel()
    {
        using var composition = Compose();
        foreach (var area in Enum.GetValues<Area>())
        {
            var property = typeof(AppComposition).GetProperty(area.ToString(), BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"AppComposition.{area} is missing; each area has a file AppComposition.{area}.cs.");
            var viewModel = Assert.IsAssignableFrom<AreaViewModel>(property!.GetValue(composition));
            Assert.Equal(area, viewModel.Area);
            Assert.Equal(area.Title(), viewModel.Title);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.Summary));
            // Created once, on first use.
            Assert.Same(viewModel, property.GetValue(composition));
        }
    }

    [Fact]
    public void TheCommandLineChoosesThePageTheSessionAndTheTheme()
    {
        using var composition = Compose("--page", "models", "--session", "cli-77b0", "--theme", "dark");

        Assert.Equal(Area.Models, composition.Shell.CurrentArea);
        Assert.Equal("cli-77b0", composition.Shell.CurrentSession?.Id);
        Assert.Equal("cli-77b0", composition.StatusBar.SessionId);
        Assert.Equal(AppTheme.Dark, composition.Shell.Theme);
    }

    [Fact]
    public void AnUnknownSessionLeavesTheDemoSessionOpen()
    {
        using var composition = Compose("--session", "cli-0000");

        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        Assert.Equal(DemoShell.CurrentSessionId, composition.Shell.CurrentSession?.Id);
    }

    [Fact]
    public void TheDemoFreezesTheClockAndAppliesARequestedModeAtOnce()
    {
        using var composition = Compose();

        Assert.Equal(new DateTime(2026, 10, 24, 14, 5, 0), composition.Time.GetLocalNow().DateTime);
        composition.StatusBar.Modes[2].Choose.Execute(null);
        Assert.Equal(ApprovalMode.Permissive, composition.StatusBar.Mode);
    }

    [Fact]
    public void SessionsFollowsTheRailAndOpensRewind()
    {
        using var composition = Compose();
        var sessions = composition.Sessions;

        Assert.Equal("Fix the flaky sandbox test on CI", sessions.SessionTitle);
        Assert.Equal("cli-9f2e · running", sessions.SessionDetail);
        Assert.Equal("cli-9f2e", sessions.Session?.SessionId);
        // The tabs' badges are the session's: three files changed, two tasks open.
        Assert.Equal(["Changes|3", "Files|", "Tasks|2", "Decisions|", "Context|", "Output|", "Session|"],
            sessions.Panels.Select(panel => $"{panel.Label}|{panel.BadgeText}"));

        composition.Shell.FindSession("cli-a410")!.Open.Execute(null);
        Assert.Equal("Draft release notes for v0.2", sessions.SessionTitle);
        Assert.Equal("cli-a410", sessions.Session?.SessionId);
        Assert.Equal(["Changes|", "Files|", "Tasks|", "Decisions|", "Context|", "Output|", "Session|"],
            sessions.Panels.Select(panel => $"{panel.Label}|{panel.BadgeText}"));
        Assert.Equal("Changes", sessions.SelectedPanel.Label);
        sessions.Panels[4].Open.Execute(null);
        Assert.Equal("Context", sessions.SelectedPanel.Label);
        Assert.Equal([false, false, false, false, true, false, false], sessions.Panels.Select(panel => panel.IsSelected));

        sessions.OpenRewindCommand.Execute(null);
        Assert.Equal(Area.Rewind, composition.Shell.CurrentArea);
        Assert.Equal("Draft release notes for v0.2", composition.Rewind.SessionTitle);
        composition.Rewind.BackToSessionCommand.Execute(null);
        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
    }

    [Fact]
    public void SettingsSwitchesTheThemeAtOnce()
    {
        using var composition = Compose("--theme", "light");
        var settings = composition.Settings;

        Assert.Equal(["light", "dark", "system"], settings.Themes.Select(theme => theme.Label));
        Assert.Equal([true, false, false], settings.Themes.Select(theme => theme.IsSelected));

        settings.Themes[1].Choose.Execute(null);

        Assert.Equal(AppTheme.Dark, composition.Shell.Theme);
        Assert.Equal([false, true, false], settings.Themes.Select(theme => theme.IsSelected));
    }
}
