using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class FirstRunTests
{
    private sealed record Setup(FirstRunViewModel FirstRun, DemoFirstRunService Service, DemoModelService Models, ShellViewModel Shell,
        StatusBarViewModel Bar, ManualTimeProvider Time);

    private static Setup Fresh()
    {
        var time = ManualTimeProvider.Demo();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        bar.SessionId = "cli-9f2e";
        var models = new DemoModelService(new InlineDispatcher(), time, DemoModelScene.FirstRun);
        var service = new DemoFirstRunService(freshMachine: true);
        shell.Navigate(Area.FirstRun);
        var firstRun = new FirstRunViewModel(service, models, shell, bar, isSetup: true);
        return new Setup(firstRun, service, models, shell, bar, time);
    }

    [Fact]
    public void TheDoctorPageShowsTheMockupsChecksBeforeAnyModelLoads()
    {
        var setup = Fresh();
        var sections = setup.FirstRun.Sections;

        Assert.Equal(["Configuration and data", "Sandbox and secrets", "GPU"], sections.Select(section => section.Title));
        Assert.Equal(["2 ok", "1 warning", "CUDA build · 1 info"], sections.Select(section => section.Summary));
        Assert.Equal(["configuration", "data directory"], sections[0].Rows.Select(row => row.Name));
        Assert.Contains(sections[0].Rows[0].Detail, run => run.Code && run.Text == "~/.config/corvid/corvid.json");

        var vault = sections[1].Rows[2];
        Assert.Equal("Vault socket", vault.Name);
        Assert.Equal("warning", vault.StatusText);
        Assert.Equal(Tone.Amber, vault.StatusTone);
        Assert.True(vault.HasFix);
        Assert.Equal("systemctl --user enable --now corvid-vault", vault.FixCommand);
        Assert.Equal(["ok", "ok", "ok", "ok", "info"], sections[2].Rows.Select(row => row.StatusText));
        Assert.Equal(Tone.Grey, sections[2].Rows[4].StatusTone);
    }

    [Fact]
    public void TheSetupsStatusBarAndRailReplaceTheSessionsAndGoWithIt()
    {
        var setup = Fresh();
        var bar = setup.Bar;

        Assert.True(setup.Shell.PageDrawsRail);
        Assert.False(setup.Shell.ShowRail);
        Assert.False(setup.Shell.ShowRailToggle);
        Assert.Equal("not started · models load after this page", bar.Engine);
        Assert.Equal("", bar.Generator);
        Assert.Equal("degraded until kev-0.8b is downloaded", bar.Decider);
        Assert.Null(bar.Mode);
        Assert.Equal("Vault absent", Assert.Single(bar.Tags).Text);
        Assert.Equal("", bar.Branch);
        Assert.Equal("", bar.SessionId);
        Assert.Equal("step 1 of 6", bar.Trailing);

        setup.FirstRun.SkipSetupCommand.Execute(null);

        Assert.Equal(Area.Sessions, setup.Shell.CurrentArea);
        Assert.False(setup.FirstRun.IsSetup);
        Assert.False(setup.Shell.PageDrawsRail);
        Assert.True(setup.Shell.ShowRail);
        // F1 ends on an empty session, and the bar keeps what the setup found: degraded, what is missing, Vault absent.
        Assert.Null(setup.Shell.CurrentSession);
        Assert.Equal("in-process", bar.Engine);
        Assert.Equal(ApprovalMode.Smart, bar.Mode);
        Assert.Equal("", bar.SessionId);
        Assert.Equal("idle", bar.Trailing);
        Assert.Equal("degraded until kev-0.8b is downloaded", bar.Decider);
        Assert.Equal(["degraded · kev-0.8b, embeddinggemma-2 missing", "Vault absent"], bar.Tags.Select(tag => tag.Text));
        // The app runs with what it has, and the Models badge says how many default models are missing.
        Assert.Equal(2, setup.Shell.RailItem(Area.Models).Badge);
        Assert.Equal(new SetupChoices("~/src/corvid", ApprovalMode.Smart, "redact"), setup.Service.Saved);
    }

    [Fact]
    public void TheStepsMoveOnFromTheChecksToTheImportAndFinishOnSessions()
    {
        var setup = Fresh();
        var firstRun = setup.FirstRun;
        var scrolled = new List<string>();
        firstRun.ScrollRequested += (_, anchor) => scrolled.Add(anchor);

        Assert.Equal("Check this machine", firstRun.Heading);
        Assert.Equal("Continue to models", firstRun.NextLabel);
        Assert.True(firstRun.Steps[0].IsCurrent);

        firstRun.NextCommand.Execute(null);
        Assert.Equal(1, firstRun.CurrentStep);
        Assert.Equal("Models to download", firstRun.Heading);
        Assert.True(firstRun.Steps[1].IsCurrent);
        Assert.False(firstRun.Steps[0].IsCurrent);
        Assert.Equal("step 2 of 6", setup.Bar.Trailing);

        firstRun.SkipModelsCommand.Execute(null);
        Assert.Equal(2, firstRun.CurrentStep);
        firstRun.Steps[5].Open.Execute(null);
        Assert.Equal("Finish setup", firstRun.NextLabel);
        Assert.Equal(["models", "workspace", "import"], scrolled);

        firstRun.Modes[0].Choose.Execute(null);
        firstRun.Postures[1].Choose.Execute(null);
        firstRun.NextCommand.Execute(null);

        Assert.Equal(Area.Sessions, setup.Shell.CurrentArea);
        Assert.Equal(new SetupChoices("~/src/corvid", ApprovalMode.Manual, "local only"), setup.Service.Saved);
    }

    [Fact]
    public void TheModelsPanelPreselectsTheDefaultSetAndDownloadsWithProgress()
    {
        var setup = Fresh();
        var downloads = setup.FirstRun.Downloads;

        Assert.Equal("3.3 GB · about 2 minutes at 48 MB/s", string.Concat(downloads.SelectedRuns.Select(run => run.Text)));
        Assert.True(setup.FirstRun.HasDeciderWarning);
        Assert.Equal("Without kev-0.8b, decisions run on the degraded tier and every chat says so. Memory writes then wait for your review.",
            string.Concat(setup.FirstRun.DeciderWarning.Select(run => run.Text)));

        downloads.DownloadCommand.Execute(null);

        var kev = downloads.Items.Single(item => item.Name == "kev-0.8b");
        var gemma = downloads.Items.Single(item => item.Name == "embeddinggemma-2");
        // In the panel's order: the smaller one first.
        Assert.True(gemma.IsDownloading);
        Assert.Equal("queued", kev.Progress);
        Assert.Equal("embeddinggemma-2 0% · 1 queued", setup.Bar.AreaFields[0].Value);

        setup.Time.Advance(21);
        Assert.Equal("downloading 84% · 1.0 of 1.2 GB · 48 MB/s · 4 s left", gemma.Progress);
        Assert.Equal(0.84, gemma.Fraction, 3);

        setup.Time.Advance(60);
        // The rows keep FirstRun.png's order while downloads finish.
        Assert.Equal(["qwen3.5-0.8b", "gliner2-base-v1", "embeddinggemma-2", "kev-0.8b", "kev-4b"], downloads.Items.Select(item => item.Name));
        Assert.Equal("cached", kev.StateTag);
        Assert.Equal("cached", gemma.StateTag);
        Assert.False(setup.FirstRun.HasDeciderWarning);
        Assert.Equal("Kev-0.8B, calibrated", setup.Bar.Decider);
        Assert.Empty(setup.Bar.AreaFields);
        Assert.Equal("nothing to download", downloads.SelectedText);
    }

    [Fact]
    public void CopyingTheFixAsksTheViewToCopyIt()
    {
        var setup = Fresh();
        string? copied = null;
        setup.FirstRun.CopyRequested += (_, text) => copied = text;

        setup.FirstRun.Sections[1].Rows[2].CopyCommand.Execute(null);

        Assert.Equal("systemctl --user enable --now corvid-vault", copied);
        Assert.True(setup.FirstRun.HasNotice);
        Assert.False(setup.FirstRun.Sections[0].Rows[0].CopyCommand.CanExecute(null));
    }

    [Fact]
    public void ChoosingAnotherWorkspaceDescribesIt()
    {
        var setup = Fresh();
        var firstRun = setup.FirstRun;

        Assert.StartsWith("A git repository", string.Concat(firstRun.WorkspaceNote.Select(run => run.Text)), StringComparison.Ordinal);
        firstRun.ChooseWorkspaceCommand.Execute(null);
        Assert.True(firstRun.IsChoosingWorkspace);
        Assert.Equal(["~/src/corvid", "~/src/starling", "~/notes"], firstRun.WorkspaceChoices.Select(choice => choice.Label));

        firstRun.WorkspaceChoices[2].Choose.Execute(null);

        Assert.False(firstRun.IsChoosingWorkspace);
        Assert.Equal("~/notes", firstRun.Workspace);
        Assert.StartsWith("Not a git repository", string.Concat(firstRun.WorkspaceNote.Select(run => run.Text)), StringComparison.Ordinal);
        Assert.True(firstRun.WorkspaceChoices[2].IsSelected);
        Assert.True(firstRun.HasHermes);
    }

    [Fact]
    public void OpenedAsTheDoctorItIsTheChecksWithoutTheSetup()
    {
        Assert.True(AppOptions.TryParse(["--page", "models"], out var options, out var error), error);
        var composition = new AppComposition(options, new InlineDispatcher());

        composition.Shell.OpenDoctorCommand.Execute(null);
        var doctor = composition.FirstRun;

        Assert.False(doctor.IsSetup);
        Assert.False(composition.Shell.PageDrawsRail);
        Assert.Equal("ok", doctor.Sections[1].Rows[2].StatusText);
        Assert.Equal("", composition.StatusBar.Engine);
        doctor.NextCommand.Execute(null);
        Assert.Equal(Area.Models, composition.Shell.CurrentArea);
    }

    [Fact]
    public void OpeningTheWindowOnTheFirstRunIsTheSetup()
    {
        Assert.True(AppOptions.TryParse(["--page", "first-run"], out var options, out var error), error);
        var composition = new AppComposition(options, new InlineDispatcher());

        Assert.True(composition.FirstRun.IsSetup);
        Assert.True(composition.Shell.PageDrawsRail);
        Assert.Equal("step 1 of 6", composition.StatusBar.Trailing);
        Assert.Equal("1 warning", composition.FirstRun.Sections[1].Summary);
    }
}
