using Corvid.App.Core.Composition;
using Corvid.App.Core.Memory;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class DemoMemoryServiceTests
{
    private static DemoMemoryService Service() => new(FrozenTimeProvider.Demo(TimeZoneInfo.Utc));

    [Fact]
    public async Task TheDemoIsMemoryPngsMoment()
    {
        var snapshot = await Service().LoadAsync(TestContext.Current.CancellationToken);

        var fact = Assert.Single(snapshot.StagedFacts);
        Assert.Equal("Mark prefers CI failures reviewed first thing on weekdays", fact.Text);
        Assert.Equal("nightly-tidy", fact.StagedBy);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero), fact.StagedAt);
        Assert.Equal([0.58, 0.41], fact.Nearest.Select(n => n.Score));
        var update = Assert.Single(snapshot.BlockUpdates);
        Assert.Equal(("user", 318, 332, 350), (update.Block, update.TokensNow, update.TokensAfter, update.Budget));
        Assert.Equal(["user", "agent", "persona"], snapshot.Blocks.Select(b => b.Name));
        Assert.Equal([318, 402, 120], snapshot.Blocks.Select(b => b.Tokens));
        Assert.Equal(["f-201", "f-207", "f-196"], snapshot.Facts.Take(3).Select(f => f.Id));
        Assert.True(snapshot.Facts[2].Invalidated);
        Assert.Single(snapshot.PendingLessons);
        Assert.Equal(new MemoryStats(212, 1804, 3), snapshot.Stats);
    }

    [Fact]
    public async Task TheDryRunStagesNothingAndTheImportStagesOnlyWhatPassed()
    {
        var service = Service();
        var token = TestContext.Current.CancellationToken;

        var entries = await service.DryRunImportAsync(token);
        Assert.Equal(5, entries.Count);
        Assert.Equal(2, entries.Count(e => e.Refused));
        Assert.Single((await service.LoadAsync(token)).StagedFacts);

        var staged = await service.ImportForReviewAsync(token);
        Assert.Equal(3, staged.Count);
        Assert.All(staged, s => Assert.Equal("hermes import", s.StagedBy));
        Assert.Equal(4, (await service.LoadAsync(token)).StagedFacts.Count);
        Assert.Empty(await service.ImportForReviewAsync(token));
    }

    [Fact]
    public async Task ABlockUpdateIsCountedAndAConsolidationReplacesTheBlock()
    {
        var service = Service();
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(14, await service.CountTokensAsync("Runs an RTX 3090; Kev and the generator sit on CUDA. Reviews CI failures before anything else.", token));
        var consolidation = await service.ConsolidateAsync("b-14", token);
        var block = await service.ApproveBlockUpdateAsync("b-14", consolidation.Text, replacesBlock: true, token);

        Assert.Equal(consolidation.Tokens, block.Tokens);
        Assert.Equal(15, block.Revision);
        Assert.Equal(consolidation.Text, block.Text);
        Assert.Empty((await service.LoadAsync(token)).BlockUpdates);
    }
}

