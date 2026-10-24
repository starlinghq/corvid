using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Tests;

/// <summary>
/// The Skills page against a scripted store: drafts and skills holding hidden characters, a replay that failed, actions
/// that fail, one action at a time per draft, and a library larger than the page draws.
/// </summary>
public sealed class SkillsReviewTests
{
    private const string Tag = "\U000E0041";
    private const string Override = "\u202E";

    private static readonly DateTimeOffset At = new(2026, 10, 24, 13, 41, 0, TimeSpan.Zero);

    private static async Task<SkillsViewModel> Open(ScriptedSkillsService service)
    {
        var skills = new SkillsViewModel(service, ManualTimeProvider.Demo());
        await skills.Loaded;
        return skills;
    }

    private static SkillDraft Draft(string id, string name, string content, ReplayResult? replay = null) =>
        new(id, name, content, "cli-77b0", "skill_write_safe scored 0.71", At,
            replay ?? new ReplayResult(true, ["read_file"], ["read_file"], "completed"));

    private static SkillRecord Skill(string name, string description = "does a thing", int uses = 1) =>
        new(name, description, "agent", "0123456789ab", At, uses, false, true, true, 0, $"---\nname: {name}\ndescription: {description}\n---\n\nbody\n");

    [Fact]
    public async Task ADraftWithHiddenCharactersShowsThemAndIsApprovedOnlyOnceTheyAreDeleted()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-1", "tidy", $"---\nname: tidy\n---\n\nRemove build{Tag} output{Override} daily.\n"));
        var skills = await Open(service);
        var draft = Assert.Single(skills.Drafts);

        // Shown, never dropped: each hidden character is its code, and a warning says why Approve is off.
        Assert.Contains("Remove build⟨U+E0041⟩ output⟨U+202E⟩ daily.", draft.Source, StringComparison.Ordinal);
        Assert.True(draft.HasHiddenText);
        Assert.Contains("hidden characters", draft.HiddenWarning, StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.True(draft.StartRejectingCommand.CanExecute(null));

        // The editor opens with the codes in place and refuses them, or the characters themselves, until deleted.
        draft.EditCommand.Execute(null);
        Assert.Contains("⟨U+E0041⟩", draft.EditText, StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        draft.EditText = draft.EditText.Replace("⟨U+E0041⟩", "", StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        draft.EditText = draft.EditText.Replace("⟨U+202E⟩", Override, StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.True(draft.HasEditProblem);
        draft.EditText = draft.EditText.Replace(Override, "", StringComparison.Ordinal);
        Assert.True(draft.ApproveCommand.CanExecute(null));

        await draft.ApproveCommand.ExecuteAsync();
        var (id, written) = Assert.Single(service.Approved);
        Assert.Equal("d-1", id);
        Assert.Equal("---\nname: tidy\n---\n\nRemove build output daily.\n", written);
        Assert.False(SafeText.HasHidden(written));
    }

    [Fact]
    public async Task ADraftWhoseNameHidesCharactersCanOnlyBeRejected()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-2", $"tidy{Override}", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        Assert.Equal("Skill draft · tidy⟨U+202E⟩", draft.Title);
        Assert.True(draft.HasHiddenText);
        Assert.Contains("name", draft.HiddenWarning, StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        draft.EditCommand.Execute(null);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.True(draft.HasEditProblem);
    }

    [Fact]
    public async Task AFailedReplayShowsWhichStepsWentWrong()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-3", "sweep", "---\nname: sweep\n---\n\nbody\n",
            new ReplayResult(false, ["list_dir", "git_status", "delete_file"], ["list_dir", "shell"], "stopped", "approval denied")));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        Assert.Equal("replay failed", draft.ReplayVerdict);
        Assert.Equal(Tone.Red, draft.ReplayTone);
        Assert.Equal(["match", "differs", "missing"], draft.Steps.Select(s => s.Verdict));
        Assert.Equal("", draft.Steps[2].Actual);
        Assert.Equal("the source turn run again with the draft loaded · turn stopped · approval denied", draft.ReplayDetail);
        Assert.Equal("step 3: expected delete_file, called nothing, missing", draft.Steps[2].AutomationName);
    }

