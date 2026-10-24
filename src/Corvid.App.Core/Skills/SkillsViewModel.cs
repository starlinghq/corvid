using System.Collections.ObjectModel;
using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Skills;

/// <summary>
/// The Skills area (docs/plan/ui.md §4.9, flow F12): drafts the learning loop staged, each with its reason, its SKILL.md
/// and its replay, to approve, edit then approve, or reject with a reason; the library with authorship, revision, last
/// use, uses, evals and pins, and each skill's SKILL.md rendered with its eval history; the store's git history with
/// diffs and revert; the import from Hermes or a folder, with the scanner's findings shown before anything is imported;
/// and stale skills offered for archiving. It mirrors <c>/skills</c> (src/Corvid.Host/Commands/SkillCommands.cs). The
/// rail's Skills badge counts the drafts; while the page shows, the status bar is the store's.
/// </summary>
public sealed class SkillsViewModel : AreaViewModel
{
    /// <summary>The line above the drafts.</summary>
    public const string Intro =
        "After a task succeeds the agent drafts a skill. Without a calibrated decider every draft waits here. Approve writes it as the agent's, "
        + "edit changes it first, reject records why. The replay ran the source turn again with the draft loaded.";

    /// <summary>Commits the history lists at most (/skills log's 20).</summary>
    public const int HistoryLimit = 20;

    /// <summary>Eval runs the open skill lists at most.</summary>
    public const int EvalLimit = 6;

    /// <summary>Library rows drawn at most; the filter narrows the rest.</summary>
    public const int LibraryCap = 200;

    /// <summary>Skills the Review tab's side panel lists as most used.</summary>
    public const int MostUsedCount = 4;

    private readonly ISkillsService _service;
    private readonly TimeProvider _time;
    private readonly ShellViewModel? _shell;
    private readonly StatusBarLease? _lease;
    private readonly List<SkillRowView> _rows = [];
    private readonly List<CommitRowView> _commits = [];
    private SkillsTabKind _selectedTab = SkillsTabKind.Review;
    private string _filterText = "";
    private string _outcome = "";
    private Tone _outcomeTone = Tone.Green;
    private string _loadError = "";
    private string _root = "";
    private int _staleDays = 90;
    private IReadOnlyList<string> _issues = [];
    private SkillRowView? _selectedSkill;
    private SkillDetailView? _detail;
    private CommitRowView? _selectedCommit;
    private CommitDetailView? _commitDetail;
    private string? _historySkill;
    private bool _historyLoaded;
    private int _historyVersion;
    private int _detailVersion;
    private int _commitVersion;
    private bool _active;
    private int _hiddenRows;
    private SkillImportKind _importKind = SkillImportKind.Hermes;
    private string _importFolder = "";
    private string _importSource = "";
    private ImportScan? _scan;

    public SkillsViewModel(ISkillsService service, TimeProvider time, ShellViewModel? shell = null, StatusBarViewModel? statusBar = null)
        : base(Area.Skills, "Drafts waiting for review with their replay, the library, revisions, evals and the import.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _time = time;
        _shell = shell;
        Tabs =
        [
            new SkillsTab(SkillsTabKind.Review, "Review", countsWaiting: true, Select),
            new SkillsTab(SkillsTabKind.Library, "Library", countsWaiting: false, Select),
            new SkillsTab(SkillsTabKind.History, "History", countsWaiting: false, Select),
            new SkillsTab(SkillsTabKind.Import, "Import", countsWaiting: false, Select),
        ];
        Tabs[0].IsSelected = true;
        ImportKinds =
        [
            new Choice("Hermes", "Hermes' skills folder, imported as hub:hermes", new RelayCommand(() => ImportKind = SkillImportKind.Hermes)),
            new Choice("Folder", "A folder of skill folders, imported as hub:<source>", new RelayCommand(() => ImportKind = SkillImportKind.Folder)),
        ];
        ImportKinds[0].IsSelected = true;
        ClearFilterCommand = new RelayCommand(() => FilterText = "");
        ClearHistoryFilterCommand = new AsyncRelayCommand(ct => LoadHistoryAsync(null, ct));
        CloseDetailCommand = new RelayCommand(CloseDetail);
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => PassedCount > 0);
        CancelScanCommand = new RelayCommand(() => SetScan(null));
        ArchiveAllStaleCommand = new AsyncRelayCommand(ct => ArchiveAsync([.. StaleSkills.Select(s => s.Row.Record.Name)], ct), () => StaleSkills.Count > 0);
        OpenLibraryCommand = new RelayCommand(() => Select(SkillsTabKind.Library));
        OpenImportCommand = new RelayCommand(() => Select(SkillsTabKind.Import));
        DismissOutcomeCommand = new RelayCommand(() => SetOutcome("", Tone.Grey));
        if (shell is not null && statusBar is not null)
        {
            _lease = new StatusBarLease(statusBar);
            shell.PropertyChanged += OnShellChanged;
        }

