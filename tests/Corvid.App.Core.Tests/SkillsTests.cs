using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;
using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Tests;

/// <summary>
/// The Skills area over its demo service: flow F12 (a draft reviewed, approved, edited or rejected; the library's uses,
/// evals and pins; stale skills archived), the history with diff and revert, the import's dry run, the status bar and
/// the rail's badge.
/// </summary>
public sealed class SkillsTests
{
    private static async Task<(SkillsViewModel Skills, DemoSkillsService Service, ShellViewModel Shell, StatusBarViewModel Bar)> Open()
    {
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        var service = new DemoSkillsService(ManualTimeProvider.Demo());
        var skills = new SkillsViewModel(service, ManualTimeProvider.Demo(), shell, bar);
        await skills.Loaded;
        return (skills, service, shell, bar);
    }

    [Fact]
    public async Task TheDemoOpensOnOneDraftWithItsReasonAndAPassingReplay()
    {
        var (skills, _, shell, _) = await Open();

        Assert.True(skills.IsReviewTab);
        var draft = Assert.Single(skills.Drafts);
        Assert.Equal("Skill draft · vault-service-policy", draft.Title);
        Assert.Equal("drafted 13:41 from cli-77b0, turn 6", draft.Origin);
        Assert.Equal("why staged: skill_write_safe scored 0.71, under its floor 0.85", draft.Why);
        Assert.Equal("replay passed", draft.ReplayVerdict);
        Assert.Equal(Tone.Green, draft.ReplayTone);
        Assert.All(draft.Steps, step => Assert.Equal("match", step.Verdict));
        Assert.Equal(4, draft.Steps.Count);
        Assert.StartsWith("---\nname: vault-service-policy", draft.Source, StringComparison.Ordinal);
        Assert.True(draft.ApproveCommand.CanExecute(null));
        Assert.False(draft.HasHiddenText);

        Assert.Equal("1", skills.Tabs[(int)SkillsTabKind.Review].BadgeText);
        Assert.Equal("8", skills.Tabs[(int)SkillsTabKind.Library].BadgeText);
        Assert.Equal(1, shell.RailItem(Area.Skills).Badge);
        Assert.Equal(["nightly-tidy-sweep", "dotnet-test-filter", "ci-flaky-triage", "release-notes"], skills.MostUsed.Select(s => s.Name));
        Assert.Equal(["python-venv-setup", "docker-compose-up"], skills.StaleSkills.Select(s => s.Name));
        Assert.Equal("idle 116 days · 2 uses · agent", skills.StaleSkills[0].Detail);
        Assert.Equal("Archive both", skills.ArchiveAllLabel);
    }

    [Fact]
    public async Task ApprovingTheDraftWritesItAsTheAgentsAndTheLibraryListsIt()
    {
        var (skills, _, shell, _) = await Open();
        var draft = skills.Drafts[0];

        await draft.ApproveCommand.ExecuteAsync();

        Assert.Empty(skills.Drafts);
        Assert.True(skills.HasNoDrafts);
        Assert.Equal("", skills.Tabs[(int)SkillsTabKind.Review].BadgeText);
        Assert.Equal(0, shell.RailItem(Area.Skills).Badge);
        Assert.Equal(Tone.Green, skills.OutcomeTone);
        Assert.Contains("Approved vault-service-policy: committed", skills.Outcome, StringComparison.Ordinal);
        Assert.Contains("as agent", skills.Outcome, StringComparison.Ordinal);
        var row = Assert.Single(skills.Skills, s => s.Name == "vault-service-policy");
        Assert.Equal("agent", row.Author);
        Assert.Equal("never", row.LastUsed);
        Assert.Equal("9", skills.Tabs[(int)SkillsTabKind.Library].BadgeText);

        skills.Select(SkillsTabKind.History);
        await WaitFor(() => skills.Commits.Count > 0);
        Assert.Equal("Add skill vault-service-policy (approved by the user)", skills.Commits[0].Subject);
        Assert.Equal("agent", skills.Commits[0].Author);
    }

