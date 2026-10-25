using Corvid.App.Core.Composition;
using Corvid.App.Core.Keys;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// The command palette and the surfaces that share it, over the demo composition: the window's keys, fuzzy matching,
/// the switcher, history, find, search, the generator switch, the shortcuts, the surfaces of what comes later, the cap,
/// failures, and text with hidden characters.
/// </summary>
public sealed class PaletteTests
{
    private const string Override = "\u202E";
    private const string Tag = "\U000E0041";

    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(["--demo", .. args], out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    private static PaletteViewModel Standalone(ShellViewModel shell, IPromptHistory? history = null, Func<SessionViewModel?>? session = null) =>
        new(new PaletteHost(shell, new KeyMap(), ManualTimeProvider.DemoLocal(), new InlineDispatcher())
        {
            History = history,
            CurrentSession = session ?? (() => null),
        });

    [Fact]
    public void FuzzyMatchingFindsCharactersInOrderAndRanksWholeWordsFirst()
    {
        Assert.Equal(0, FuzzyMatch.Score("", "anything"));
        Assert.Null(FuzzyMatch.Score("xyz", "Go to Settings"));
        Assert.Null(FuzzyMatch.Score("sg", "Go to Settings".Replace("g", "", StringComparison.Ordinal)));
        Assert.NotNull(FuzzyMatch.Score("gts", "Go to Settings"));
        Assert.True(FuzzyMatch.Score("set", "Settings") > FuzzyMatch.Score("set", "Reset"));
        Assert.True(FuzzyMatch.Score("dock", "Show or hide the dock") > FuzzyMatch.Score("dock", "Done or check"));
    }

    [Fact]
    public void CtrlKListsEveryPageCommandAndSettingAndFiltersAsYouType()
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(KeyCommands.Palette);

        Assert.True(palette.IsOpen);
        Assert.Equal(PaletteMode.Commands, palette.Mode);
        Assert.Contains(palette.Items, item => item.Title == "Go to Settings" && item.KeyText == "Ctrl+,");
        Assert.Contains(palette.Items, item => item.Title == "Switch session…" && item.KeyText == "Ctrl+Tab");
        Assert.Contains(palette.Items, item => item.Title == "Settings: Keybindings");
        Assert.Equal("Pages", palette.Items[0].GroupHeader);
        Assert.Equal(0, palette.SelectedIndex);
        Assert.True(palette.Items[0].IsSelected);

        palette.Query = "keybind";
        Assert.Equal("Settings: Keybindings", palette.Items[0].Title);
        Assert.All(palette.Items, item => Assert.Equal("", item.GroupHeader));
        palette.Accept();
        Assert.False(palette.IsOpen);
        Assert.Equal(Area.Settings, composition.Shell.CurrentArea);
        Assert.True(composition.Settings.IsKeys);

        palette.Execute(KeyCommands.PaletteAlias);
        palette.Query = "zzzzqqq";
        Assert.Empty(palette.Items);
        Assert.True(palette.ShowEmpty);
        Assert.Equal("No command matches.", palette.EmptyText);
    }

    [Fact]
    public void OneRowIsSelectedAsTheQueryNarrowsAndTheSelectionMoves()
    {
        using var composition = Compose();
        var palette = composition.Palette;
        palette.Execute(KeyCommands.Palette);

        foreach (var query in new[] { "s", "se", "sec", "secr" })
        {
            palette.Query = query;
            Assert.Single(palette.Items, item => item.IsSelected);
            Assert.Same(palette.Selected, palette.Items.Single(item => item.IsSelected));
        }

        palette.MoveSelection(1);
        Assert.Single(palette.Items, item => item.IsSelected);
        Assert.Same(palette.Items[palette.SelectedIndex], palette.Items.Single(item => item.IsSelected));

        palette.Query = "sec";
        palette.MoveSelection(1);
        palette.Query = "";
        Assert.Single(palette.Items, item => item.IsSelected);
        Assert.True(palette.Items[0].IsSelected);
    }