        Loaded = LoadAsync(CancellationToken.None);
        // Created on first use: --page skills navigated here before this existed.
        UpdateActive();
    }

    /// <summary>Completes when the first load has (a failure lands in <see cref="LoadError"/>); tests await it.</summary>
    public Task Loaded { get; }

    public string IntroText => Intro;

    public IReadOnlyList<SkillsTab> Tabs { get; }

    public SkillsTabKind SelectedTab
    {
        get => _selectedTab;
        set => Select(value);
    }

    public bool IsReviewTab => _selectedTab == SkillsTabKind.Review;

    public bool IsLibraryTab => _selectedTab == SkillsTabKind.Library;

    public bool IsHistoryTab => _selectedTab == SkillsTabKind.History;

    public bool IsImportTab => _selectedTab == SkillsTabKind.Import;

    // ---- notices ----

    public string Outcome => _outcome;

    public Tone OutcomeTone => _outcomeTone;

    public bool HasOutcome => _outcome.Length > 0;

    public string LoadError => _loadError;

    public bool HasLoadError => _loadError.Length > 0;

    public bool HasNotice => HasOutcome || HasLoadError;

    // ---- review ----

    public ObservableCollection<SkillDraftView> Drafts { get; } = [];

    public bool HasDrafts => Drafts.Count > 0;

    public bool HasNoDrafts => Drafts.Count == 0;

    public string EmptyReviewText => "No drafted skill is waiting. After a task succeeds, the agent's drafts land here with their replay.";

    public ObservableCollection<SkillRowView> MostUsed { get; } = [];

    public ObservableCollection<StaleSkillView> StaleSkills { get; } = [];

    public bool HasStaleSkills => StaleSkills.Count > 0;

    public bool HasNoStaleSkills => StaleSkills.Count == 0;

    public string StaleTitle => $"Idle {_staleDays} days";

    public string StaleIntro => "Agent-authored, not pinned, unused for the curator's limit. Archive moves them to .archive/; git keeps them.";

    public string NoStaleText => $"No agent skill has been idle for {_staleDays} days.";

    public string ArchiveAllLabel => StaleSkills.Count switch
    {
        0 => "Archive",
        1 => "Archive it",
        2 => "Archive both",
        var n => $"Archive all {n}",
    };

    // ---- library ----

    public ObservableCollection<SkillRowView> Skills { get; } = [];

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? ""))
                return;
            Refilter();
            OnPropertyChanged(nameof(IsFiltered));
        }
    }

    public bool IsFiltered => _filterText.Trim().Length > 0;

    public string LibraryRoot => _root;

    /// <summary>"8 skills in ~/.local/share/corvid/skills", or how many match the filter.</summary>
    public string LibrarySummary => IsFiltered
        ? $"{Skills.Count + _hiddenRows} of {_rows.Count} match “{SafeText.Clean(_filterText.Trim())}”"
        : $"{_rows.Count} skill{(_rows.Count == 1 ? "" : "s")} in {SafeText.Clean(_root)}";

    public bool HasNoSkills => Skills.Count == 0;

    /// <summary>"40 more skills; filter to find them", when the library holds more than <see cref="LibraryCap"/> matches.</summary>
    public string MoreSkillsText => _hiddenRows == 0 ? "" : $"{_hiddenRows} more skill{(_hiddenRows == 1 ? "" : "s")}; filter to find them.";

    public bool HasMoreSkills => _hiddenRows > 0;

    public string NoSkillsText => IsFiltered ? "No skill matches. Try fewer words." : "The library is empty. Approve a draft or import skills.";

    /// <summary>SKILL.md files the loader could not use: "not loaded: path: problem".</summary>
    public IReadOnlyList<string> Issues => _issues;

    public bool HasIssues => _issues.Count > 0;

    public SkillRowView? SelectedSkill => _selectedSkill;

    public SkillDetailView? Detail => _detail;

    public bool HasDetail => _detail is not null;

    public bool HasNoDetail => _detail is null;

    // ---- history ----

    public ObservableCollection<CommitRowView> Commits { get; } = [];

    public string HistoryTitle => _historySkill is null ? "Skills history" : $"History of {SafeText.Reveal(_historySkill)}";

    public string HistorySummary => $"newest first · the last {HistoryLimit} commits · git";

    public bool IsHistoryNarrowed => _historySkill is not null;

    public bool HasNoCommits => Commits.Count == 0;

    public string NoCommitsText => IsFiltered ? "No commit matches the filter." : "No commits yet.";

    public CommitRowView? SelectedCommit => _selectedCommit;

    public CommitDetailView? CommitDetail => _commitDetail;

    public bool HasCommitDetail => _commitDetail is not null;

    public bool HasNoCommitDetail => _commitDetail is null;

    // ---- import ----

    public IReadOnlyList<Choice> ImportKinds { get; }

    public SkillImportKind ImportKind
    {
        get => _importKind;
        set
        {
            if (!SetProperty(ref _importKind, value))
                return;
            ImportKinds[0].IsSelected = value == SkillImportKind.Hermes;
            ImportKinds[1].IsSelected = value == SkillImportKind.Folder;
            OnPropertyChanged(nameof(IsFolderImport));
            OnPropertyChanged(nameof(ImportNote));
            OnPropertyChanged(nameof(FolderPlaceholder));
            SetScan(null);
        }
    }

    public bool IsFolderImport => _importKind == SkillImportKind.Folder;

    public string ImportNote => _importKind == SkillImportKind.Hermes
        ? "Reads ~/.hermes/skills, or the Hermes home given below. Each skill is scanned first; a finding refuses it. Imported skills are outside content: loading one taints the session."
        : "Reads every folder holding a SKILL.md. Each skill is scanned first; a finding refuses it. Imported skills are authored hub:<source> and are outside content.";

    public string ImportFolder
    {
        get => _importFolder;
        set => SetProperty(ref _importFolder, value ?? "");
    }

    public string ImportSource
    {
        get => _importSource;
        set => SetProperty(ref _importSource, value ?? "");
    }

    public string FolderPlaceholder => _importKind == SkillImportKind.Hermes ? "~/.hermes (the default)" : "a folder of skill folders, e.g. ~/skills";

    public ObservableCollection<ImportCandidateView> Candidates { get; } = [];

    public bool HasScan => _scan is not null;

    public string ScanTitle => _scan is null ? "" : $"Dry run of {SafeText.Reveal(_scan.Location)}";

    /// <summary>"Dry run: 2 would be imported as hub:hermes, 2 refused. Nothing is imported yet."</summary>
    public string ScanSummary
    {
        get
        {
            if (_scan is null)
                return "";
            var passed = PassedCount;
            var refused = Candidates.Count - passed;
            var author = SafeText.Reveal(_scan.Author);
            if (Candidates.Count == 0)
                return "No skill folder (a folder with SKILL.md) was found. Nothing can be imported.";
            return passed == 0
                ? $"The scanner refused {(refused == 1 ? "the one skill" : $"all {refused} skills")}. Nothing can be imported."
                : $"{passed} would be imported as {author}, {refused} refused. Nothing is imported yet.";
        }
    }

    public string ImportLabel => _scan is null || PassedCount == 0 ? "Nothing to import" : $"Import {PassedCount} as {SafeText.Reveal(_scan.Author)}";

    private int PassedCount => Candidates.Count(c => !c.IsRefused);

    // ---- commands ----

    public RelayCommand ClearFilterCommand { get; }

    public AsyncRelayCommand ClearHistoryFilterCommand { get; }

    public RelayCommand CloseDetailCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand ImportCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public AsyncRelayCommand ArchiveAllStaleCommand { get; }

    public RelayCommand OpenLibraryCommand { get; }

    public RelayCommand OpenImportCommand { get; }

    public RelayCommand DismissOutcomeCommand { get; }

    /// <summary>Reads the store again and redraws the page; a failure shows in <see cref="LoadError"/>.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        SkillsSnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            SetLoadError("Could not read the skills store: " + SafeText.Clean(exception.Message));
            return;
        }

        SetLoadError("");
        _root = snapshot.Root;
        _staleDays = snapshot.StaleDays;
        _issues = [.. snapshot.Issues.Select(i => "not loaded: " + SafeText.Reveal(i))];
        var zone = _time.LocalTimeZone;
        Reset(Drafts, snapshot.Drafts.Select(d => new SkillDraftView(this, d, zone)));
        SetLibrary(snapshot.Skills);
        OnPropertyChanged(nameof(LibraryRoot));
        OnPropertyChanged(nameof(Issues));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(StaleTitle));
        OnPropertyChanged(nameof(NoStaleText));
        DraftsChanged();
        if (_historyLoaded)
            await LoadHistoryAsync(_historySkill, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Picks a tab (a click, or Ctrl+1 to Ctrl+4). History reads the log on its first showing.</summary>
    public void Select(SkillsTabKind tab)
    {
        if (!SetProperty(ref _selectedTab, tab, nameof(SelectedTab)))
            return;
        foreach (var item in Tabs)
            item.IsSelected = item.Kind == tab;
        OnPropertyChanged(nameof(IsReviewTab));
        OnPropertyChanged(nameof(IsLibraryTab));
        OnPropertyChanged(nameof(IsHistoryTab));
        OnPropertyChanged(nameof(IsImportTab));
        if (tab == SkillsTabKind.History && !_historyLoaded)
            _ = LoadHistoryAsync(_historySkill, CancellationToken.None);
    }

    /// <summary>Opens a skill in the library (a click, Enter, or a link from Review).</summary>
    public void SelectSkill(SkillRowView? row)
    {
        if (ReferenceEquals(row, _selectedSkill) && (row is null || _detail is not null))
            return;
        if (_selectedSkill is not null)
            _selectedSkill.IsSelected = false;
        _selectedSkill = row;
        if (row is not null)
            row.IsSelected = true;
        _detail = row is null ? null : new SkillDetailView(this, row, _time, _staleDays);
        RaiseDetail();
        if (_detail is { } detail)
            _ = LoadEvalsAsync(detail, CancellationToken.None);
    }

    /// <summary>Up and Down in the library or the history table: the next or previous row, the first when none is open.</summary>
    public void MoveSelection(int delta)
    {
        if (_selectedTab == SkillsTabKind.History)
        {
            if (Commits.Count == 0)
                return;
            var at = _selectedCommit is null ? -1 : Commits.IndexOf(_selectedCommit);
            var next = at < 0 ? (delta > 0 ? 0 : Commits.Count - 1) : Math.Clamp(at + delta, 0, Commits.Count - 1);
            SelectCommit(Commits[next]);
            return;
        }

        if (Skills.Count == 0)
            return;
        var index = _selectedSkill is null ? -1 : Skills.IndexOf(_selectedSkill);
        var target = index < 0 ? (delta > 0 ? 0 : Skills.Count - 1) : Math.Clamp(index + delta, 0, Skills.Count - 1);
        SelectSkill(Skills[target]);
    }

    /// <summary>Opens a commit in the history (a click, Enter, Up or Down).</summary>
    public void SelectCommit(CommitRowView? row)
    {
        if (ReferenceEquals(row, _selectedCommit) && (row is null || _commitDetail is not null))
            return;
        if (_selectedCommit is not null)
            _selectedCommit.IsSelected = false;
        _selectedCommit = row;
        if (row is not null)
            row.IsSelected = true;
        _commitDetail = null;
        RaiseCommit();
        if (row is not null)
            _ = ShowCommitAsync(row, CancellationToken.None);
    }

    /// <summary>Esc: closes the open skill or commit.</summary>
    public void CloseDetail()
    {
        if (_selectedTab == SkillsTabKind.History)
            SelectCommit(null);
        else
            SelectSkill(null);
    }

    /// <summary>Whether Esc has something to close on the current tab.</summary>
    public bool HasOpenDetail => _selectedTab == SkillsTabKind.History ? _selectedCommit is not null : _selectedSkill is not null;

    // ---- actions the cards and panels call; each failure shows as the outcome, in red, and nothing else changes ----

    internal async Task ApproveDraftAsync(SkillDraftView draft, CancellationToken cancellationToken)
    {
        var edited = draft.EditedContent;
        SkillRecord written;
        try
        {
            written = await _service.ApproveDraftAsync(draft.Id, edited, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not approve the draft {draft.Name}", exception);
            return;
        }

        Drafts.Remove(draft);
        await ReloadLibraryAsync(cancellationToken).ConfigureAwait(true);
        var name = SafeText.Reveal(written.Name);
        SetOutcome(edited is null
            ? $"Approved {name}: committed {SkillsText.Short(written.Revision)} as agent. The library lists its uses and evals from here on."
            : $"Approved {name} as edited: committed {SkillsText.Short(written.Revision)} as {SafeText.Clean(written.Author)}, since the text is now yours.", Tone.Green);
        DraftsChanged();
        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    internal async Task RejectDraftAsync(SkillDraftView draft, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _service.RejectDraftAsync(draft.Id, reason, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not reject the draft {draft.Name}", exception);
            return;
        }

        Drafts.Remove(draft);
        SetOutcome($"Rejected the draft {draft.Name}: “{SafeText.Clean(reason)}”. The reason is kept with it, so the loop learns from it.", Tone.Grey);
        DraftsChanged();
    }

    internal async Task SetPinnedAsync(SkillDetailView detail, bool pinned, CancellationToken cancellationToken)
    {
        var name = detail.Row.Record.Name;
        try
        {
            await _service.SetPinnedAsync(name, pinned, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not {(pinned ? "pin" : "unpin")} {detail.Name}", exception);
            return;
        }

        await ReloadLibraryAsync(cancellationToken).ConfigureAwait(true);
        SetOutcome(pinned ? $"{detail.Name} is pinned: the curator never touches it." : $"{detail.Name} is unpinned.", Tone.Green);
    }

    internal async Task RunEvalsAsync(SkillDetailView detail, CancellationToken cancellationToken)
    {
        EvalRun? run;
        try
        {
            run = await _service.RunEvalsAsync(detail.Row.Record.Name, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"The evals of {detail.Name} could not run", exception);
            return;
        }

        if (run is null)
        {
            SetOutcome($"{detail.Name} has no evals/ cases.", Tone.Grey);
            return;
        }

        await ReloadLibraryAsync(cancellationToken).ConfigureAwait(true);
        SetOutcome($"Evals for {detail.Name} at {SkillsText.Short(run.Revision)}: {run.Passed}/{run.Cases.Count} passed.",
            run.Score.AllPassed ? Tone.Green : Tone.Amber);
    }

    internal Task ShowHistoryAsync(string skill, CancellationToken cancellationToken)
    {
        Select(SkillsTabKind.History);
        return LoadHistoryAsync(skill, cancellationToken);
    }

    internal async Task ArchiveAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
            return;
        IReadOnlyList<string> archived;
        try
        {
            archived = await _service.ArchiveAsync(names, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("Could not archive " + string.Join(", ", names.Select(n => SafeText.Reveal(n))), exception);
            return;
        }

        await ReloadLibraryAsync(cancellationToken).ConfigureAwait(true);
        SetOutcome(archived.Count == 0 ? "Nothing to archive." : $"Archived {string.Join(", ", archived.Select(n => SafeText.Reveal(n)))} to .archive/; git keeps {(archived.Count == 1 ? "it" : "them")}.",
            archived.Count == 0 ? Tone.Grey : Tone.Green);
        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    internal async Task RevertAsync(CommitDetailView detail, CancellationToken cancellationToken)
    {
        SkillCommit commit;
        try
        {
            commit = await _service.RevertAsync(detail.Detail.Commit.Revision, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not revert {detail.Revision}", exception);
            detail.IsConfirmingRevert = false;
            return;
        }

        SetOutcome($"Reverted {detail.Revision} as user: {SkillsText.Short(commit.Revision)}. The files it touched are now yours.", Tone.Green);
        await ReloadLibraryAsync(cancellationToken).ConfigureAwait(true);
        await LoadHistoryAsync(_historySkill, cancellationToken).ConfigureAwait(true);
        if (Commits.FirstOrDefault(c => c.Commit.Revision == commit.Revision) is { } row)
            SelectCommit(row);
    }

    internal void OpenSession(string sessionId)
    {
        if (_shell is null)
            return;
        if (!_shell.OpenSession(sessionId))
            SetOutcome($"The session {SafeText.Clean(sessionId)} is not in the list any more; its events are in Audit.", Tone.Grey);
    }

    // ---- import ----

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        ImportScan scan;
        try
        {
            scan = await _service.ScanImportAsync(_importKind, _importFolder.Trim().Length == 0 ? null : _importFolder.Trim(),
                _importSource.Trim().Length == 0 ? null : _importSource.Trim(), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("The dry run failed", exception);
            return;
        }

        SetScan(scan);
    }

    /// <summary>Imports exactly what the dry run showed passing, by the candidates' ids.</summary>
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        if (_scan is not { } scan)
            return;
        IReadOnlyList<string> ids = [.. Candidates.Where(c => !c.IsRefused).Select(c => c.Candidate.Id)];
        SkillImportResult result;
        try
        {
            result = await _service.ImportAsync(scan, ids, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("Could not import the skills", exception);
            return;
        }

        SetScan(null);
        await ReloadLibraryAsync(cancellationToken).ConfigureAwait(true);
        var author = SafeText.Reveal(scan.Author);
        var refused = result.Refused.Count == 0 ? "" : " Refused: " + string.Join("; ", result.Refused.Select(r => SafeText.Reveal(r))) + ".";
        SetOutcome(result.Imported.Count == 0
            ? "Nothing was imported." + refused
            : $"Imported {string.Join(", ", result.Imported.Select(s => SafeText.Reveal(s.Name)))} as {author} ({SkillsText.Short(result.Revision)}). "
              + "They are outside content: loading one taints the session." + refused,
            result.Imported.Count == 0 ? Tone.Amber : result.Refused.Count == 0 ? Tone.Green : Tone.Amber);
        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
        if (result.Imported.Count > 0)
            Select(SkillsTabKind.Library);
    }

    private void SetScan(ImportScan? scan)
    {
        _scan = scan;
        Reset(Candidates, scan?.Candidates.Select(c => new ImportCandidateView(c)) ?? []);
        OnPropertyChanged(nameof(HasScan));
        OnPropertyChanged(nameof(ScanTitle));
        OnPropertyChanged(nameof(ScanSummary));
        OnPropertyChanged(nameof(ImportLabel));
        ImportCommand.NotifyCanExecuteChanged();
    }

    // ---- loading ----

    private async Task ReloadLibraryAsync(CancellationToken cancellationToken)
    {
        SkillsSnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("The library could not be read again", exception);
            return;
        }

        SetLibrary(snapshot.Skills);
    }

    /// <summary>Rebuilds the library's rows once, keeping the open skill open when it is still there.</summary>
    private void SetLibrary(IReadOnlyList<SkillRecord> skills)
    {
        var open = _selectedSkill?.Record.Name;
        _rows.Clear();
        _rows.AddRange(skills.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new SkillRowView(s, _time, _staleDays, SelectSkill)));
        Reset(MostUsed, _rows.OrderByDescending(r => r.Record.Uses).ThenBy(r => r.Record.Name, StringComparer.Ordinal).Take(MostUsedCount));
        var archive = ArchiveAllStaleCommand;
        Reset(StaleSkills, _rows.Where(r => r.IsStale).OrderByDescending(r => r.IdleDays).Select(r =>
            new StaleSkillView(r, new AsyncRelayCommand(ct => ArchiveAsync([r.Record.Name], ct)), new RelayCommand(() => OpenInLibrary(r)))));
        archive.NotifyCanExecuteChanged();
        Tabs[(int)SkillsTabKind.Library].Count = _rows.Count;
        _selectedSkill = null;
        _detail = null;
        Refilter();
        if (open is not null && _rows.FirstOrDefault(r => r.Record.Name == open) is { } again)
            SelectSkill(again);
        else
            RaiseDetail();
        OnPropertyChanged(nameof(HasStaleSkills));
        OnPropertyChanged(nameof(HasNoStaleSkills));
        OnPropertyChanged(nameof(ArchiveAllLabel));
        ApplyStatus();
    }

    private void OpenInLibrary(SkillRowView row)
    {
        FilterText = "";
        Select(SkillsTabKind.Library);
        SelectSkill(_rows.FirstOrDefault(r => r.Record.Name == row.Record.Name));
    }

    private void Refilter()
    {
        var filter = _filterText.Trim();
        var matching = _rows.Where(r => r.Matches(filter)).ToList();
        _hiddenRows = Math.Max(0, matching.Count - LibraryCap);
        Reset(Skills, matching.Take(LibraryCap));
        if (_selectedSkill is not null && !Skills.Contains(_selectedSkill))
            SelectSkill(null);
        Reset(Commits, _commits.Where(c => CommitMatches(c, filter)));
        if (_selectedCommit is not null && !Commits.Contains(_selectedCommit))
            SelectCommit(null);
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(HasNoSkills));
        OnPropertyChanged(nameof(NoSkillsText));
        OnPropertyChanged(nameof(MoreSkillsText));
        OnPropertyChanged(nameof(HasMoreSkills));
        OnPropertyChanged(nameof(HasNoCommits));
        OnPropertyChanged(nameof(NoCommitsText));
    }

    private static bool CommitMatches(CommitRowView row, string filter) =>
        filter.Length == 0 || filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            $"{row.Commit.Subject} {row.Commit.Author} {row.Commit.Revision} {string.Join(' ', row.Commit.Skills)}".Contains(word, StringComparison.OrdinalIgnoreCase));

    private async Task LoadEvalsAsync(SkillDetailView detail, CancellationToken cancellationToken)
    {
        var version = ++_detailVersion;
        IReadOnlyList<EvalRun> runs;
        try
        {
            runs = await _service.EvalHistoryAsync(detail.Row.Record.Name, EvalLimit, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            if (version == _detailVersion)
                Fail($"The eval history of {detail.Name} could not be read", exception);
            return;
        }

        if (version != _detailVersion || !ReferenceEquals(detail, _detail))
            return;
        var zone = _time.LocalTimeZone;
        detail.SetRuns(runs.Select(r => new EvalRunView(r, zone, r.Revision == detail.Row.Record.Revision)));
    }

    private Task RefreshHistoryAsync(CancellationToken cancellationToken) =>
        _historyLoaded ? LoadHistoryAsync(_historySkill, cancellationToken) : Task.CompletedTask;

    /// <summary>Reads the log (the whole store's, or one skill's); a newer read wins over one still in flight.</summary>
    private async Task LoadHistoryAsync(string? skill, CancellationToken cancellationToken)
    {
        var version = ++_historyVersion;
        IReadOnlyList<SkillCommit> commits;
        try
        {
            commits = await _service.LogAsync(skill, HistoryLimit, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            if (version == _historyVersion)
                Fail("The skills history could not be read", exception);
            return;
        }

        if (version != _historyVersion)
            return;
        _historyLoaded = true;
        _historySkill = skill;
        var open = _selectedCommit?.Commit.Revision;
        var zone = _time.LocalTimeZone;
        _commits.Clear();
        _commits.AddRange(commits.Select(c => new CommitRowView(c, zone, SelectCommit)));
        _selectedCommit = null;
        _commitDetail = null;
        Refilter();
        RaiseCommit();
        OnPropertyChanged(nameof(HistoryTitle));
        OnPropertyChanged(nameof(IsHistoryNarrowed));
        if (open is not null && Commits.FirstOrDefault(c => c.Commit.Revision == open) is { } again)
            SelectCommit(again);
    }

    private async Task ShowCommitAsync(CommitRowView row, CancellationToken cancellationToken)
    {
        var version = ++_commitVersion;
        SkillCommitDetail detail;
        try
        {
            detail = await _service.ShowAsync(row.Commit.Revision, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            if (version == _commitVersion)
                Fail($"Could not show {row.Revision}", exception);
            return;
        }

        if (version != _commitVersion || !ReferenceEquals(row, _selectedCommit))
            return;
        _commitDetail = new CommitDetailView(this, detail, _time.LocalTimeZone);
        RaiseCommit();
    }

    // ---- state ----

    private void DraftsChanged()
    {
        Tabs[(int)SkillsTabKind.Review].Count = Drafts.Count;
        _shell?.SetBadge(Area.Skills, Drafts.Count);
        OnPropertyChanged(nameof(HasDrafts));
        OnPropertyChanged(nameof(HasNoDrafts));
        ApplyStatus();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            UpdateActive();
    }

    private void UpdateActive()
    {
        if (_shell is null || _lease is null)
            return;
        var active = _shell.CurrentArea == Area.Skills;
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            _lease.Take();
            ApplyStatus();
            return;
        }

        // Leaving: what this area wrote and nobody has written since goes back to what it replaced.
        _lease.Release();
    }

    /// <summary>
    /// The status bar on this page: the store's size and what waits. The session's context, tags and id stay, as on the
    /// other pages of this group (Jobs, MCP, Gateway, Safety, Secrets, Settings).
    /// </summary>
    private void ApplyStatus()
    {
        if (_lease is not { IsHeld: true } lease)
            return;
        lease.Engine = "";
        lease.Gateway = "";
        lease.AreaFields = [new StatusField("skills", StatusText)];
        lease.Trailing = "idle";
    }

    /// <summary>"8 · 1 draft · 2 idle".</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string> { _rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (Drafts.Count > 0)
                parts.Add(Drafts.Count == 1 ? "1 draft" : $"{Drafts.Count} drafts");
            if (StaleSkills.Count > 0)
                parts.Add($"{StaleSkills.Count} idle");
            return string.Join(" · ", parts);
        }
    }

    private void SetOutcome(string text, Tone tone)
    {
        _outcome = text;
        _outcomeTone = tone;
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(OutcomeTone));
        OnPropertyChanged(nameof(HasOutcome));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void SetLoadError(string text)
    {
        if (string.Equals(_loadError, text, StringComparison.Ordinal))
            return;
        _loadError = text;
        OnPropertyChanged(nameof(LoadError));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void RaiseDetail()
    {
        OnPropertyChanged(nameof(SelectedSkill));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasNoDetail));
        OnPropertyChanged(nameof(HasOpenDetail));
    }

    private void RaiseCommit()
    {
        OnPropertyChanged(nameof(SelectedCommit));
        OnPropertyChanged(nameof(CommitDetail));
        OnPropertyChanged(nameof(HasCommitDetail));
        OnPropertyChanged(nameof(HasNoCommitDetail));
        OnPropertyChanged(nameof(HasOpenDetail));
    }

    private static bool Failed(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;

    /// <summary>An action failed: the outcome line says what and why, in red.</summary>
    private void Fail(string what, Exception exception) => SetOutcome($"{what}: {SafeText.Clean(exception.Message)}", Tone.Red);

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