    [Fact]
    public async Task AnEditedDraftIsWrittenAsEditedAndAuthoredByTheUser()
    {
        var (skills, _, _, _) = await Open();
        var draft = skills.Drafts[0];

        draft.EditCommand.Execute(null);
        Assert.True(draft.IsEditing);
        Assert.Equal("Approve as edited", draft.ApproveLabel);
        draft.EditText = "";
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.Equal("A SKILL.md cannot be empty.", draft.EditProblem);
        draft.EditText = draft.Source.Replace("nothing on the parent", "nothing on the parent path", StringComparison.Ordinal);
        Assert.True(draft.ApproveCommand.CanExecute(null));

        await draft.ApproveCommand.ExecuteAsync();

        var row = Assert.Single(skills.Skills, s => s.Name == "vault-service-policy");
        Assert.Equal("user", row.Author);
        Assert.Contains("nothing on the parent path", row.Record.Content, StringComparison.Ordinal);
        Assert.Contains("as edited", skills.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditorOpenedAndLeftAsItWasApprovesTheDraftAsTheAgents()
    {
        var (skills, _, _, _) = await Open();
        var draft = skills.Drafts[0];

        draft.EditCommand.Execute(null);
        await draft.ApproveCommand.ExecuteAsync();

        Assert.Equal("agent", skills.Skills.Single(s => s.Name == "vault-service-policy").Author);
        Assert.Contains("as agent", skills.Outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("as edited", skills.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectingNeedsAReasonAndRecordsIt()
    {
        var (skills, service, _, _) = await Open();
        var draft = skills.Drafts[0];

        draft.StartRejectingCommand.Execute(null);
        Assert.True(draft.IsRejecting);
        Assert.False(draft.ShowsActions);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.False(draft.ConfirmRejectCommand.CanExecute(null));
        draft.Reason = "   ";
        Assert.False(draft.ConfirmRejectCommand.CanExecute(null));
        draft.Reason = "the policy belongs in the vault repo";
        await draft.ConfirmRejectCommand.ExecuteAsync();

        Assert.Empty(skills.Drafts);
        Assert.Equal("the policy belongs in the vault repo", service.Rejections[DemoSkillsService.DraftId]);
        Assert.Contains("“the policy belongs in the vault repo”", skills.Outcome, StringComparison.Ordinal);
        Assert.DoesNotContain(skills.Skills, s => s.Name == "vault-service-policy");
    }

    [Fact]
    public async Task TheEvidenceLinkOpensTheSourceSession()
    {
        using var composition = new AppComposition(Options("--page", "skills"), new InlineDispatcher(), ManualTimeProvider.DemoLocal());
        var skills = composition.Skills;
        await skills.Loaded;

        skills.Drafts[0].OpenEvidenceCommand.Execute(null);

        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        Assert.Equal("cli-77b0", composition.Shell.CurrentSession?.Id);
    }

    [Fact]
    public async Task TheOpenSkillShowsItsDocumentFactsAndEvalHistory()
    {
        var (skills, _, _, _) = await Open();
        skills.Select(SkillsTabKind.Library);
        var row = skills.Skills.Single(s => s.Name == "ci-flaky-triage");

        skills.SelectSkill(row);
        await WaitFor(() => skills.Detail?.HasEvalRuns == true);

        var detail = skills.Detail!;
        Assert.True(row.IsSelected);
        Assert.True(row.IsPinned);
        Assert.Equal("Unpin", detail.PinLabel);
        Assert.Contains(detail.Facts, f => f.Key == "last used" && f.Value == "14:03 in cli-9f2e · 12 uses");
        Assert.Contains(detail.Facts, f => f.Key == "revision" && f.Value == "a3f9c21");
        Assert.Equal(["a3f9c21 · current", "f1a0c3d"], detail.EvalRuns.Select(r => r.Label));
        Assert.Equal(["3/3", "2/3"], detail.EvalRuns.Select(r => r.Score));
        Assert.Equal(Tone.Amber, detail.EvalRuns[1].Tone);
        Assert.NotEmpty(detail.Document.Blocks);
        Assert.False(detail.CanArchive);

        skills.CloseDetail();
        Assert.Null(skills.Detail);
        Assert.False(row.IsSelected);
    }

    [Fact]
    public async Task RunningEvalsReportsTheScoreAndAFailureInAmber()
    {
        var (skills, _, _, _) = await Open();
        skills.SelectSkill(skills.Skills.Single(s => s.Name == "dotnet-test-filter"));
        await WaitFor(() => skills.Detail?.HasEvalRuns == true);

        await skills.Detail!.RunEvalsCommand.ExecuteAsync();

        Assert.Equal("Evals for dotnet-test-filter at 51c8d2a: 4/5 passed.", skills.Outcome);
        Assert.Equal(Tone.Amber, skills.OutcomeTone);
        await WaitFor(() => skills.Detail?.EvalRuns.Count == 2);
        var failed = Assert.Single(skills.Detail!.LatestCases, c => !c.Passed);
        Assert.Equal("expected shell; called shell, shell · ran the whole suite first", failed.Detail);
    }

    [Fact]
    public async Task PinningTakesASkillOutOfTheStaleListAndUnpinningPutsItBack()
    {
        var (skills, service, _, _) = await Open();
        skills.SelectSkill(skills.Skills.Single(s => s.Name == "docker-compose-up"));
        Assert.True(skills.Detail!.CanArchive);

        await skills.Detail.TogglePinCommand.ExecuteAsync();

        Assert.Equal("docker-compose-up is pinned: the curator never touches it.", skills.Outcome);
        Assert.Equal(["python-venv-setup"], skills.StaleSkills.Select(s => s.Name));
        Assert.True(skills.SelectedSkill!.IsPinned);
        Assert.Equal("Unpin", skills.Detail!.PinLabel);
        var snapshot = await service.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(snapshot.Skills.Single(s => s.Name == "docker-compose-up").Pinned);

        await skills.Detail.TogglePinCommand.ExecuteAsync();
        Assert.Equal(2, skills.StaleSkills.Count);
    }

    [Fact]
    public async Task ArchivingTheStaleSkillsRemovesThemAndCommits()
    {
        var (skills, _, _, bar) = await Open();

        await skills.ArchiveAllStaleCommand.ExecuteAsync();

        Assert.Empty(skills.StaleSkills);
        Assert.True(skills.HasNoStaleSkills);
        Assert.Equal(6, skills.Skills.Count);
        Assert.Equal("Archived python-venv-setup, docker-compose-up to .archive/; git keeps them.", skills.Outcome);
        skills.Select(SkillsTabKind.History);
        await WaitFor(() => skills.Commits.Count > 0);
        Assert.Equal("Archive python-venv-setup, docker-compose-up (idle)", skills.Commits[0].Subject);
        Assert.Equal("user", skills.Commits[0].Author);
    }

    [Fact]
    public async Task TheHistoryShowsACommitsDiffAndRevertsItAsTheUser()
    {
        var (skills, _, _, _) = await Open();
        skills.Select(SkillsTabKind.History);
        await WaitFor(() => skills.Commits.Count > 0);
        Assert.Equal(11, skills.Commits.Count);
        Assert.Equal("3e1d07a", skills.Commits[0].Revision);
        Assert.Equal("2026-10-24 09:02", skills.Commits[0].When);

        skills.SelectCommit(skills.Commits.Single(c => c.Revision == "a3f9c21"));
        await WaitFor(() => skills.CommitDetail is not null);
        var file = Assert.Single(skills.CommitDetail!.Files);
        Assert.Equal("ci-flaky-triage/SKILL.md", file.Path);
        Assert.Equal("+3 -2", file.Counts.Replace("−", "-", StringComparison.Ordinal));
        Assert.Contains(file.Rows, r => r.Kind == Diffs.DiffRowKind.Added && r.Text.Contains("TMPDIR", StringComparison.Ordinal));

        skills.CommitDetail.StartRevertCommand.Execute(null);
        Assert.True(skills.CommitDetail.IsConfirmingRevert);
        await skills.CommitDetail.ConfirmRevertCommand.ExecuteAsync();

        Assert.Equal(Tone.Green, skills.OutcomeTone);
        Assert.StartsWith("Reverted a3f9c21 as user", skills.Outcome, StringComparison.Ordinal);
        Assert.Equal("Revert \"Update ci-flaky-triage: check TMPDIR before re-running\"", skills.Commits[0].Subject);
        Assert.Same(skills.Commits[0], skills.SelectedCommit);
        var row = skills.Skills.Single(s => s.Name == "ci-flaky-triage");
        Assert.Equal("user", row.Author);
        Assert.DoesNotContain("TMPDIR", row.Record.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevertTheStoreRefusesShowsWhyInRed()
    {
        var (skills, _, _, _) = await Open();
        skills.Select(SkillsTabKind.History);
        await WaitFor(() => skills.Commits.Count > 0);
        skills.SelectCommit(skills.Commits.Single(c => c.Revision == "f1a0c3d"));
        await WaitFor(() => skills.CommitDetail is not null);

        skills.CommitDetail!.StartRevertCommand.Execute(null);
        await skills.CommitDetail.ConfirmRevertCommand.ExecuteAsync();

        Assert.Equal(Tone.Red, skills.OutcomeTone);
        Assert.Equal("Could not revert f1a0c3d: ci-flaky-triage changed after f1a0c3d; revert the later commits first.", skills.Outcome);
        Assert.False(skills.CommitDetail!.IsConfirmingRevert);
        Assert.Equal(11, skills.Commits.Count);
    }

    [Fact]
    public async Task OneSkillsHistoryNarrowsTheLogAndClearingWidensIt()
    {
        var (skills, _, _, _) = await Open();
        skills.SelectSkill(skills.Skills.Single(s => s.Name == "release-notes"));

        await skills.Detail!.ShowHistoryCommand.ExecuteAsync();

        Assert.True(skills.IsHistoryTab);
        Assert.True(skills.IsHistoryNarrowed);
        Assert.Equal("History of release-notes", skills.HistoryTitle);
        Assert.Equal(["7b20e5f", "2d8e61f"], skills.Commits.Select(c => c.Revision));

        await skills.ClearHistoryFilterCommand.ExecuteAsync();
        Assert.False(skills.IsHistoryNarrowed);
        Assert.Equal(11, skills.Commits.Count);
    }

    [Fact]
    public async Task UpAndDownMoveThroughTheLibraryAndTheHistory()
    {
        var (skills, _, _, _) = await Open();
        skills.Select(SkillsTabKind.Library);

        skills.MoveSelection(1);
        Assert.Same(skills.Skills[0], skills.SelectedSkill);
        skills.MoveSelection(1);
        Assert.Same(skills.Skills[1], skills.SelectedSkill);
        skills.MoveSelection(-5);
        Assert.Same(skills.Skills[0], skills.SelectedSkill);

        skills.Select(SkillsTabKind.History);
        await WaitFor(() => skills.Commits.Count > 0);
        skills.MoveSelection(-1);
        Assert.Same(skills.Commits[^1], skills.SelectedCommit);
        Assert.True(skills.HasOpenDetail);
        skills.CloseDetail();
        Assert.Null(skills.SelectedCommit);
    }

    [Fact]
    public async Task TheFilterNarrowsTheLibraryAndTheHistory()
    {
        var (skills, _, _, _) = await Open();
        skills.Select(SkillsTabKind.History);
        await WaitFor(() => skills.Commits.Count > 0);

        skills.FilterText = "vault";

        Assert.Equal(["vault-cli"], skills.Skills.Select(s => s.Name));
        Assert.Equal("1 of 8 match “vault”", skills.LibrarySummary);
        Assert.Equal(["0d94bb7"], skills.Commits.Select(c => c.Revision));
        skills.FilterText = "nothing-like-this";
        Assert.True(skills.HasNoSkills);
        Assert.Equal("No skill matches. Try fewer words.", skills.NoSkillsText);
        skills.ClearFilterCommand.Execute(null);
        Assert.Equal(8, skills.Skills.Count);
        Assert.Equal("8 skills in ~/.local/share/corvid/skills", skills.LibrarySummary);
    }

    [Fact]
    public async Task TheHermesDryRunShowsTheScannersFindingsAndImportsOnlyWhatPassed()
    {
        var (skills, _, _, _) = await Open();
        skills.Select(SkillsTabKind.Import);
        Assert.False(skills.ImportCommand.CanExecute(null));

        await skills.ScanCommand.ExecuteAsync();

        Assert.True(skills.HasScan);
        Assert.Equal("Dry run of ~/.hermes/skills", skills.ScanTitle);
        Assert.Equal(4, skills.Candidates.Count);
        Assert.Equal("2 would be imported as hub:hermes, 2 refused. Nothing is imported yet.", skills.ScanSummary);
        Assert.Equal("Import 2 as hub:hermes", skills.ImportLabel);
        var tunnel = skills.Candidates.Single(c => c.Name == "ssh-tunnel");
        Assert.Equal("refused", tunnel.Verdict);
        Assert.Equal(["credential_read · SKILL.md:14 · cat ~/.ssh/id_ed25519"], tunnel.Findings);

        await skills.ImportCommand.ExecuteAsync();

        Assert.False(skills.HasScan);
        Assert.True(skills.IsLibraryTab);
        Assert.Equal(10, skills.Skills.Count);
        var report = skills.Skills.Single(s => s.Name == "weekly-report");
        Assert.True(report.IsOutside);
        Assert.Equal("hub:hermes", report.Author);
        Assert.Equal(Tone.Amber, report.AuthorTone);
        Assert.Contains("Imported git-cleanup, weekly-report as hub:hermes", skills.Outcome.Replace("weekly-report, git-cleanup", "git-cleanup, weekly-report", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("taints the session", skills.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderImportNeedsAFolderAndSaysSo()
    {
        var (skills, _, _, _) = await Open();
        skills.ImportKinds[1].Choose.Execute(null);
        Assert.True(skills.IsFolderImport);
        Assert.True(skills.ImportKinds[1].IsSelected);
        Assert.False(skills.ImportKinds[0].IsSelected);

        await skills.ScanCommand.ExecuteAsync();
        Assert.Equal(Tone.Red, skills.OutcomeTone);
        Assert.StartsWith("The dry run failed: Give the folder", skills.Outcome, StringComparison.Ordinal);
        Assert.False(skills.HasScan);

        skills.ImportFolder = "~/skills";
        skills.ImportSource = "team";
        await skills.ScanCommand.ExecuteAsync();
        Assert.Equal("1 would be imported as hub:team, 0 refused. Nothing is imported yet.", skills.ScanSummary);
        skills.CancelScanCommand.Execute(null);
        Assert.False(skills.HasScan);
    }

    [Fact]
    public async Task WhileThePageShowsTheStatusBarIsTheStoresAndLeavingPutsItBack()
    {
        var bar = new StatusBarViewModel { Context = "38%", SessionId = "cli-9f2e", Trailing = "turn 48 s" };
        var shell = new ShellViewModel(bar);
        var skills = new SkillsViewModel(new DemoSkillsService(ManualTimeProvider.Demo()), ManualTimeProvider.Demo(), shell, bar);
        await skills.Loaded;

        shell.Navigate(Area.Skills);
        Assert.Equal(new StatusField("skills", "8 · 1 draft · 2 idle"), Assert.Single(bar.AreaFields));
        // The session's context and id stay, as on the other pages of the group.
        Assert.Equal("38%", bar.Context);
        Assert.Equal("cli-9f2e", bar.SessionId);
        Assert.Equal("idle", bar.Trailing);

        await skills.Drafts[0].ApproveCommand.ExecuteAsync();
        Assert.Equal(new StatusField("skills", "9 · 2 idle"), Assert.Single(bar.AreaFields));

        shell.Navigate(Area.Sessions);
        Assert.Empty(bar.AreaFields);
        Assert.Equal("38%", bar.Context);
        Assert.Equal("turn 48 s", bar.Trailing);
    }

    [Fact]
    public async Task OpenedFromTheCommandLineThePageTakesTheBarAtOnceAndTheCompositionDisposes()
    {
        using var composition = new AppComposition(Options("--page", "skills"), new InlineDispatcher(), ManualTimeProvider.DemoLocal());

        var skills = composition.Skills;
        await skills.Loaded;

        Assert.Equal("skills", Assert.Single(composition.StatusBar.AreaFields).Label);
        Assert.Equal(1, composition.Shell.RailItem(Area.Skills).Badge);
        Assert.Same(skills, composition.Skills);
    }

    private static AppOptions Options(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    /// <summary>The demo answers synchronously, so what a command started has finished; this only spins the continuations.</summary>
    internal static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Yield();
        Assert.True(condition());
    }
}
