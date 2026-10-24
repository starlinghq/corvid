using System.ComponentModel;
using Corvid.App.Core;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class ShellViewModelTests
{
    private static ShellViewModel DemoShellViewModel(out StatusBarViewModel bar)
    {
        bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell, FrozenTimeProvider.Demo());
        return shell;
    }

    [Fact]
    public void TheDemoShellIsTheMainMockups()
    {
        var shell = DemoShellViewModel(out var bar);

        Assert.Equal("corvid · main", shell.Workspace);
        Assert.Equal("3", shell.AttentionText);
        Assert.Equal("Attention: 3 items waiting", shell.AttentionName);
        Assert.Equal(["2", "", "3", "1", "", "", "", "", "1", "", "", ""], shell.NavItems.Select(item => item.BadgeText));
        Assert.Equal(["Pinned", "Recent"], shell.SessionGroups.Select(group => group.Title));
        Assert.Equal(
            ["Fix the flaky sandbox test on CI|Running|12 s", "Draft release notes for v0.2|Running|2 m", "Vault policy for ntfy|Idle|1 h", "nightly-tidy run|Job|09:00"],
            shell.SessionGroups.SelectMany(group => group.Sessions).Select(session => $"{session.Title}|{session.State}|{session.When}"));
        Assert.Equal("cli-9f2e", shell.CurrentSession?.Id);
        Assert.True(shell.CurrentSession!.IsSelected);
        Assert.Equal("cli-9f2e", bar.SessionId);
        Assert.Equal(Area.Sessions, shell.CurrentArea);
        Assert.Equal([true, false, false, false, false, false, false, false, false, false, false, false], shell.NavItems.Select(item => item.IsSelected));
    }

    [Fact]
    public void NavigatingMovesTheRailSelectionAndSaysSo()
    {
        var shell = DemoShellViewModel(out _);
        var raised = new List<string?>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.RailItem(Area.Models).Open.Execute(null);

        Assert.Equal(Area.Models, shell.CurrentArea);
        Assert.Equal("Models", shell.CurrentTitle);
        Assert.Contains(nameof(ShellViewModel.CurrentArea), raised);
        Assert.Equal([Area.Models], shell.NavItems.Where(item => item.IsSelected).Select(item => item.Area));

        shell.Navigate(Area.Rewind);
        Assert.Equal([Area.Sessions], shell.NavItems.Where(item => item.IsSelected).Select(item => item.Area));

        shell.OpenSettingsCommand.Execute(null);
        Assert.Equal(Area.Settings, shell.CurrentArea);
        Assert.DoesNotContain(shell.NavItems, item => item.IsSelected);

        shell.OpenDoctorCommand.Execute(null);
        Assert.Equal(Area.FirstRun, shell.CurrentArea);

        shell.OpenAttentionCommand.Execute(null);
        Assert.Equal(Area.Board, shell.CurrentArea);
    }

    [Fact]
    public void OpeningASessionSelectsItShowsSessionsAndNamesItInTheStatusBar()
    {
        var shell = DemoShellViewModel(out var bar);
        shell.Navigate(Area.Audit);
        var vault = shell.FindSession("cli-77b0")!;

        vault.Open.Execute(null);

        Assert.Equal(Area.Sessions, shell.CurrentArea);
        Assert.Same(vault, shell.CurrentSession);
        Assert.True(vault.IsSelected);
        Assert.False(shell.FindSession("cli-9f2e")!.IsSelected);
        Assert.Equal("cli-77b0", bar.SessionId);
        Assert.Equal("Vault policy for ntfy", shell.PhoneTitle);
        Assert.Equal("Vault policy for ntfy, idle, 1 h", vault.AutomationName);
        Assert.False(shell.OpenSession("cli-0000"));
        Assert.Same(vault, shell.CurrentSession);
    }

    [Fact]
    public void ARefreshedSessionListKeepsTheOpenSessionSelectedById()
    {
        var shell = DemoShellViewModel(out _);

        shell.SetSessions([new SessionGroup("Recent", [shell.CreateSession("cli-9f2e", "Fix the flaky sandbox test on CI", SessionState.Waiting, "1 m")])]);

        Assert.Equal("cli-9f2e", shell.CurrentSession?.Id);
        Assert.True(shell.CurrentSession!.IsSelected);
        Assert.Equal("waiting on you", shell.CurrentSession.StateLabel);

        shell.SetSessions([]);
        Assert.Null(shell.CurrentSession);
    }

    [Theory]
    [InlineData(1440, ShellLayout.Wide)]
    [InlineData(1180, ShellLayout.Wide)]
    [InlineData(1179, ShellLayout.Medium)]
    [InlineData(1024, ShellLayout.Medium)]
    [InlineData(860, ShellLayout.Medium)]
    [InlineData(859, ShellLayout.Narrow)]
    [InlineData(820, ShellLayout.Narrow)]
    [InlineData(720, ShellLayout.Narrow)]
    [InlineData(719, ShellLayout.Phone)]
    [InlineData(390, ShellLayout.Phone)]
    public void TheBreakpointsAreTheMockups(double width, ShellLayout layout) => Assert.Equal(layout, ShellLayouts.ForWidth(width));

    [Theory]
    // layout, page has a dock: rail, rail overlay, dock, dock overlay, status bar, phone bars, rail toggle, dock toggle
    [InlineData(ShellLayout.Wide, true, "rail dock statusbar")]
    [InlineData(ShellLayout.Wide, false, "rail statusbar")]
    [InlineData(ShellLayout.Medium, true, "rail statusbar dock-toggle")]
    [InlineData(ShellLayout.Narrow, true, "statusbar rail-toggle dock-toggle")]
    [InlineData(ShellLayout.Narrow, false, "statusbar rail-toggle")]
    [InlineData(ShellLayout.Phone, true, "phone")]
    public void EachWidthShowsWhatTheMockupsShow(ShellLayout layout, bool hasDock, string expected)
    {
        var shell = new ShellViewModel(new StatusBarViewModel()) { Layout = layout, HasDock = hasDock };

        Assert.Equal(expected, Shown(shell));
    }

    [Fact]
    public void TheTogglesBringBackWhatTheWidthHid()
    {
        var shell = new ShellViewModel(new StatusBarViewModel()) { HasDock = true };

        shell.ToggleDockCommand.Execute(null);
        Assert.Equal("rail statusbar dock-toggle", Shown(shell));
        shell.ToggleDockCommand.Execute(null);
        Assert.Equal("rail dock statusbar", Shown(shell));
        shell.ToggleRailCommand.Execute(null);
        Assert.Equal("dock statusbar rail-toggle", Shown(shell));
        shell.ToggleRailCommand.Execute(null);

        shell.Layout = ShellLayout.Medium;
        shell.ToggleDockCommand.Execute(null);
        // The toggle stays: it closes the overlay again.
        Assert.Equal("rail dock-overlay scrim statusbar dock-toggle", Shown(shell));
        shell.CloseOverlaysCommand.Execute(null);
        Assert.Equal("rail statusbar dock-toggle", Shown(shell));

        shell.Layout = ShellLayout.Narrow;
        shell.ToggleRailCommand.Execute(null);
        Assert.Equal("rail-overlay scrim statusbar rail-toggle dock-toggle", Shown(shell));
        // Following a link in the overlay closes it, as a drawer does.
        shell.Navigate(Area.Models);
        Assert.Equal("statusbar rail-toggle dock-toggle", Shown(shell));

        // An overlay belongs to the width it opened at.
        shell.ToggleDockCommand.Execute(null);
        shell.Layout = ShellLayout.Wide;
        shell.Layout = ShellLayout.Narrow;
        Assert.Equal("statusbar rail-toggle dock-toggle", Shown(shell));
    }

    [Fact]
    public void ThePhoneBarCountsAndFollowsTheArea()
    {
        var shell = DemoShellViewModel(out _);
        shell.Layout = ShellLayout.Phone;

        Assert.Equal(["Chat", "Changes|3", "Attention|3", "More"], shell.PhoneTabs.Select(tab => tab.CountText.Length > 0 ? $"{tab.Label}|{tab.CountText}" : tab.Label));
        Assert.Equal("Chat", Selected(shell));
        shell.PhoneTabs[1].Open.Execute(null);
        Assert.Equal((Area.Review, "Changes"), (shell.CurrentArea, Selected(shell)));
        shell.PhoneTabs[2].Open.Execute(null);
        Assert.Equal((Area.Board, "Attention"), (shell.CurrentArea, Selected(shell)));
        shell.Navigate(Area.Models);
        Assert.Equal("More", Selected(shell));
        shell.PhoneTabs[3].Open.Execute(null);
        Assert.True(shell.ShowRailOverlay);
        shell.Navigate(Area.Rewind);
        Assert.Equal("Chat", Selected(shell));

        shell.SetBadge(Area.Review, 5);
        Assert.Equal("5", shell.PhoneTabs[1].CountText);
        Assert.Throws<ArgumentOutOfRangeException>(() => shell.SetBadge(Area.Settings, 1));
    }

    [Fact]
    public void TheThemeIsAShellSetting()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        var changed = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.SetThemeCommand.Execute(AppTheme.Dark);

        Assert.Equal(AppTheme.Dark, shell.Theme);
        Assert.Equal([nameof(ShellViewModel.Theme)], changed);
    }

    private static string Selected(ShellViewModel shell) => shell.PhoneTabs.Single(tab => tab.IsSelected).Label;

    private static string Shown(ShellViewModel shell) => string.Join(' ', new (bool Shown, string Name)[]
    {
        (shell.ShowRail, "rail"), (shell.ShowRailOverlay, "rail-overlay"), (shell.ShowDock, "dock"), (shell.ShowDockOverlay, "dock-overlay"),
        (shell.ShowScrim, "scrim"), (shell.ShowStatusBar, "statusbar"), (shell.ShowPhoneBars, "phone"), (shell.ShowRailToggle, "rail-toggle"),
        (shell.ShowDockToggle, "dock-toggle"),
    }.Where(item => item.Shown).Select(item => item.Name));
}