    [Fact]
    public void TheWindowsOwnKeysRunWithoutThePalette()
    {
        using var composition = Compose();
        var shell = composition.Shell;
        var palette = composition.Palette;

        palette.Execute(KeyCommands.Settings);
        Assert.Equal(Area.Settings, shell.CurrentArea);
        palette.Execute(KeyCommands.Changes);
        Assert.Equal(Area.Review, shell.CurrentArea);
        var rail = shell.ShowRail;
        palette.Execute(KeyCommands.ToggleRail);
        Assert.NotEqual(rail, shell.ShowRail);
        Assert.False(palette.IsOpen);
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.Execute("no.such.command"));
    }

    [Fact]
    public void CtrlTabLandsOnThePreviousSessionAndAgainOnTheNext()
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(KeyCommands.SessionSwitcher);

        Assert.Equal(PaletteMode.Sessions, palette.Mode);
        Assert.EndsWith("· open", palette.Items[0].Detail, StringComparison.Ordinal);
        Assert.Equal(1, palette.SelectedIndex);
        palette.Execute(KeyCommands.SessionSwitcher);
        Assert.Equal(2, palette.SelectedIndex);
        palette.MoveSelection(-1);
        Assert.Equal(1, palette.SelectedIndex);
        var chosen = palette.Selected!.Keywords;

        palette.Accept();

        Assert.False(palette.IsOpen);
        Assert.Equal(chosen, composition.Shell.CurrentSession?.Id);
        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
    }

    [Fact]
    public void TheSwitcherCleansHiddenCharactersFromSessionTitles()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        shell.SetSessions([new SessionGroup("Recent", [shell.CreateSession("cli-1", $"evil{Override} title{Tag}", SessionState.Idle, "1 m")])]);
        var palette = Standalone(shell);

        palette.Execute(KeyCommands.SessionSwitcher);

        Assert.Equal("evil title", Assert.Single(palette.Items).Title);
    }

    [Fact]
    public void CtrlRPutsAPromptFromTheHistoryIntoTheComposer()
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(KeyCommands.History);

        Assert.Equal(PaletteMode.History, palette.Mode);
        Assert.Equal(6, palette.Items.Count);
        Assert.Equal("cli-c3d2 · 14:04", palette.Items[0].Detail);
        Assert.Equal("cli-9f2e · 23 Oct 17:12", palette.Items[^1].Detail);
        palette.Query = "retry";
        Assert.Equal("also check the retry count", Assert.Single(palette.Items).Title);

        palette.Accept();

        Assert.False(palette.IsOpen);
        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        Assert.Equal("also check the retry count", composition.Sessions.Session!.Draft);
    }

    [Fact]
    public void APromptWithHiddenCharactersIsShownCleanAndNeverPutBack()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        using var composition = Compose();
        var session = composition.Sessions.Session!;
        var palette = Standalone(shell, new ScriptedHistory([new($"rm -rf build{Override}/ ~{Tag}", "cli-9f2e", DateTimeOffset.UnixEpoch)]), () => session);

        palette.Execute(KeyCommands.History);
        var item = Assert.Single(palette.Items);

        Assert.Equal("rm -rf build/ ~", item.Title);
        Assert.False(item.IsEnabled);
        Assert.Contains("hidden characters", item.Note, StringComparison.Ordinal);
        palette.Choose(item);
        Assert.True(palette.IsOpen);
        Assert.Equal(Tone.Amber, palette.NoteTone);
        Assert.Equal("", session.Draft);
    }

    [Fact]
    public void AHistoryThatFailsToLoadSaysSoInRed()
    {
        var palette = Standalone(new ShellViewModel(new StatusBarViewModel()), new ScriptedHistory(failure: new IOException("the log is locked")));

        palette.Execute(KeyCommands.History);

        Assert.True(palette.IsOpen);
        Assert.Equal(Tone.Red, palette.NoteTone);
        Assert.Equal("Could not load: the log is locked", palette.Note);
        Assert.False(palette.IsLoading);
    }

    [Fact]
    public void TheListIsCappedAndSaysHowManyMoreMatched()
    {
        var entries = Enumerable.Range(1, 120).Select(i => new PromptHistoryEntry($"prompt number {i}", "cli-1", DateTimeOffset.UnixEpoch)).ToList();
        var palette = Standalone(new ShellViewModel(new StatusBarViewModel()), new ScriptedHistory(entries));

        palette.Execute(KeyCommands.History);

        Assert.Equal(PaletteViewModel.MaxItems, palette.Items.Count);
        Assert.Equal("50 of 120 · type to narrow", palette.CountText);
        palette.Query = "number 7";
        Assert.Equal("prompt number 7", palette.Items[0].Title);
    }

    [Fact]
    public void CtrlFFindsInTheOpenSessionAndRevealsTheBlock()
    {
        using var composition = Compose("--page", "models");
        var palette = composition.Palette;

        palette.Execute(KeyCommands.Find);
        Assert.Equal("Find in Fix the flaky sandbox test on CI", palette.Title);
        Assert.Empty(palette.Items);
        Assert.Equal("Type to find in Fix the flaky sandbox test on CI.", palette.EmptyText);

        palette.Query = "unshare";
        Assert.NotEmpty(palette.Items);
        var first = palette.Items[0];
        Assert.Contains("unshare", first.Title, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("turn ", first.Detail, StringComparison.Ordinal);

        palette.Accept();

        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        var revealed = composition.Sessions.TakeReveal();
        Assert.Contains(composition.Sessions.Session!.Blocks, block => block.Id == revealed);
        Assert.Null(composition.Sessions.TakeReveal());
    }

    [Fact]
    public void FindWithNoSessionOpenSaysToOpenOne()
    {
        var palette = Standalone(new ShellViewModel(new StatusBarViewModel()));
        palette.Execute(KeyCommands.Find);
        Assert.True(palette.IsOpen);
        Assert.Contains("Open a session first", palette.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlShiftFSearchesTheSessionsAndSaysWhatComesWithTheIndex()
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(KeyCommands.Search);
        Assert.Contains("G-5", palette.Note, StringComparison.Ordinal);
        palette.Query = "release";
        var hit = Assert.Single(palette.Items);
        Assert.Equal("Draft release notes for v0.2", hit.Title);

        palette.Accept();

        Assert.Equal("cli-a410", composition.Shell.CurrentSession?.Id);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public async Task AltPSwitchesTheGeneratorFromThePalette()
    {
        using var composition = Compose();
        var palette = composition.Palette;
        var models = composition.ModelService;
        await models.LoadAsync(TestContext.Current.CancellationToken);
        var before = models.Snapshot.Slot(ModelSlotNames.Generator)!.Model;

        palette.Execute(KeyCommands.SwitchGenerator);

        Assert.Equal(PaletteMode.Generator, palette.Mode);
        Assert.Contains(palette.Items, item => item.Title == before && item.Detail.EndsWith("in use", StringComparison.Ordinal) && !item.IsEnabled);
        // A5: two groups, "This session" first, then "Default for new sessions": separate choices.
        Assert.Equal([PaletteViewModel.ThisSessionGroup, PaletteViewModel.NewSessionsGroup], palette.Items.Select(item => item.Group).Distinct());
        var target = palette.Items.First(item => item.IsEnabled && item.Group == PaletteViewModel.ThisSessionGroup);
        palette.Choose(target);

        Assert.True(palette.IsOpen);
        Assert.Equal(Tone.Green, palette.NoteTone);
        Assert.Contains("This session's generator is", palette.Note, StringComparison.Ordinal);
        Assert.Equal(target.Title, models.Snapshot.Slot(ModelSlotNames.Generator)!.SessionModel);
        Assert.Equal(before, models.Snapshot.Slot(ModelSlotNames.Generator)!.Model);
        Assert.Contains(palette.Items, item => item.Group == PaletteViewModel.ThisSessionGroup && item.Title == target.Title && item.Detail.EndsWith("in use", StringComparison.Ordinal));

        var other = palette.Items.First(item => item.IsEnabled && item.Group == PaletteViewModel.NewSessionsGroup);
        palette.Choose(other);
        Assert.Contains("New sessions start on", palette.Note, StringComparison.Ordinal);
        Assert.Equal(other.Title, models.Snapshot.Slot(ModelSlotNames.Generator)!.Model);
        Assert.Equal(target.Title, models.Snapshot.Slot(ModelSlotNames.Generator)!.SessionModel);
    }

    [Theory]
    [InlineData(KeyCommands.NewSession, "New session", "come with the engine (A3)")]
    [InlineData(KeyCommands.NewWindow, "New window", "come with pop-out windows")]
    [InlineData(KeyCommands.Aside, "Aside", "comes with the scratch fork (F7)")]
    public void AKeyWhoseFeatureComesLaterOpensItsSurfaceSayingSo(string command, string title, string says)
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(command);

        Assert.True(palette.IsOpen);
        Assert.Equal(PaletteMode.Pending, palette.Mode);
        Assert.False(palette.HasQuery);
        Assert.Equal(title, palette.Title);
        Assert.Contains(says, palette.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", palette.CountText);
        Assert.NotEmpty(palette.Items);
    }

    [Fact]
    public void ForkForksTheOpenSessionAtItsHeadAndOpensTheFork()
    {
        // G-15: Fork… is the session's, at its head (during a running turn, its last completed step).
        using var composition = Compose();
        var palette = composition.Palette;
        var source = composition.Sessions.Session!.SessionId;
        // The aside's surface offers the same fork to ask in, until the scratch fork (F7) comes.
        palette.Execute(KeyCommands.Aside);
        Assert.Contains(palette.Items, item => item.Title == "Fork it and ask there" && item.IsEnabled);
        palette.Close();

        palette.Execute(KeyCommands.Palette);
        palette.Query = "fork";
        var fork = Assert.Single(palette.Items, item => item.Title == "Fork…");
        Assert.Equal("Session", fork.Group);
        palette.Choose(fork);

        Assert.False(palette.IsOpen);
        var opened = composition.Sessions.Session!;
        Assert.NotEqual(source, opened.SessionId);
        Assert.Contains($"fork of {source}", opened.ForkText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAsideWithNoSessionAsksForOne()
    {
        var palette = Standalone(new ShellViewModel(new StatusBarViewModel()));
        palette.Execute(KeyCommands.Aside);
        Assert.Contains("open a session first", palette.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void F1ListsEveryKeyByContextAndRunsTheWindowsOwn()
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(KeyCommands.Help);

        Assert.Equal(composition.Keys.Bindings.Count, palette.Items.Count);
        Assert.Equal("Anywhere", palette.Items[0].GroupHeader);
        Assert.Contains(palette.Items, item => item.GroupHeader == "Composer");
        // A page's own key says where it works rather than doing nothing.
        palette.Choose(palette.Items.First(item => item.KeyText == "Ctrl+Enter"));
        Assert.True(palette.IsOpen);
        Assert.Equal("Ctrl+Enter belongs to Composer: it works while the focus is there.", palette.Note);

        palette.Query = "rail";
        // Filtering keeps the groups' order and headings.
        Assert.Equal("Anywhere", palette.Items[0].GroupHeader);
        var rail = palette.Items.First(item => item.Title == "Shows or hides the rail");
        var shown = composition.Shell.ShowRail;
        palette.Choose(rail);
        Assert.NotEqual(shown, composition.Shell.ShowRail);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public void ARemappedKeyShowsInThePalette()
    {
        using var composition = Compose();
        Assert.True(composition.Keys.Rebind(KeyCommands.History, "Ctrl+Shift+H").Accepted);

        composition.Palette.Execute(KeyCommands.Palette);

        Assert.Equal("Ctrl+Shift+H", composition.Palette.Items.Single(item => item.Title == "Prompt history…").KeyText);
    }

    [Fact]
    public async Task AGeneratorWhoseNameHidesCharactersIsRevealedAndCannotBePicked()
    {
        using var composition = Compose();
        var demo = composition.ModelService;
        await demo.LoadAsync(TestContext.Current.CancellationToken);
        var models = new HiddenNameModels(demo, $"qwen{Override}b72");
        var palette = new PaletteViewModel(new PaletteHost(composition.Shell, new KeyMap(), ManualTimeProvider.DemoLocal(), new InlineDispatcher())
        {
            Models = () => models,
        });

        palette.Execute(KeyCommands.SwitchGenerator);

        var row = Assert.Single(palette.Items, item => item.Title.StartsWith("qwen", StringComparison.Ordinal) && item.Title.Contains("b72", StringComparison.Ordinal));
        Assert.Equal(SafeText.Reveal(models.Hidden), row.Title);
        Assert.NotEqual("qwenb72", row.Title);
        Assert.False(row.IsEnabled);
        Assert.Contains("hidden characters", row.Note, StringComparison.Ordinal);
        var before = demo.Snapshot.Slot(ModelSlotNames.Generator)!.Model;
        palette.Choose(row);
        Assert.Equal(before, demo.Snapshot.Slot(ModelSlotNames.Generator)!.Model);
        Assert.Equal(0, models.Switches);
    }

    [Fact]
    public void ALoadThatEndsAfterANewerSurfaceOpenedLeavesThatSurfacesList()
    {
        var history = new PendingHistory();
        var palette = Standalone(new ShellViewModel(new StatusBarViewModel()), history);

        palette.Execute(KeyCommands.History);
        Assert.True(palette.IsLoading);
        palette.Execute(KeyCommands.Help);
        var help = palette.Items;
        history.Finish([new PromptHistoryEntry("an old prompt", "cli-1", DateTimeOffset.UnixEpoch)]);

        Assert.Equal(PaletteMode.Help, palette.Mode);
        Assert.Same(help, palette.Items);
        Assert.DoesNotContain(palette.Items, item => item.Title == "an old prompt");
        Assert.False(palette.HasNote && palette.NoteTone == Tone.Red);
    }

    [Fact]
    public void ASlashCommandPutsAnUnsentDraftAsideAndNeverDropsOne()
    {
        using var composition = Compose();
        var palette = composition.Palette;
        var session = composition.Sessions.Session!;
        // The demo opens with a draft already put aside (Main.png's "draft saved"): there is no room for another.
        Assert.True(session.HasStashedDraft);
        session.Draft = "half a thought";

        palette.Execute(KeyCommands.Palette);
        palette.Choose(palette.Items.Single(item => item.Title == "/compact…"));

        Assert.True(palette.IsOpen);
        Assert.Equal(Tone.Amber, palette.NoteTone);
        Assert.Contains("Send or clear one of them first", palette.Note, StringComparison.Ordinal);
        Assert.Equal("half a thought", session.Draft);

        session.StashDraft("");
        palette.Choose(palette.Items.Single(item => item.Title == "/compact…"));

        Assert.False(palette.IsOpen);
        Assert.Equal("/compact ", session.Draft);
        Assert.True(session.HasStashedDraft);
        session.Draft = "";
        session.StashDraftCommand.Execute(null);
        Assert.Equal("half a thought", session.Draft);
    }

    [Fact]
    public void TheSelectedRowIsAnnouncedWithItsPlace()
    {
        using var composition = Compose();
        var palette = composition.Palette;

        palette.Execute(KeyCommands.Palette);
        Assert.StartsWith($"{palette.Items[0].AutomationName}, 1 of {palette.Items.Count}", palette.SelectionText, StringComparison.Ordinal);
        palette.MoveSelection(1);
        Assert.Equal($"{palette.Items[1].AutomationName}, 2 of {palette.Items.Count}", palette.SelectionText);
        palette.Close();
        Assert.Equal("", palette.SelectionText);
    }

    [Fact]
    public void TheSwitcherCleansHiddenCharactersFromSessionIds()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        shell.SetSessions([new SessionGroup("Recent", [shell.CreateSession($"cli{Override}-1", "plain", SessionState.Idle, "1 m")])]);
        var palette = Standalone(shell);

        palette.Execute(KeyCommands.SessionSwitcher);

        var row = Assert.Single(palette.Items);
        Assert.StartsWith("cli-1 · ", row.Detail, StringComparison.Ordinal);
        Assert.Equal("cli-1", row.Keywords);
    }

    private sealed class ScriptedHistory(IReadOnlyList<PromptHistoryEntry>? entries = null, Exception? failure = null) : IPromptHistory
    {
        public Task<IReadOnlyList<PromptHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default) =>
            failure is not null ? Task.FromException<IReadOnlyList<PromptHistoryEntry>>(failure) : Task.FromResult(entries ?? []);
    }

    /// <summary>A history that answers only when told, and ignores cancellation, as a slow log might.</summary>
    private sealed class PendingHistory : IPromptHistory
    {
        private readonly TaskCompletionSource<IReadOnlyList<PromptHistoryEntry>> _answer = new();

        public Task<IReadOnlyList<PromptHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default) => _answer.Task;

        public void Finish(IReadOnlyList<PromptHistoryEntry> entries) => _answer.SetResult(entries);
    }

    /// <summary>The demo's models with one more generator whose name holds a hidden character.</summary>
    private sealed class HiddenNameModels(IModelService inner, string hidden) : IModelService
    {
        public string Hidden { get; } = hidden;

        public int Switches { get; private set; }

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public ModelsSnapshot Snapshot => With(inner.Snapshot);

        public async Task<ModelsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => With(await inner.LoadAsync(cancellationToken));

        public Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default)
        {
            Switches++;
            return inner.SwitchAsync(slot, model, cancellationToken);
        }

        public Task<ConfigWrite> SetLadderAsync(string slot, IReadOnlyList<string> ladder, CancellationToken cancellationToken = default) => inner.SetLadderAsync(slot, ladder, cancellationToken);

        public Task<ConfigWrite> SetPinnedAsync(string slot, bool pinned, CancellationToken cancellationToken = default) => inner.SetPinnedAsync(slot, pinned, cancellationToken);

        public Task<ConfigWrite> SetPlacementAsync(string model, string device, CancellationToken cancellationToken = default) => inner.SetPlacementAsync(model, device, cancellationToken);

        public Task DownloadAsync(IEnumerable<string> models, CancellationToken cancellationToken = default) => inner.DownloadAsync(models, cancellationToken);

        public Task CancelDownloadAsync(string model, CancellationToken cancellationToken = default) => inner.CancelDownloadAsync(model, cancellationToken);

        public Task<ServerCheck> CheckServerAsync(string model, CancellationToken cancellationToken = default) => inner.CheckServerAsync(model, cancellationToken);

        public Task<ConfigWrite> AddServerAsync(string name, string url, CancellationToken cancellationToken = default) => inner.AddServerAsync(name, url, cancellationToken);

        private ModelsSnapshot With(ModelsSnapshot snapshot)
        {
            var available = snapshot.Catalogue.First(model => model.CanServe.Contains(ModelSlotNames.Generator) && model.IsAvailable);
            return snapshot with { Catalogue = [.. snapshot.Catalogue, available with { Name = Hidden }] };
        }
    }
}