public sealed class MemoryViewModelTests
{
    private static (MemoryViewModel Memory, DemoMemoryService Service, ShellViewModel Shell, StatusBarViewModel Bar) Create(Area area = Area.Memory)
    {
        var time = ManualTimeProvider.Demo();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell);
        shell.Navigate(area);
        var service = new DemoMemoryService(time);
        return (new MemoryViewModel(service, time, shell, bar, MemoryEngineStatus.Demo), service, shell, bar);
    }

    private static int RailBadge(ShellViewModel shell) => shell.NavItems.Single(item => item.Area == Area.Memory).Badge;

    [Fact]
    public void ThePageDrawsTheMockupsQueueAndPanels()
    {
        var (memory, _, shell, _) = Create();

        var fact = Assert.Single(memory.StagedFacts);
        Assert.Equal("Fact · staged 09:00 by nightly-tidy", fact.Title);
        Assert.Equal("why staged: memory_op 0.62, under its floor 0.80", fact.Why);
        Assert.Equal(["person · Mark", "habit · CI review"], fact.Entities);
        Assert.Equal("valid from 2026-10-24", fact.ValidText);
        Assert.Equal("add", fact.OperationLabel);
        Assert.Equal("evidence: job:nightly-tidy events 41, 58", fact.Evidence);
        Assert.Equal("starlinghq/corvid", Assert.Single(fact.Nearest[0].Spans, s => s.IsCode).Text);
        var update = Assert.Single(memory.BlockUpdates);
        Assert.Equal("Core block update · user · staged 14:02 by cli-9f2e", update.TitleText);
        Assert.Equal("332 of 350 tokens", update.BudgetText);
        Assert.True(update.IsNearBudget);
        Assert.Equal("consolidation is offered at 90%", update.ConsolidationNote);
        var lesson = Assert.Single(memory.LessonCandidates);
        Assert.Equal("Lesson candidate · project corvid", lesson.Title);
        Assert.Equal("evidence: cli-9f2e events 1182 and 1190 · kind: recovery", lesson.Evidence);
        Assert.Equal(["318 / 350", "402 / 550", "120 / 500"], memory.CoreBlocks.Select(b => b.CountText));
        Assert.Equal(["local-only · revision 14 · updated 14:02", "revision 9 · updated yesterday", "revision 2 · set by you"],
            memory.CoreBlocks.Select(b => b.Detail));
        Assert.Equal([true, false, false], memory.CoreBlocks.Select(b => b.IsNearBudget));
        Assert.Equal(["from 2026-10-16", "from 2026-10-22", "2026-10-19 to 2026-10-24"], memory.RecentFacts.Select(f => f.ValidText));
        Assert.Equal("Mark works in starlinghq/corvid on an Arch workstation", memory.RecentFacts[0].Text);
        Assert.Equal(["2", "", "", "1", ""], memory.Tabs.Select(t => t.BadgeText));
        Assert.Equal("Import from Hermes", memory.ImportTitle);
        Assert.Equal(2, RailBadge(shell));
    }

    [Fact]
    public void OnTheMemoryPageTheStatusBarIsTheStores()
    {
        var (_, _, _, bar) = Create();

        Assert.Equal("qwen3.5-0.8b · cuda:0 · q0 · 180 ms", bar.Generator);
        Assert.Equal("", bar.Context);
        Assert.Empty(bar.Tags);
        Assert.Equal("idle", bar.Trailing);
        var field = Assert.Single(bar.AreaFields);
        Assert.Equal(("memory", "212 facts · 1,804 episodes · 3 blocks"), (field.Label, field.Value));
    }

    [Fact]
    public void TheStatusBarIsLeftAloneUntilThePageShows()
    {
        var (_, _, shell, bar) = Create(Area.Sessions);

        Assert.Equal("turn 48 s", bar.Trailing);
        shell.Navigate(Area.Memory);
        Assert.Equal("idle", bar.Trailing);
    }

    [Fact]
    public async Task ACorrectedFactApprovedAsAnUpdateReplacesTheNearestFact()
    {
        var (memory, _, shell, bar) = Create();
        var fact = memory.StagedFacts[0];

        fact.CorrectCommand.Execute(null);
        Assert.True(fact.IsCorrecting);
        fact.CorrectedText = "Mark prefers CI failures reviewed first thing on workdays";
        fact.Operations[(int)MemoryOperation.Update].Choose.Execute(null);
        Assert.Equal(MemoryOperation.Update, fact.Operation);
        Assert.True(fact.Nearest[0].IsTarget);
        fact.Nearest[1].SelectCommand.Execute(null);
        Assert.False(fact.Nearest[0].IsTarget);
        Assert.Contains("Mark prefers quiet UI chrome", fact.TargetHint, StringComparison.Ordinal);
        await memory.ApproveFactAsync(fact, TestContext.Current.CancellationToken);

        Assert.Empty(memory.StagedFacts);
        Assert.Equal("Mark prefers CI failures reviewed first thing on workdays", memory.RecentFacts[0].Text);
        Assert.Equal("from 2026-10-24", memory.RecentFacts[0].ValidText);
        Assert.Equal("Mark prefers quiet UI chrome", memory.RecentFacts[1].Text);
        Assert.True(memory.RecentFacts[1].IsInvalidated);
        Assert.Equal("2026-10-02 to 2026-10-24", memory.RecentFacts[1].ValidText);
        Assert.StartsWith("Corrected and written as an update", memory.Outcome, StringComparison.Ordinal);
        Assert.Equal("1", memory.Tabs[0].BadgeText);
        Assert.Equal(1, RailBadge(shell));
        Assert.Equal("213 facts · 1,804 episodes · 3 blocks", bar.AreaFields[0].Value);
    }

    [Fact]
    public void UndoingACorrectionRestoresTheStagedText()
    {
        var (memory, _, _, _) = Create();
        var fact = memory.StagedFacts[0];

        fact.CorrectCommand.Execute(null);
        fact.CorrectedText = "";
        Assert.False(fact.ApproveCommand.CanExecute(null));
        fact.CancelCorrectionCommand.Execute(null);
        Assert.False(fact.IsCorrecting);
        Assert.Equal(fact.Fact.Text, fact.TextToWrite);
        Assert.True(fact.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task RejectingTheBlockUpdateRecordsWhyAndEmptiesTheQueue()
    {
        var (memory, service, shell, _) = Create();
        var token = TestContext.Current.CancellationToken;

        await memory.RejectBlockAsync(memory.BlockUpdates[0], token);
        await memory.RejectFactAsync(memory.StagedFacts[0], token);

        Assert.Equal(0, memory.ReviewCount);
        // The lesson candidate still waits in the Review list, so the tab and the rail keep counting it.
        Assert.Equal(1, memory.WaitingCount);
        Assert.Equal(1, RailBadge(shell));
        Assert.Equal("1", memory.Tabs[0].BadgeText);
        Assert.Equal(["b-14", "s-41"], service.Rejections.Keys.Order());
        Assert.False(memory.HasNothingToReview);
        await memory.RejectLessonAsync(memory.LessonCandidates[0], "not a rule", token);
        Assert.Equal(0, RailBadge(shell));
        Assert.Equal("", memory.Tabs[0].BadgeText);
        Assert.Equal(318, memory.CoreBlocks[0].Block.Tokens);
    }

    [Fact]
    public async Task ACorrectionRecountsTheBudgetAndConsolidationShrinksTheBlock()
    {
        var (memory, _, _, _) = Create();
        var update = memory.BlockUpdates[0];

        update.CorrectCommand.Execute(null);
        update.EditText = "Runs an RTX 3090.";
        Assert.Equal("321 of 350 tokens", update.BudgetText);
        // The block is past 90% already (318 of 350), so any edit keeps consolidation on offer.
        Assert.True(update.IsNearBudget);
        Assert.True(update.ConsolidateCommand.CanExecute(null));
        update.CancelCommand.Execute(null);
        Assert.Equal("332 of 350 tokens", update.BudgetText);
        Assert.True(update.ConsolidateCommand.CanExecute(null));

        await memory.ConsolidateAsync(update, TestContext.Current.CancellationToken);
        Assert.True(update.IsConsolidated);
        Assert.Equal("274 of 350 tokens", update.BudgetText);
        await memory.ApproveBlockAsync(update, TestContext.Current.CancellationToken);

        Assert.Empty(memory.BlockUpdates);
        Assert.Equal("274 / 350", memory.CoreBlocks[0].CountText);
        Assert.False(memory.CoreBlocks[0].IsNearBudget);
        Assert.StartsWith("Wrote the user block, consolidated", memory.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCoreBlocksTabOffersConsolidationForABlockNearItsBudget()
    {
        var (memory, _, _, _) = Create();
        memory.Select(MemoryTabKind.CoreBlocks);

        var user = memory.CoreBlocks[0];
        Assert.True(user.CanOpenReview);
        Assert.StartsWith("Near its budget: consolidation is offered with the update waiting", user.ConsolidationOffer, StringComparison.Ordinal);
        Assert.Equal("", memory.CoreBlocks[1].ConsolidationOffer);
        user.OpenReview!.Execute(null);
        Assert.Equal(MemoryTabKind.Review, memory.SelectedTab);

        await memory.RejectBlockAsync(memory.BlockUpdates[0], TestContext.Current.CancellationToken);
        Assert.False(memory.CoreBlocks[0].CanOpenReview);
        Assert.EndsWith("with the next staged write to it.", memory.CoreBlocks[0].ConsolidationOffer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALessonApprovedSupersedingMarksTheOldOne()
    {
        var (memory, _, _, _) = Create();
        var lesson = memory.LessonCandidates[0];

        lesson.StartSupersedingCommand.Execute(null);
        Assert.True(lesson.IsSuperseding);
        Assert.False(lesson.ConfirmSupersedeCommand.CanExecute(null));
        lesson.Supersedable[0].ChooseCommand.Execute(null);
        await memory.ApproveLessonAsync(lesson, lesson.Supersedable[0].Lesson.Id, TestContext.Current.CancellationToken);

        Assert.Empty(memory.LessonCandidates);
        Assert.Equal("", memory.Tabs[(int)MemoryTabKind.Lessons].BadgeText);
        Assert.Equal("Run sandbox tests with TMPDIR under the runner's temp.", memory.Lessons[0].Lesson.Rule);
        Assert.True(memory.Lessons[1].IsSuperseded);
        Assert.StartsWith("superseded by", memory.Lessons[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALessonIsRejectedOnlyWithAReason()
    {
        var (memory, service, _, _) = Create();
        var lesson = memory.LessonCandidates[0];

        lesson.StartRejectingCommand.Execute(null);
        Assert.False(lesson.ShowsActions);
        Assert.False(lesson.ConfirmRejectCommand.CanExecute(null));
        lesson.Reason = "the runner's temp is already TMPDIR";
        Assert.True(lesson.ConfirmRejectCommand.CanExecute(null));
        await memory.RejectLessonAsync(lesson, lesson.Reason, TestContext.Current.CancellationToken);

        Assert.Empty(memory.LessonCandidates);
        Assert.Equal("the runner's temp is already TMPDIR", service.Rejections["lc-3"]);
    }

    [Fact]
    public async Task ImportForReviewShowsTheDryRunBeforeStaging()
    {
        var (memory, _, shell, _) = Create();
        memory.Select(MemoryTabKind.Import);

        await memory.ImportForReviewCommand.ExecuteAsync();
        Assert.True(memory.HasPreview);
        Assert.Single(memory.StagedFacts);
        Assert.Equal("Dry run: 3 would be staged, 2 refused. Nothing is staged yet.", memory.PreviewSummary);
        Assert.Equal("Stage 3 for review", memory.StageLabel);

        await memory.StageImportCommand.ExecuteAsync();
        Assert.False(memory.HasPreview);
        Assert.Equal(4, memory.StagedFacts.Count);
        Assert.Equal("Fact · staged 14:05 by hermes import", memory.StagedFacts[1].Title);
        Assert.Equal(MemoryTabKind.Review, memory.SelectedTab);
        Assert.Equal(5, RailBadge(shell));
    }

    [Fact]
    public async Task SearchingFiltersTheFactsTab()
    {
        var (memory, _, _, _) = Create();

        memory.SearchText = "kev";
        await memory.SearchCommand.ExecuteAsync();

        Assert.Equal(MemoryTabKind.Facts, memory.SelectedTab);
        Assert.True(memory.IsFactsTab);
        Assert.Equal(["Kev-4B is not downloaded", "Kev-0.8B is the decider, calibrated on the corvid sessions"], memory.Facts.Select(f => f.Text));
        Assert.Equal("2 facts match “kev”", memory.FactsSummary);
        await memory.ClearSearchCommand.ExecuteAsync();
        Assert.Equal(7, memory.Facts.Count);
    }

    [Fact]
    public void TheCompositionCreatesTheMemoryPageOnTheDemo()
    {
        Assert.True(AppOptions.TryParse(["--page", "memory"], out var options, out var error), error);
        var composition = new AppComposition(options, new InlineDispatcher());

        Assert.Equal(2, composition.Memory.ReviewCount);
        Assert.Equal("idle", composition.StatusBar.Trailing);
        Assert.Same(composition.Memory, composition.Memory);
    }
}