    [Fact]
    public async Task AnExtraStepIsCalledExtra()
    {
        var steps = ReplayStepView.Steps(new ReplayResult(false, ["shell"], ["shell", $"web{Override}_fetch"], "completed"));

        Assert.Equal(["match", "extra"], steps.Select(s => s.Verdict));
        Assert.Equal("web_fetch", steps[1].Actual);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AFailedApprovalIsShownInRedAndTheDraftStays()
    {
        var service = new ScriptedSkillsService { FailApprove = true };
        service.Drafts.Add(Draft("d-4", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);

        await skills.Drafts[0].ApproveCommand.ExecuteAsync();

        Assert.Equal(Tone.Red, skills.OutcomeTone);
        Assert.Equal($"Could not approve the draft tidy: the store is read-only", skills.Outcome);
        Assert.Single(skills.Drafts);
        Assert.Null(skills.Drafts[0].ApproveCommand.Error);
        Assert.True(skills.Drafts[0].ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADraftTakesOneAnswerAtATime()
    {
        var service = new ScriptedSkillsService { HoldApprove = new TaskCompletionSource() };
        service.Drafts.Add(Draft("d-5", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        var first = draft.ApproveCommand.ExecuteAsync();
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.False(draft.StartRejectingCommand.CanExecute(null));
        Assert.False(draft.EditCommand.CanExecute(null));
        await draft.ApproveCommand.ExecuteAsync();
        service.HoldApprove.SetResult();
        await first;

        Assert.Single(service.Approved);
        Assert.Empty(skills.Drafts);
    }

    [Fact]
    public async Task AFailedLoadIsShownAndTheOtherFailuresToo()
    {
        var service = new ScriptedSkillsService { FailLoad = true };
        var skills = await Open(service);
        Assert.True(skills.HasLoadError);
        Assert.Equal("Could not read the skills store: the store is read-only", skills.LoadError);
        Assert.True(skills.HasNotice);

        service.FailLoad = false;
        service.Skills.Add(Skill("tidy"));
        await skills.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(skills.HasLoadError);

        service.FailOthers = true;
        skills.SelectSkill(skills.Skills[0]);
        await SkillsTests.WaitFor(() => skills.HasOutcome);
        Assert.Equal("The eval history of tidy could not be read: the store is read-only", skills.Outcome);
        await skills.Detail!.TogglePinCommand.ExecuteAsync();
        Assert.Equal("Could not pin tidy: the store is read-only", skills.Outcome);
        await skills.ScanCommand.ExecuteAsync();
        Assert.Equal("The dry run failed: the store is read-only", skills.Outcome);
        skills.Select(SkillsTabKind.History);
        await SkillsTests.WaitFor(() => skills.Outcome.StartsWith("The skills history", StringComparison.Ordinal));
        Assert.Equal(Tone.Red, skills.OutcomeTone);
        skills.DismissOutcomeCommand.Execute(null);
        Assert.False(skills.HasOutcome);
    }

    [Fact]
    public async Task OutsideTextInTheLibraryIsCleanedForShowingAndRevealedWhereItIsActedOn()
    {
        var service = new ScriptedSkillsService();
        service.Skills.Add(Skill($"tidy{Tag}", $"Removes{Override} build output") with { Author = $"hub:x{Override}y" });
        var skills = await Open(service);
        var row = skills.Skills[0];

        Assert.Equal("tidy⟨U+E0041⟩", row.Name);
        Assert.Equal("Removes build output", row.Description);
        Assert.Equal("hub:xy", row.Author);
        Assert.False(SafeText.HasHidden(row.AutomationName.Replace("⟨U+E0041⟩", "", StringComparison.Ordinal)));
        skills.SelectSkill(row);
        Assert.Equal("tidy⟨U+E0041⟩", skills.Detail!.Name);
        Assert.Contains(skills.Detail.Facts, f => f.Key == "folder" && f.Value == "tidy⟨U+E0041⟩/");
    }

    [Fact]
    public async Task AnImportCandidateWhoseNameHidesCharactersIsRefusedAndItsExcerptRevealed()
    {
        var service = new ScriptedSkillsService();
        service.Scan = new ImportScan(SkillImportKind.Hermes, "~/.hermes/skills", "hub:hermes",
        [
            new ImportCandidate("h-1", $"report{Tag}", "report", "ok", []),
            new ImportCandidate("h-2", "digest", "digest", $"notes{Override}", [new SkillScanFinding("SKILL.md", 9, "hide_from_user", $"skip{Override} this")]),
            new ImportCandidate("h-3", "fine", "fine", "fine", []),
        ]);
        var skills = await Open(service);

        await skills.ScanCommand.ExecuteAsync();

        var hidden = skills.Candidates[0];
        Assert.True(hidden.IsRefused);
        Assert.Equal("report⟨U+E0041⟩", hidden.Name);
        Assert.Equal(["the name holds hidden characters"], hidden.Findings);
        Assert.Equal(["hide_from_user · SKILL.md:9 · skip⟨U+202E⟩ this"], skills.Candidates[1].Findings);
        Assert.Equal("notes", skills.Candidates[1].Description);
        Assert.Equal("Import 1 as hub:hermes", skills.ImportLabel);

        await skills.ImportCommand.ExecuteAsync();
        Assert.Equal(["h-3"], service.ImportedIds);
    }

    [Fact]
    public async Task TheLibraryDrawsAtMostItsCapAndSaysHowManyMore()
    {
        var service = new ScriptedSkillsService();
        for (var i = 0; i < SkillsViewModel.LibraryCap + 40; i++)
            service.Skills.Add(Skill($"skill-{i:000}"));
        var skills = await Open(service);

        Assert.Equal(SkillsViewModel.LibraryCap, skills.Skills.Count);
        Assert.True(skills.HasMoreSkills);
        Assert.Equal("40 more skills; filter to find them.", skills.MoreSkillsText);
        Assert.Equal("240", skills.Tabs[(int)SkillsTabKind.Library].BadgeText);
        skills.FilterText = "skill-23";
        Assert.False(skills.HasMoreSkills);
        Assert.Equal(10, skills.Skills.Count);
    }

    [Fact]
    public async Task AnEditWithTheTextBoxsBareCarriageReturnsIsApprovableAndWrittenWithLineFeeds()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-6", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        // A text box hands back "\r" where Enter was pressed: neither a hidden character nor, untouched, a change.
        draft.EditCommand.Execute(null);
        draft.EditText = draft.Source.Replace("\n", "\r", StringComparison.Ordinal);
        Assert.False(draft.HasEditProblem);
        Assert.True(draft.ApproveCommand.CanExecute(null));
        draft.EditText = draft.Source.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\rone more line";
        Assert.False(draft.HasEditProblem);
        Assert.True(draft.ApproveCommand.CanExecute(null));

        await draft.ApproveCommand.ExecuteAsync();

        var (_, written) = Assert.Single(service.Approved);
        Assert.Equal("---\nname: tidy\n---\n\nbody\n\none more line\n", written);
    }

    [Fact]
    public async Task AnEditorLeftAsItWasWithCarriageReturnsApprovesTheDraftAsStaged()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-7", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        draft.EditCommand.Execute(null);
        draft.EditText = draft.Source.Replace("\n", "\r", StringComparison.Ordinal);
        await draft.ApproveCommand.ExecuteAsync();

        // Unchanged: no edited content goes to the store, so it is written as the agent's.
        var (_, written) = Assert.Single(service.Approved);
        Assert.Equal("---\nname: tidy\n---\n\nbody\n", written);
        Assert.Contains("as agent", skills.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditCannotPointTheDraftAtAnotherSkill()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-8", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        draft.EditCommand.Execute(null);
        draft.EditText = draft.Source.Replace("name: tidy", "name: release-notes", StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.Equal("The frontmatter's name: is “release-notes”, but this draft writes tidy. Put the name back, or reject the draft.", draft.EditProblem);
        draft.EditText = draft.Source.Replace("name: tidy\n", "", StringComparison.Ordinal);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        Assert.Equal("The frontmatter must keep name: tidy; this draft writes that skill.", draft.EditProblem);
        draft.EditText = draft.Source + "\nmore";
        Assert.True(draft.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task CodesAndControlPicturesAreRefusedOnlyWhereTheDraftHidCharacters()
    {
        // A draft with nothing hidden may say "⟨U+202E⟩" or "␛" in its own words.
        var plain = new ScriptedSkillsService();
        plain.Drafts.Add(Draft("d-9", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var clean = (await Open(plain)).Drafts[0];
        clean.EditCommand.Execute(null);
        clean.EditText = clean.Source + "\nThe mark ⟨U+202E⟩ is a right-to-left override; ␛ is the escape key's picture.";
        Assert.False(clean.HasEditProblem);
        Assert.True(clean.ApproveCommand.CanExecute(null));

        // A draft that hid an escape opens with its picture, which stays refused until deleted.
        var hiding = new ScriptedSkillsService();
        hiding.Drafts.Add(Draft("d-10", "tidy", "---\nname: tidy\n---\n\nbody\u001B[2J done\n"));
        var draft = (await Open(hiding)).Drafts[0];
        Assert.Contains("body␛[2J done", draft.Source, StringComparison.Ordinal);
        Assert.True(draft.HasHiddenText);
        draft.EditCommand.Execute(null);
        Assert.True(draft.HasEditProblem);
        Assert.False(draft.ApproveCommand.CanExecute(null));
        draft.EditText = draft.EditText.Replace("␛[2J", "", StringComparison.Ordinal);
        Assert.False(draft.HasEditProblem);
        Assert.True(draft.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task JoinersStayAsTheDiffShowsThemAndDoNotBlockApproval()
    {
        var service = new ScriptedSkillsService();
        // An emoji family (joined by U+200D) and a Persian word with a non-joiner (U+200C): ordinary text.
        var content = "---\nname: tidy\n---\n\nSay \U0001F468‍\U0001F469‍\U0001F467 and می‌خواهم.\n";
        service.Drafts.Add(Draft("d-11", "tidy", content));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        Assert.Equal(content.TrimEnd('\n'), draft.Source);
        Assert.False(draft.HasHiddenText);
        Assert.True(draft.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task AReasonHoldingHiddenCharactersIsShownAndNotStored()
    {
        var service = new ScriptedSkillsService();
        service.Drafts.Add(Draft("d-12", "tidy", "---\nname: tidy\n---\n\nbody\n"));
        var skills = await Open(service);
        var draft = skills.Drafts[0];

        draft.StartRejectingCommand.Execute(null);
        draft.Reason = $"not{Override} needed";
        Assert.False(draft.ConfirmRejectCommand.CanExecute(null));
        Assert.Equal("The reason holds hidden characters: “not⟨U+202E⟩ needed”. Delete them to reject.", draft.ReasonProblem);
        await draft.ConfirmRejectCommand.ExecuteAsync();
        Assert.Single(skills.Drafts);

        draft.Reason = "not needed";
        Assert.False(draft.HasReasonProblem);
        await draft.ConfirmRejectCommand.ExecuteAsync();
        Assert.Empty(skills.Drafts);
    }

    [Fact]
    public async Task DraftsPastTheCapWaitAndTakeTheirTurn()
    {
        var service = new ScriptedSkillsService();
        for (var i = 0; i < SkillsViewModel.DraftCap + 2; i++)
            service.Drafts.Add(Draft($"d-{i}", $"skill-{i:00}", $"---\nname: skill-{i:00}\n---\n\nbody\n"));
        var skills = await Open(service);

        Assert.Equal(SkillsViewModel.DraftCap, skills.Drafts.Count);
        Assert.Equal("2 more drafts wait: each shows here as one above is answered.", skills.MoreDraftsText);
        Assert.Equal((SkillsViewModel.DraftCap + 2).ToString(System.Globalization.CultureInfo.InvariantCulture), skills.Tabs[(int)SkillsTabKind.Review].BadgeText);

        await skills.Drafts[0].ApproveCommand.ExecuteAsync();

        Assert.Equal(SkillsViewModel.DraftCap, skills.Drafts.Count);
        Assert.Equal("skill-20", skills.Drafts[^1].Draft.Skill);
        Assert.Equal("1 more draft waits: it shows here once one above is answered.", skills.MoreDraftsText);
    }

    [Fact]
    public async Task TheDryRunDrawsAtMostItsCapAndImportsOnlyWhatItDrew()
    {
        var service = new ScriptedSkillsService
        {
            Scan = new ImportScan(SkillImportKind.Hermes, "~/.hermes/skills", "hub:hermes",
                [.. Enumerable.Range(0, SkillsViewModel.CandidateCap + 3).Select(i => new ImportCandidate($"h-{i}", $"s-{i}", $"s-{i}", "ok", []))]),
        };
        var skills = await Open(service);

        await skills.ScanCommand.ExecuteAsync();

        Assert.Equal(SkillsViewModel.CandidateCap, skills.Candidates.Count);
        Assert.Equal("3 more found, not drawn and not imported: import these, then run the dry run again.", skills.MoreCandidatesText);
        await skills.ImportCommand.ExecuteAsync();
        Assert.Equal(SkillsViewModel.CandidateCap, service.ImportedIds.Count);
        Assert.False(skills.HasMoreCandidates);
    }

    [Fact]
    public void TheBodyOfASkillLeavesItsFrontmatterOut()
    {
        Assert.Equal("# Title\n\ntext\n", SkillsText.Body("---\nname: a\ndescription: b\n---\n\n# Title\n\ntext\n"));
        Assert.Equal("no frontmatter", SkillsText.Body("no frontmatter"));
        Assert.Equal("b", SkillsText.Description("---\nname: a\ndescription: b\n---\n"));
        Assert.Equal("a3f9c21", SkillsText.Short("a3f9c21e8d40"));
        Assert.Equal(Tone.Amber, SkillsText.AuthorTone("hub:hermes"));
        Assert.Equal("—", SkillsText.Score(null));
    }

    /// <summary>A store the test scripts: what it holds, and which calls fail.</summary>
    private sealed class ScriptedSkillsService : ISkillsService
    {
        public List<SkillRecord> Skills { get; } = [];

        public List<SkillDraft> Drafts { get; } = [];

        public List<(string Id, string Content)> Approved { get; } = [];

        public List<string> ImportedIds { get; } = [];

        public ImportScan? Scan { get; set; }

        public bool FailLoad { get; set; }

        public bool FailApprove { get; set; }

        public bool FailOthers { get; set; }

        public TaskCompletionSource? HoldApprove { get; set; }

        public Task<SkillsSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            FailLoad ? Task.FromException<SkillsSnapshot>(ReadOnly()) : Task.FromResult(new SkillsSnapshot([.. Skills], [.. Drafts], "~/skills", []));

        public async Task<SkillRecord> ApproveDraftAsync(string draftId, string? editedContent, CancellationToken cancellationToken)
        {
            if (HoldApprove is { } hold)
                await hold.Task;
            if (FailApprove)
                throw ReadOnly();
            var draft = Drafts.Single(d => d.Id == draftId);
            var content = editedContent ?? draft.Content;
            Approved.Add((draftId, content));
            Drafts.Remove(draft);
            var record = new SkillRecord(draft.Skill, "", "agent", "fedcba987654", null, 0, false, true, true, 0, content);
            Skills.Add(record);
            return record;
        }

        public Task RejectDraftAsync(string draftId, string reason, CancellationToken cancellationToken) => Others(() => Drafts.RemoveAll(d => d.Id == draftId));

        public Task<IReadOnlyList<SkillCommit>> LogAsync(string? skill, int limit, CancellationToken cancellationToken) =>
            Others<IReadOnlyList<SkillCommit>>(() => []);

        public Task<SkillCommitDetail> ShowAsync(string revision, CancellationToken cancellationToken) => throw ReadOnly();

        public Task<SkillCommit> RevertAsync(string revision, CancellationToken cancellationToken) => throw ReadOnly();

        public Task<IReadOnlyList<EvalRun>> EvalHistoryAsync(string skill, int limit, CancellationToken cancellationToken) =>
            Others<IReadOnlyList<EvalRun>>(() => []);

        public Task<EvalRun?> RunEvalsAsync(string skill, CancellationToken cancellationToken) => Others<EvalRun?>(() => null);

        public Task SetPinnedAsync(string skill, bool pinned, CancellationToken cancellationToken) => Others(() => 0);

        public Task<IReadOnlyList<string>> ArchiveAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken) =>
            Others<IReadOnlyList<string>>(() => []);

        public Task<ImportScan> ScanImportAsync(SkillImportKind kind, string? folder, string? source, CancellationToken cancellationToken) =>
            Others(() => Scan ?? new ImportScan(kind, "~/.hermes/skills", "hub:hermes", []));

        public Task<SkillImportResult> ImportAsync(ImportScan scan, IReadOnlyList<string> candidateIds, CancellationToken cancellationToken) =>
            Others(() =>
            {
                ImportedIds.AddRange(candidateIds);
                return new SkillImportResult([], [], null);
            });

        private Task<T> Others<T>(Func<T> result) => FailOthers ? Task.FromException<T>(ReadOnly()) : Task.FromResult(result());

        private static InvalidOperationException ReadOnly() => new("the store is read-only");
    }
}
