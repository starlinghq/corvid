using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class ModelsTests
{
    private static (DemoModelService Service, ManualTimeProvider Time) Demo(DemoModelScene scene = DemoModelScene.Main)
    {
        var time = ManualTimeProvider.Demo();
        return (new DemoModelService(new InlineDispatcher(), time, scene), time);
    }

    private static ModelsViewModel ViewModel(IModelService service, ShellViewModel? shell = null, StatusBarViewModel? bar = null)
    {
        bar ??= new StatusBarViewModel();
        shell ??= new ShellViewModel(bar);
        return new ModelsViewModel(service, shell, bar);
    }

    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    [Fact]
    public void TheMainSceneDrawsModelsPngsSlots()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        Assert.Equal(ModelSlotNames.All, models.Slots.Select(slot => slot.Name));
        var generator = models.Slots[0];
        Assert.Equal("qwen3.5-0.8b", generator.Model);
        Assert.Equal("default", generator.CurrentNote);
        Assert.Equal(["qwen-27b", "claude"], generator.Ladder.Select(step => step.Model));
        Assert.True(generator.Ladder[0].ShowArrow);
        Assert.False(generator.Ladder[1].ShowArrow);
        Assert.Equal("a hard step escalates up this ladder", generator.Note);
        Assert.Equal("cuda:0 · q0 · 180 ms", generator.Placement);
        Assert.Equal(0.3, generator.Latency, 3);
        Assert.True(generator.CanSwitch);
        Assert.True(generator.CanPin);

        var decider = models.Slots[2];
        Assert.Equal("calibrated", decider.TagText);
        Assert.Equal(Tone.Green, decider.TagTone);
        Assert.Equal("second tier for gates below their floor · downloading", decider.Note);
        Assert.True(decider.IsShared);
        Assert.False(decider.CanSwitch);

        Assert.Equal("none", models.Slots[1].Plain);
        Assert.Equal("cpu · 38 ms", models.Slots[3].Placement);
        Assert.Equal("e5", models.Slots[4].Fallback);
        Assert.Equal("memory embeds only with models configured at start", models.Slots[4].Note);
        Assert.Equal("unbound", models.Slots[5].Model);
        Assert.False(models.Slots[5].HasPlacement);
        Assert.Equal("—", models.Slots[5].Placement);
        Assert.Equal("needed for images in prompts (K-8)", models.Slots[6].Plain);
    }

    [Fact]
    public void TheCatalogueShowsKev4bAtTheMockupsMomentAndEachStatesAction()
    {
        var (service, time) = Demo();
        var models = ViewModel(service);

        Assert.Equal(["qwen3.5-0.8b", "kev-0.8b", "kev-4b", "gliner2-base-v1", "embeddinggemma-2", "qwen-27b", "claude", "gemini"],
            models.Catalogue.Select(row => row.Name));
        var kev4b = models.Catalogue[2];
        Assert.True(kev4b.IsDownloading);
        Assert.Equal("42% · 7.1 of 17 GB · 48 MB/s · 3 m left", kev4b.Progress);
        Assert.Equal(0.42, kev4b.Fraction, 3);
        Assert.Equal("Cancel", kev4b.ActionLabel);
        Assert.True(kev4b.ActionIsDanger);
        Assert.Equal(["cached"], models.Catalogue[0].Tags.Select(tag => tag.Text));
        Assert.Equal(["reachable"], models.Catalogue[5].Tags.Select(tag => tag.Text));
        Assert.Equal("Check", models.Catalogue[5].ActionLabel);
        Assert.Equal(["redact", "key bound"], models.Catalogue[6].Tags.Select(tag => tag.Text));
        Assert.Equal("Egress", models.Catalogue[6].ActionLabel);
        Assert.Equal(["unavailable"], models.Catalogue[7].Tags.Select(tag => tag.Text));
        Assert.Equal("Set the secret…", models.Catalogue[7].ActionLabel);
        Assert.Contains(models.Catalogue[6].Where, run => run.Code && run.Text == "anthropic.key");

        // The scene's own download stands still, so every screenshot of it is the same.
        time.Advance(60);
        Assert.Equal("42% · 7.1 of 17 GB · 48 MB/s · 3 m left", kev4b.Progress);
    }

    [Fact]
    public void TheRightColumnsPanelsSayWhatTheMockupSays()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        Assert.Equal("cloud: redact", models.PrivacyTag);
        Assert.Equal("Requests to cloud models are redacted before they leave: PII and any credential value. The extractor is bound, so cloud slots are available.",
            string.Concat(models.PrivacySummary.Select(run => run.Text)));
        Assert.Equal("today: 7 cloud requests · 11 values redacted · 0 refused", models.PrivacyFacts[0].AutomationName);
        Assert.Equal("Kev-0.8B, calibrated", models.DeciderTag);
        Assert.Equal(["floors", "second tier", "fallback", "profile"], models.DeciderFacts.Select(fact => fact.Key));
        Assert.Equal(["cuda:0", "kev-0.8b", "qwen3.5-0.8b", "cpu"], models.PlacementFacts.Select(fact => fact.Key));
        Assert.Equal("kev-0.8b: device cuda · fallback cpu, with a notice", models.PlacementFacts[1].AutomationName);
        Assert.False(models.HasMissing);
        Assert.Equal("config ~/.config/corvid/corvid.json · validated · a slot change is written there and applies to new sessions",
            string.Concat(models.Subtitle.Select(run => run.Text)));
    }

    [Fact]
    public void MovingALadderStepWritesTheNewOrder()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);
        var generator = models.Slots[0];

        // F14: claude above qwen-27b.
        generator.Ladder[1].MoveEarlierCommand.Execute(null);

        Assert.Equal(["claude", "qwen-27b"], generator.Ladder.Select(step => step.Model));
        Assert.Equal(["claude", "qwen-27b"], service.Snapshot.Slot(ModelSlotNames.Generator)!.Escalation);
        Assert.Equal(Tone.Green, models.NoticeTone);
        Assert.Contains("goes to claude, then qwen-27b", string.Concat(models.Notice.Select(run => run.Text)), StringComparison.Ordinal);
        Assert.False(generator.Ladder[0].MoveEarlierCommand.CanExecute(null));
        Assert.True(generator.Ladder[0].MoveLaterCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheValidatorRefusesALadderItCannotWrite()
    {
        var (service, _) = Demo();
        var token = TestContext.Current.CancellationToken;

        var gemini = await service.SetLadderAsync(ModelSlotNames.Generator, ["gemini"], token);
        Assert.False(gemini.Written);
        Assert.Contains("gemini.key", gemini.Message, StringComparison.Ordinal);
        Assert.False((await service.SetLadderAsync(ModelSlotNames.Generator, ["claude", "claude"], token)).Written);
        Assert.False((await service.SetLadderAsync(ModelSlotNames.Generator, ["qwen3.5-0.8b"], token)).Written);
        Assert.False((await service.SetLadderAsync(ModelSlotNames.Generator, ["kev-0.8b"], token)).Written);
        Assert.False((await service.SetLadderAsync(ModelSlotNames.Extractor, [], token)).Written);
        Assert.Equal(["qwen-27b", "claude"], service.Snapshot.Slot(ModelSlotNames.Generator)!.Escalation);
    }

    [Fact]
    public void SwitchingASlotOffersWhatCanServeIt()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        models.SwitchGeneratorCommand.Execute(null);

        Assert.True(models.IsSwitchOpen);
        Assert.Equal("Switch the generator", models.SwitchTitle);
        Assert.Equal(["qwen3.5-0.8b", "qwen-27b", "claude", "gemini"], models.SwitchChoices.Select(choice => choice.Model));
        Assert.True(models.SwitchChoices[0].IsCurrent);
        Assert.False(models.SwitchChoices[3].Choose.CanExecute(null));
        Assert.Equal("set gemini.key in Vault", models.SwitchChoices[3].Reason);

        models.SwitchChoices[1].Choose.Execute(null);

        Assert.False(models.IsPickerOpen);
        Assert.Equal("qwen-27b", models.Slots[0].Model);
        Assert.Equal(["claude"], models.Slots[0].Ladder.Select(step => step.Model));
        Assert.Equal("", models.Slots[0].CurrentNote);
        Assert.Equal(Tone.Green, models.NoticeTone);
    }

    [Fact]
    public void PinningAndUnpinningTheGenerator()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        models.Slots[0].PinCommand.Execute(null);
        Assert.True(models.Slots[0].IsPinned);
        Assert.Equal("Unpin", models.Slots[0].PinLabel);
        Assert.Equal("default · pinned", models.Slots[0].CurrentNote);

        models.Slots[0].PinCommand.Execute(null);
        Assert.False(models.Slots[0].IsPinned);
        Assert.False(models.Slots[1].PinCommand.CanExecute(null));
    }

    [Fact]
    public void CancellingKev4bLeavesTheCacheConsistentAndADownloadAgainRunsToTheEnd()
    {
        var (service, time) = Demo();
        var models = ViewModel(service);
        var kev4b = models.Catalogue[2];

        kev4b.ActionCommand.Execute(null);

        Assert.Equal(ModelState.Missing, service.Snapshot.Find("kev-4b")!.State);
        Assert.Null(service.Snapshot.Find("kev-4b")!.Download);
        Assert.False(kev4b.IsDownloading);
        Assert.Equal(["missing"], kev4b.Tags.Select(tag => tag.Text));
        Assert.Equal("Download", kev4b.ActionLabel);
        Assert.Contains("partial file was removed", string.Concat(models.Notice.Select(run => run.Text)), StringComparison.Ordinal);
        Assert.Equal("second tier for gates below their floor · not downloaded", models.Slots[2].Note);

        kev4b.ActionCommand.Execute(null);
        Assert.Equal(ModelState.Downloading, service.Snapshot.Find("kev-4b")!.State);
        Assert.Equal(0, service.Snapshot.Find("kev-4b")!.Download!.Bytes);

        time.Advance(10);
        Assert.Equal(480_000_000, service.Snapshot.Find("kev-4b")!.Download!.Bytes);
        Assert.StartsWith("2% · 0.5 of 17 GB · 48 MB/s", kev4b.Progress, StringComparison.Ordinal);

        time.Advance(400);
        Assert.Equal(ModelState.Cached, service.Snapshot.Find("kev-4b")!.State);
        Assert.Equal("second tier for gates below their floor", models.Slots[2].Note);
        Assert.Equal("kev-4b for gates below their floor", models.DeciderFacts[1].Value[0].Text);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public void DownloadsRunOneAtATimeAndTheRestWait()
    {
        var (service, time) = Demo(DemoModelScene.FirstRun);
        var token = TestContext.Current.CancellationToken;

        _ = service.DownloadAsync(["kev-0.8b", "embeddinggemma-2"], token);

        Assert.Equal(ModelState.Downloading, service.Snapshot.Find("kev-0.8b")!.State);
        Assert.Equal(ModelState.Queued, service.Snapshot.Find("embeddinggemma-2")!.State);
        Assert.Equal("downloads", ModelsViewModel.StatusDownloads(service.Snapshot)[0].Label);
        Assert.Equal("kev-0.8b 0% · 1 queued", ModelsViewModel.StatusDownloads(service.Snapshot)[0].Value);

        time.Advance(2.1e9 / DemoModelService.Rate + 0.25);
        Assert.Equal(ModelState.Cached, service.Snapshot.Find("kev-0.8b")!.State);
        Assert.Equal(DeciderTier.Calibrated, service.Snapshot.Decider.Tier);
        Assert.Equal(ModelState.Downloading, service.Snapshot.Find("embeddinggemma-2")!.State);

        time.Advance(60);
        Assert.Equal(ModelState.Cached, service.Snapshot.Find("embeddinggemma-2")!.State);
        Assert.Empty(ModelsViewModel.StatusDownloads(service.Snapshot));
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public void MissingModelsShowTheBannerAndItsDownloadClearsIt()
    {
        var (service, time) = Demo(DemoModelScene.FirstRun);
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        var models = ViewModel(service, shell, bar);

        Assert.True(models.HasMissing);
        Assert.Equal("2 models are missing: kev-0.8b (decider) and embeddinggemma-2 (embedder.text). Decisions run on the degraded tier and every chat says so until kev-0.8b is downloaded.",
            string.Concat(models.MissingText.Select(run => run.Text)));
        Assert.Equal("Download 3.3 GB", models.MissingButtonText);
        Assert.Equal(2, shell.RailItem(Area.Models).Badge);
        Assert.Equal("degraded", models.Slots[2].TagText);
        Assert.Equal(Tone.Amber, models.DeciderTone);
        // Without Vault the cloud step is hidden, with the reason.
        Assert.Empty(models.Slots[0].Ladder);
        Assert.Equal("none · claude hidden: no Vault, so no key", models.Slots[0].Plain);

        models.DownloadMissingCommand.Execute(null);
        Assert.False(models.HasMissing);
        time.Advance(120);

        Assert.Equal(0, shell.RailItem(Area.Models).Badge);
        Assert.Equal("calibrated", models.Slots[2].TagText);
        Assert.Equal("Kev-0.8B, calibrated", models.DeciderTag);
    }

    [Fact]
    public void EditingPlacementShowsTheDriverCheckBesideCuda()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        models.EditPlacementCommand.Execute(null);
        var kev = models.PlacementRows[0];
        Assert.Equal("kev-0.8b", kev.Model);
        Assert.True(kev.ShowChecks);
        Assert.Equal("driver: ok · NVIDIA 580.65 · CUDA driver API 13.0", kev.Checks[0].AutomationName);

        kev.Choices[1].Choose.Execute(null);
        Assert.Equal("cpu", kev.Device);
        Assert.False(kev.ShowChecks);
        models.SavePlacementCommand.Execute(null);

        Assert.False(models.IsEditingPlacement);
        Assert.Equal("cpu", service.Snapshot.Placements[0].Device);
        Assert.Equal("cpu · q0 · 960 ms", models.Slots[2].Placement);
        Assert.Equal("kev-0.8b: device cpu · fallback cpu, with a notice", models.PlacementFacts[1].AutomationName);

        // F14: back on CUDA, with the driver check beside it.
        models.EditPlacementCommand.Execute(null);
        models.PlacementRows[0].Device = "cuda";
        Assert.True(models.PlacementRows[0].ShowChecks);
        models.SavePlacementCommand.Execute(null);
        Assert.Equal("cuda:0 · q0 · 240 ms", models.Slots[2].Placement);
    }

    [Fact]
    public void AddingALocalServerChecksItFirst()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        models.AddServerPickerCommand.Execute(null);
        Assert.True(models.IsAddServerOpen);
        Assert.False(models.AddServerCommand.CanExecute(null));
        models.ServerName = "qwen-coder";
        models.ServerUrl = "ftp://127.0.0.1/v1";
        models.AddServerCommand.Execute(null);
        Assert.Equal(Tone.Red, models.NoticeTone);
        Assert.True(models.IsAddServerOpen);

        models.ServerUrl = "http://127.0.0.1:11434/v1";
        models.AddServerCommand.Execute(null);

        Assert.False(models.IsPickerOpen);
        Assert.Equal("qwen-coder", models.Catalogue[^1].Name);
        Assert.Equal(["reachable"], models.Catalogue[^1].Tags.Select(tag => tag.Text));
    }

    [Fact]
    public void TheDownloadPickerTotalsWhatIsPicked()
    {
        var (service, _) = Demo(DemoModelScene.FirstRun);
        var selection = new DownloadSelectionViewModel(service);

        Assert.Equal(["qwen3.5-0.8b", "gliner2-base-v1", "embeddinggemma-2", "kev-0.8b", "kev-4b"], selection.Items.Select(item => item.Name));
        Assert.Equal("3.3 GB · about 2 minutes at 48 MB/s", string.Concat(selection.SelectedRuns.Select(run => run.Text)));
        Assert.True(selection.Items[0].IsSelected);
        Assert.False(selection.Items[0].IsEnabled);
        Assert.Equal("cached", selection.Items[0].StateTag);
        Assert.Equal("kev-0.8b · decider · 2.1 GB missing · fits cuda:0", string.Concat(selection.Items[3].Text.Select(run => run.Text)));
        Assert.Contains(selection.Items[3].Text, run => run.Muted && run.Text == "missing");
        Assert.Equal("kev-4b · second decider tier · 17 GB · needs 18 GB RAM optional", string.Concat(selection.Items[4].Text.Select(run => run.Text)));
        Assert.False(selection.Items[4].IsSelected);

        selection.Items[4].IsSelected = true;
        Assert.Equal("20 GB · about 8 minutes at 48 MB/s", string.Concat(selection.SelectedRuns.Select(run => run.Text)));
        selection.Items[0].IsSelected = false;
        Assert.True(selection.Items[0].IsSelected);
    }

    [Fact]
    public void WhileModelsShowsTheStatusBarIsTheMockupsAndTheSessionsComesBack()
    {
        using var composition = Compose("--page", "models");
        var bar = composition.StatusBar;

        _ = composition.Models;

        Assert.Equal("qwen3.5-0.8b · cuda:0 · q0 · 180 ms", bar.Generator);
        Assert.Equal("Kev-0.8B, calibrated", bar.Decider);
        Assert.Equal("", bar.Context);
        Assert.Equal([new StatusField("downloads", "kev-4b 42%")], bar.AreaFields);
        Assert.Equal(ApprovalMode.Smart, bar.Mode);
        Assert.Empty(bar.Tags);
        Assert.Equal("here · 2 jobs", bar.Scheduler);
        Assert.Equal("main", bar.Branch);
        Assert.Equal("", bar.SessionId);
        Assert.Equal("idle", bar.Trailing);

        composition.Shell.Navigate(Area.Sessions);

        Assert.Equal("cli-9f2e", bar.SessionId);
        Assert.Empty(bar.AreaFields);
        Assert.Equal("38%", bar.Context);
        Assert.Equal("tainted", Assert.Single(bar.Tags).Text);

        composition.Shell.Navigate(Area.Models);
        Assert.Equal("idle", bar.Trailing);
        Assert.Equal("", bar.SessionId);
    }

    [Fact]
    public void TheCompositionSharesOneModelStoreAndPicksTheSceneFromThePage()
    {
        using var main = Compose("--page", "models");
        Assert.Same(main.ModelService, main.ModelService);
        Assert.False(main.Models.HasMissing);
        Assert.Equal(main.ModelService.Snapshot.Catalogue.Count(model => model.Kind == ModelKind.Local), main.FirstRun.Downloads.Items.Count);
        Assert.Equal(ModelState.Cached, main.ModelService.Snapshot.Find("kev-0.8b")!.State);

        using var firstRun = Compose("--page", "first-run");
        Assert.Equal(ModelState.Missing, firstRun.ModelService.Snapshot.Find("kev-0.8b")!.State);
        Assert.True(firstRun.Models.HasMissing);
    }

    [Fact]
    public void AModelNamesHiddenCharactersShowWhereItIsDownloadedAndGoElsewhere()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        models.ServerName = "evil‮gpj.exe";
        models.ServerUrl = "http://127.0.0.1:11434/v1";
        models.AddServerCommand.Execute(null);

        // The catalogue row's one button acts on the model: its name shows the override.
        Assert.Equal("evil⟨U+202E⟩gpj.exe", models.Catalogue[^1].Name);
        Assert.Equal("evil‮gpj.exe", models.Catalogue[^1].Model.Name);
        // The notice only reports it: drawn without the override.
        var notice = string.Concat(models.Notice.Select(run => run.Text));
        Assert.DoesNotContain('‮', notice);
        Assert.Contains("evilgpj.exe", notice, StringComparison.Ordinal);

        var choice = new DownloadChoiceViewModel(
            new CatalogueModel("kev‮-9b", ModelKind.Local, "decider", "decider", "Starling", "1 GB", 1_000_000_000, ModelState.Missing, [ModelSlotNames.Decider]),
            () => { });
        Assert.Equal("kev⟨U+202E⟩-9b", choice.Text[0].Text);
        Assert.True(choice.Text[0].Code);
    }

    [Fact]
    public void MarksInATypedNameAreTextNotStyle()
    {
        var (service, _) = Demo();
        var models = ViewModel(service);

        models.ServerName = "qwen`~**x";
        models.ServerUrl = "http://127.0.0.1:11434/v1";
        models.AddServerCommand.Execute(null);

        Assert.Equal(Tone.Green, models.NoticeTone);
        var named = Assert.Single(models.Notice, run => run.Text.Contains("qwen`~**x", StringComparison.Ordinal));
        Assert.False(named.Code || named.Bold || named.Muted);
        Assert.Equal("qwen`~**x", models.Catalogue[^1].Name);
        Assert.Equal([new TextRun("a`b~c**d\\e")], Spans.Parse(Spans.Escape("a`b~c**d\\e")));
        Assert.Equal([new TextRun("x"), new TextRun("p`q", Code: true)], Spans.Parse($"x`{Spans.Escape("p`q")}`"));
        Assert.Equal("ab", Spans.Plain("a‮b"));
    }

    [Fact]
    public async Task ASlotBackOnALocalModelRunsWhereItRanBefore()
    {
        var (service, _) = Demo();
        var token = TestContext.Current.CancellationToken;
        var before = service.Snapshot.Slot(ModelSlotNames.Generator)!.Placement;

        Assert.True((await service.SwitchAsync(ModelSlotNames.Generator, "qwen-27b", token)).Written);
        Assert.Equal("local server", service.Snapshot.Slot(ModelSlotNames.Generator)!.Placement!.Device);
        Assert.True((await service.SwitchAsync(ModelSlotNames.Generator, "qwen3.5-0.8b", token)).Written);

        Assert.Equal(before, service.Snapshot.Slot(ModelSlotNames.Generator)!.Placement);
        Assert.Equal(new SlotPlacement("cuda:0", 0, 180), before);

        // Moved to the CPU while the slot ran another model: back on it, the slot follows the new placement.
        Assert.True((await service.SwitchAsync(ModelSlotNames.Generator, "qwen-27b", token)).Written);
        Assert.True((await service.SetPlacementAsync("qwen3.5-0.8b", "cpu", token)).Written);
        Assert.True((await service.SwitchAsync(ModelSlotNames.Generator, "qwen3.5-0.8b", token)).Written);
        Assert.Equal("cpu", service.Snapshot.Slot(ModelSlotNames.Generator)!.Placement!.Device);
    }

    [Fact]
    public async Task ALadderKeepsItsHiddenCloudStepWhenItIsReordered()
    {
        var (service, _) = Demo(DemoModelScene.FirstRun);
        var token = TestContext.Current.CancellationToken;
        Assert.True((await service.AddServerAsync("qwen-coder", "http://127.0.0.1:11434/v1", token)).Written);

        // claude has no key without Vault, but it is already on the ladder: moving the new step past it is allowed.
        Assert.True((await service.SetLadderAsync(ModelSlotNames.Generator, ["qwen-coder", "claude"], token)).Written);
        Assert.True((await service.SetLadderAsync(ModelSlotNames.Generator, ["claude", "qwen-coder"], token)).Written);
        Assert.Equal(["claude", "qwen-coder"], service.Snapshot.Slot(ModelSlotNames.Generator)!.Escalation);
        // A cloud step new to the ladder still needs its key.
        Assert.True((await service.SetLadderAsync(ModelSlotNames.Generator, ["qwen-coder"], token)).Written);
        Assert.False((await service.SetLadderAsync(ModelSlotNames.Generator, ["qwen-coder", "claude"], token)).Written);
    }

    [Fact]
    public void AFailedButtonSaysSoInRed()
    {
        var (service, _) = Demo();
        var models = ViewModel(new FailingModelService(service));

        models.ServerName = "qwen-coder";
        models.ServerUrl = "http://127.0.0.1:11434/v1";
        models.AddServerCommand.Execute(null);

        Assert.Equal(Tone.Red, models.NoticeTone);
        Assert.Contains("the store is read-only", string.Concat(models.Notice.Select(run => run.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void LeavingModelsForAnAreaWithNoStatusPutsTheBarBack()
    {
        using var composition = Compose("--page", "sessions");
        var bar = composition.StatusBar;
        var before = (bar.Generator, bar.Decider, bar.Trailing, bar.SessionId, bar.Context);
        _ = composition.Models;

        composition.Shell.Navigate(Area.Models);
        Assert.Equal("idle", bar.Trailing);
        Assert.Single(bar.AreaFields);
        composition.Shell.Navigate(Area.Skills);

        Assert.Equal(before, (bar.Generator, bar.Decider, bar.Trailing, bar.SessionId, bar.Context));
        Assert.Empty(bar.AreaFields);
    }

    [Fact]
    public void OpeningTheEgressLedgerFollowsALinkToWhatLeft()
    {
        using var composition = Compose("--page", "models");

        composition.Models.OpenEgressCommand.Execute(null);

        Assert.Equal(Area.Audit, composition.Shell.CurrentArea);
        Assert.Equal(AreaLinkTarget.Egress, composition.Shell.Link?.Target);
        Assert.Equal(Area.Audit, composition.Shell.Link?.Area);
    }

    /// <summary>The demo store, but adding a server fails as a store that cannot write would.</summary>
    private sealed class FailingModelService(DemoModelService inner) : IModelService
    {
        public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }

        public ModelsSnapshot Snapshot => inner.Snapshot;

        public Task<ModelsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

        public Task<ConfigWrite> SetLadderAsync(string slot, IReadOnlyList<string> ladder, CancellationToken cancellationToken = default) =>
            inner.SetLadderAsync(slot, ladder, cancellationToken);

        public Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default) =>
            inner.SwitchAsync(slot, model, cancellationToken);

        public Task<ConfigWrite> SetPinnedAsync(string slot, bool pinned, CancellationToken cancellationToken = default) =>
            inner.SetPinnedAsync(slot, pinned, cancellationToken);

        public Task<ConfigWrite> SetPlacementAsync(string model, string device, CancellationToken cancellationToken = default) =>
            inner.SetPlacementAsync(model, device, cancellationToken);

        public Task DownloadAsync(IEnumerable<string> models, CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(models, cancellationToken);

        public Task CancelDownloadAsync(string model, CancellationToken cancellationToken = default) =>
            inner.CancelDownloadAsync(model, cancellationToken);

        public Task<ServerCheck> CheckServerAsync(string model, CancellationToken cancellationToken = default) =>
            inner.CheckServerAsync(model, cancellationToken);

        public Task<ConfigWrite> AddServerAsync(string name, string url, CancellationToken cancellationToken = default) =>
            Task.FromException<ConfigWrite>(new IOException("the store is read-only"));
    }

    [Fact]
    public void SpansReadCodeBoldAndMutedMarks()
    {
        var runs = Spans.Parse("no `~/.config/corvid.json` yet **3.3 GB** ~optional~");

        Assert.Equal(
            [
                new TextRun("no "), new TextRun("~/.config/corvid.json", Code: true), new TextRun(" yet "),
                new TextRun("3.3 GB", Bold: true), new TextRun(" "), new TextRun("optional", Muted: true),
            ],
            runs);
        Assert.Equal("a ** b", Spans.Plain("`a ** b`"));
    }

    [Fact]
    public void SizesRatesAndEstimatesPrintAsTheMockupsDo()
    {
        Assert.Equal("1.9 GB", ModelText.Size(1_900_000_000));
        Assert.Equal("17 GB", ModelText.Size(17_000_000_000));
        Assert.Equal("7.1 of 17 GB", ModelText.Of(7_140_000_000, 17_000_000_000));
        Assert.Equal("about 2 minutes at 48 MB/s", ModelText.Estimate(3_300_000_000, 48e6));
        Assert.Equal("40 s left", ModelText.Left(TimeSpan.FromSeconds(39.2)));
        Assert.Equal("cpu · 12 ms", ModelText.Placement(new SlotPlacement("cpu", null, 12)));
    }
}
