using System.Collections.ObjectModel;
using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Memory;

/// <summary>
/// What the status bar shows on the Memory page besides the store's size (Memory.png): the generator, the decider and
/// the scheduler, with no session context, tags or running turn.
/// </summary>
public sealed record MemoryEngineStatus(string Generator, string Decider, string Scheduler, string Trailing = "idle")
{
    /// <summary>Memory.png's status bar.</summary>
    public static MemoryEngineStatus Demo { get; } = new("qwen3.5-0.8b · cuda:0 · q0 · 180 ms", "Kev-0.8B, calibrated", "here · 2 jobs");
}

/// <summary>
/// The Memory area (docs/plan/ui.md §4.9, flow F11, Memory.png): the review queue of staged writes (facts and core block
/// updates) and lesson candidates, the core blocks and their budgets, the facts with their bi-temporal fields, the
/// approved lessons and the Hermes import. Approve writes, Correct… edits first, Reject records why. The rail's Memory
/// badge and the page's tab badges count what waits; while the page shows, the status bar is the store's.
/// </summary>
public sealed class MemoryViewModel : AreaViewModel
{
    /// <summary>The line above the queue (Memory.png).</summary>
    public const string Intro =
        "Every write is staged when the decider is unsure or absent. Approve writes it, correct edits the text first, reject records why. "
        + "The nearest existing facts are shown so an add that should be an update is visible.";

    /// <summary>How many facts the Review tab's side panel lists.</summary>
    public const int RecentCount = 3;

    private readonly IMemoryService _service;
    private readonly TimeProvider _time;
    private readonly ShellViewModel? _shell;
    private readonly StatusBarViewModel? _statusBar;
    private readonly MemoryEngineStatus? _engine;
    private readonly List<FactRecord> _facts = [];
    private readonly List<Lesson> _lessons = [];
    private MemoryTabKind _selectedTab = MemoryTabKind.Review;
    private MemoryStats _stats = new(0, 0, 0);
    private string _searchText = "";
    private string _searchedFor = "";
    private string _outcome = "";
    private Tone _outcomeTone = Tone.Green;
    private ImportSource? _import;
    private bool _hasPreview;
    private string _loadError = "";
    private int _searches;
    private readonly RelayCommand _openReview;

    public MemoryViewModel(IMemoryService service, TimeProvider time, ShellViewModel? shell = null, StatusBarViewModel? statusBar = null,
        MemoryEngineStatus? engine = null)
        : base(Area.Memory, "Staged writes to review, the core blocks and their budgets, facts, lessons and the Hermes import.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _time = time;
        _shell = shell;
        _statusBar = statusBar;
        _engine = engine;
        Tabs =
        [
            new MemoryTab(MemoryTabKind.Review, "Review", Select),
            new MemoryTab(MemoryTabKind.Facts, "Facts", Select),
            new MemoryTab(MemoryTabKind.CoreBlocks, "Core blocks", Select),
            new MemoryTab(MemoryTabKind.Lessons, "Lessons", Select),
            new MemoryTab(MemoryTabKind.Import, "Import", Select),
        ];
        Tabs[0].IsSelected = true;
        _openReview = new RelayCommand(() => Select(MemoryTabKind.Review));
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearSearchCommand = new AsyncRelayCommand(ct => { SearchText = ""; return SearchAsync(ct); });
        EditBlocksCommand = new RelayCommand(() => Select(MemoryTabKind.CoreBlocks));
        DryRunCommand = new AsyncRelayCommand(DryRunAsync);
        ImportForReviewCommand = new AsyncRelayCommand(ct => !HasPreview ? DryRunAsync(ct)
            : PassedCount > 0 ? StageImportAsync(ct)
            : NothingToStage());
        StageImportCommand = new AsyncRelayCommand(StageImportAsync, () => PassedCount > 0);
        CancelImportCommand = new RelayCommand(() => { ImportEntries.Clear(); HasPreview = false; });
        DismissOutcomeCommand = new RelayCommand(() => SetOutcome("", Tone.Grey));
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellChanged;
            if (_shell.CurrentArea == Area.Memory)
                ApplyStatus();
        }

        Loaded = LoadAsync(CancellationToken.None);
    }

    /// <summary>Completes when the first load has (a failure lands in <see cref="LoadError"/>); tests await it.</summary>
    public Task Loaded { get; }

    public string IntroText => Intro;

    public IReadOnlyList<MemoryTab> Tabs { get; }

    public MemoryTabKind SelectedTab
    {
        get => _selectedTab;
        set => Select(value);
    }

    public bool IsReviewTab => _selectedTab == MemoryTabKind.Review;

    public bool IsFactsTab => _selectedTab == MemoryTabKind.Facts;

    public bool IsBlocksTab => _selectedTab == MemoryTabKind.CoreBlocks;

    public bool IsLessonsTab => _selectedTab == MemoryTabKind.Lessons;

    public bool IsImportTab => _selectedTab == MemoryTabKind.Import;

    /// <summary>The review tab's two columns: the queue, and the blocks, recent facts and import beside it.</summary>
    public bool ShowsSidePanels => _selectedTab == MemoryTabKind.Review;

    // ---- the review queue ----

    public ObservableCollection<StagedFactView> StagedFacts { get; } = [];

    public ObservableCollection<BlockUpdateView> BlockUpdates { get; } = [];

    public ObservableCollection<LessonCandidateView> LessonCandidates { get; } = [];

    /// <summary>Staged facts and block updates waiting.</summary>
    public int ReviewCount => StagedFacts.Count + BlockUpdates.Count;

    /// <summary>
    /// The Review tab's and the rail's count. Memory.png counts the memory writes there and the lesson candidate under
    /// Lessons; once the writes are answered, a candidate still waits in the Review list, so the count becomes the
    /// candidates' and the badge never goes while something waits on you.
    /// </summary>
    public int WaitingCount => ReviewCount > 0 ? ReviewCount : LessonCandidates.Count;

    public bool HasNothingToReview => ReviewCount == 0 && LessonCandidates.Count == 0;

    public bool HasNoLessonCandidates => LessonCandidates.Count == 0;

    public bool HasStagedFacts => StagedFacts.Count > 0;

    public bool HasBlockUpdates => BlockUpdates.Count > 0;

    public bool HasLessonCandidates => LessonCandidates.Count > 0;

    public string EmptyReviewText => "Nothing waits for review. Writes the decider is unsure of, block updates and lessons land here.";

    /// <summary>What the last action did: "Written as an update: …", "Rejected: …".</summary>
    public string Outcome => _outcome;

    public Tone OutcomeTone => _outcomeTone;

    public bool HasOutcome => _outcome.Length > 0;

    public string LoadError => _loadError;

    public bool HasLoadError => _loadError.Length > 0;

    /// <summary>The strip under the page head shows: an outcome, a failure or a failed load.</summary>
    public bool HasNotice => HasOutcome || HasLoadError;

    // ---- blocks, facts, lessons ----

    public ObservableCollection<CoreBlockView> CoreBlocks { get; } = [];

    /// <summary>The newest few facts for the side panel.</summary>
    public ObservableCollection<FactRowView> RecentFacts { get; } = [];

    /// <summary>The Facts tab: every fact, or those matching the search.</summary>
    public ObservableCollection<FactRowView> Facts { get; } = [];

    public ObservableCollection<LessonRowView> Lessons { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? "");
    }

    /// <summary>"3 facts match “kev”", or the total when nothing is searched for.</summary>
    public string FactsSummary => _searchedFor.Length == 0
        ? $"{MemoryText.Count(Facts.Count)} facts, newest first · bi-temporal"
        : $"{MemoryText.Count(Facts.Count)} {(Facts.Count == 1 ? "fact matches" : "facts match")} “{_searchedFor}”";

    public bool IsFiltered => _searchedFor.Length > 0;

    public bool HasNoFacts => Facts.Count == 0;

    public string StatsText => $"{MemoryText.Count(_stats.Facts)} facts · {MemoryText.Count(_stats.Episodes)} episodes · {MemoryText.Count(_stats.Blocks)} blocks";

    // ---- import ----

    public string ImportTitle => _import is null ? "Import" : $"Import from {_import.Name}";

    public string ImportPath => _import?.Path ?? "";

    public string ImportNote => _import?.Note ?? "";

    public ObservableCollection<ImportEntryView> ImportEntries { get; } = [];

    /// <summary>A dry run's list is showing; nothing is staged until "Stage … for review".</summary>
    public bool HasPreview
    {
        get => _hasPreview;
        private set
        {
            if (!SetProperty(ref _hasPreview, value))
                return;
            OnPropertyChanged(nameof(PreviewSummary));
            OnPropertyChanged(nameof(StageLabel));
            StageImportCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"Dry run: 3 would be staged, 2 refused. Nothing is staged yet.", or that every entry was refused.</summary>
    public string PreviewSummary
    {
        get
        {
            var passed = PassedCount;
            var refused = ImportEntries.Count - passed;
            if (ImportEntries.Count == 0)
                return "Dry run: the source holds no entries. Nothing can be staged.";
            return passed == 0
                ? $"Dry run: the scanner refused {(refused == 1 ? "the one entry" : $"all {refused} entries")}. Nothing can be staged."
                : $"Dry run: {passed} would be staged, {refused} refused. Nothing is staged yet.";
        }
    }

    public string StageLabel => PassedCount == 0 ? "Nothing to stage" : $"Stage {PassedCount} for review";

    private int PassedCount => ImportEntries.Count(e => !e.IsRefused);

    // ---- commands ----

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand ClearSearchCommand { get; }

    public RelayCommand EditBlocksCommand { get; }

    public AsyncRelayCommand DryRunCommand { get; }

    /// <summary>Runs the dry run first when none is showing, so every entry is seen before anything is staged.</summary>
    public AsyncRelayCommand ImportForReviewCommand { get; }

    public AsyncRelayCommand StageImportCommand { get; }

    public RelayCommand CancelImportCommand { get; }

    public RelayCommand DismissOutcomeCommand { get; }

    /// <summary>Reads the store again and redraws the page; a failure shows in <see cref="LoadError"/>.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        MemorySnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            SetLoadError("Could not read memory: " + SafeText.Clean(exception.Message));
            return;
        }

        SetLoadError("");
        var zone = _time.LocalTimeZone;
        _facts.Clear();
        _facts.AddRange(snapshot.Facts);
        _lessons.Clear();
        _lessons.AddRange(snapshot.Lessons);
        Reset(StagedFacts, snapshot.StagedFacts.Select(f => new StagedFactView(this, f, zone)));
        Reset(BlockUpdates, snapshot.BlockUpdates.Select(u => new BlockUpdateView(this, u, zone)));
        Reset(LessonCandidates, snapshot.PendingLessons.Select(l => new LessonCandidateView(this, l, _lessons)));
        Reset(CoreBlocks, snapshot.Blocks.Select(BlockView));
        _import = snapshot.Import;
        _stats = snapshot.Stats;
        await RefreshFactsAsync(cancellationToken).ConfigureAwait(true);
        RefreshLessons();
        OnPropertyChanged(nameof(ImportTitle));
        OnPropertyChanged(nameof(ImportPath));
        OnPropertyChanged(nameof(ImportNote));
        QueueChanged();
    }

    /// <summary>Picks a tab (a click, or Ctrl+1 to Ctrl+5).</summary>
    public void Select(MemoryTabKind tab)
    {
        if (!SetProperty(ref _selectedTab, tab, nameof(SelectedTab)))
            return;
        foreach (var item in Tabs)
            item.IsSelected = item.Kind == tab;
        OnPropertyChanged(nameof(IsReviewTab));
        OnPropertyChanged(nameof(IsFactsTab));
        OnPropertyChanged(nameof(IsBlocksTab));
        OnPropertyChanged(nameof(IsLessonsTab));
        OnPropertyChanged(nameof(IsImportTab));
        OnPropertyChanged(nameof(ShowsSidePanels));
    }

    // ---- actions the cards call; a failure shows as the outcome, in red, and the card stays ----

    internal async Task ApproveFactAsync(StagedFactView fact, CancellationToken cancellationToken)
    {
        var operation = fact.Operation;
        var text = fact.TextToWrite;
        var corrected = fact.IsCorrected;
        FactWrite write;
        try
        {
            write = await _service.ApproveFactAsync(fact.Id, operation, text, fact.Target?.Fact.FactId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not write “{MemoryText.CleanPlain(text)}”", exception);
            return;
        }

        StagedFacts.Remove(fact);
        if (write.Closed is { } closed)
        {
            Replace(closed);
            // The closed fact no longer holds: no other card may offer it, nor close it again.
            foreach (var other in StagedFacts)
                other.Forget(closed.Id);
        }

        if (write.Written is { } written)
        {
            _facts.Insert(0, written);
            _stats = _stats with { Facts = _stats.Facts + 1 };
        }

        var plain = MemoryText.CleanPlain(text);
        var how = corrected ? "Corrected and written" : "Written";
        SetOutcome(operation switch
        {
            MemoryOperation.Update when write.Closed is { } c => $"{how} as an update: “{plain}”. It replaces “{MemoryText.CleanPlain(c.Text)}”, kept as history.",
            MemoryOperation.Invalidate when write.Closed is { } c => $"Invalidated “{MemoryText.CleanPlain(c.Text)}” from today.",
            MemoryOperation.Noop => $"Nothing written for “{plain}”; the proposal is cleared.",
            _ => $"{how}: “{plain}”.",
        }, Tone.Green);
        QueueChanged();
        await RefreshFactsAsync(cancellationToken).ConfigureAwait(true);
    }

    internal async Task RejectFactAsync(StagedFactView fact, CancellationToken cancellationToken)
    {
        try
        {
            await _service.RejectAsync(fact.Id, "rejected in review", cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not reject “{MemoryText.CleanPlain(fact.Fact.Text)}”", exception);
            return;
        }

        StagedFacts.Remove(fact);
        SetOutcome($"Rejected “{MemoryText.CleanPlain(fact.Fact.Text)}”; the reason is recorded with the proposal.", Tone.Grey);
        QueueChanged();
    }

    internal async Task ApproveBlockAsync(BlockUpdateView update, CancellationToken cancellationToken)
    {
        var replaces = update.IsConsolidated;
        var text = update.TextToWrite;
        CoreBlock block;
        try
        {
            block = await _service.ApproveBlockUpdateAsync(update.Id, text, replaces, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not write the {SafeText.Clean(update.Update.Block)} block", exception);
            return;
        }

        BlockUpdates.Remove(update);
        var index = CoreBlocks.ToList().FindIndex(b => b.Block.Name == block.Name);
        var view = BlockView(block);
        if (index >= 0)
            CoreBlocks[index] = view;
        else
            CoreBlocks.Add(view);
        SetOutcome($"Wrote the {SafeText.Clean(block.Name)} block{(replaces ? ", consolidated" : "")}: {view.CountText} tokens, revision {block.Revision}.", Tone.Green);
        await RestageBlockUpdatesAsync(block.Name, cancellationToken).ConfigureAwait(true);
        QueueChanged();
    }

    internal async Task RejectBlockAsync(BlockUpdateView update, CancellationToken cancellationToken)
    {
        try
        {
            await _service.RejectAsync(update.Id, "rejected in review", cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not reject the {SafeText.Clean(update.Update.Block)} block update", exception);
            return;
        }

        BlockUpdates.Remove(update);
        SetOutcome($"Rejected the {SafeText.Clean(update.Update.Block)} block update; the block stays as it was.", Tone.Grey);
        QueueChanged();
    }

    internal async Task ConsolidateAsync(BlockUpdateView update, CancellationToken cancellationToken)
    {
        BlockConsolidation consolidation;
        try
        {
            consolidation = await _service.ConsolidateAsync(update.Id, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not consolidate the {SafeText.Clean(update.Update.Block)} block", exception);
            return;
        }

        update.ShowConsolidation(consolidation);
    }

    internal Task<int> CountTokensAsync(string text) => _service.CountTokensAsync(text, CancellationToken.None);

    internal async Task ApproveLessonAsync(LessonCandidateView lesson, string? supersedesId, CancellationToken cancellationToken)
    {
        Lesson approved;
        try
        {
            approved = await _service.ApproveLessonAsync(lesson.Id, supersedesId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"Could not approve the lesson “{MemoryText.CleanPlain(lesson.Lesson.Rule)}”", exception);
            return;
        }

        LessonCandidates.Remove(lesson);
        string? replaced = null;
        if (supersedesId is not null)
        {
            var index = _lessons.FindIndex(l => l.Id == supersedesId);
            if (index >= 0)
            {
                replaced = _lessons[index].Rule;
                _lessons[index] = _lessons[index] with { SupersededBy = approved.Rule };
            }
        }

        _lessons.Insert(0, approved);
        RefreshLessons();
        // What the others could supersede changed: the replaced lesson leaves, the approved one joins.
        foreach (var other in LessonCandidates)
            other.Refresh(_lessons);
        var project = SafeText.Clean(approved.Project);
        SetOutcome(replaced is null
            ? $"Lesson approved for project {project}: “{MemoryText.CleanPlain(approved.Rule)}”"
            : $"Lesson approved for project {project}; it supersedes “{MemoryText.CleanPlain(replaced)}”.", Tone.Green);
        QueueChanged();
    }

    internal async Task RejectLessonAsync(LessonCandidateView lesson, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _service.RejectLessonAsync(lesson.Id, reason, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("Could not reject the lesson", exception);
            return;
        }

        LessonCandidates.Remove(lesson);
        SetOutcome($"Lesson rejected: “{SafeText.Clean(reason)}”.", Tone.Grey);
        QueueChanged();
    }

    /// <summary>The evidence link: the events are in Audit.</summary>
    internal void OpenEvidence() => _shell?.Navigate(Area.Audit);

    // ---- search and import ----

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        _searchedFor = _searchText.Trim();
        await RefreshFactsAsync(cancellationToken).ConfigureAwait(true);
        Select(MemoryTabKind.Facts);
    }

    private async Task DryRunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ImportEntry> entries;
        try
        {
            entries = await _service.DryRunImportAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("The dry run failed", exception);
            return;
        }

        Reset(ImportEntries, entries.Select(e => new ImportEntryView(e)));
        HasPreview = false;
        HasPreview = true;
        OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged(nameof(StageLabel));
        StageImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Import for review" on a dry run that refused every entry: nothing to stage, and it says so.</summary>
    private Task NothingToStage()
    {
        SetOutcome("Nothing to stage: the scanner refused every entry of the dry run.", Tone.Grey);
        return Task.CompletedTask;
    }

    /// <summary>Stages exactly what the dry run showed: the entries it passed, by their ids.</summary>
    private async Task StageImportAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = [.. ImportEntries.Where(e => !e.IsRefused).Select(e => e.Entry.Id)];
        IReadOnlyList<StagedFact> staged;
        try
        {
            staged = await _service.ImportForReviewAsync(ids, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("Could not stage the import", exception);
            return;
        }

        var zone = _time.LocalTimeZone;
        foreach (var fact in staged)
            StagedFacts.Add(new StagedFactView(this, fact, zone));
        ImportEntries.Clear();
        HasPreview = false;
        var name = SafeText.Clean(_import?.Name ?? "the import");
        SetOutcome(staged.Count switch
        {
            0 => "Nothing new to stage: these entries are already in the queue, or the source changed since the dry run.",
            _ when staged.Count < ids.Count => $"Staged {staged.Count} of {ids.Count} entries from {name} for review; the rest were staged before or changed since the dry run.",
            1 => $"Staged 1 entry from {name} for review; nothing is written until you approve.",
            _ => $"Staged {staged.Count} entries from {name} for review; nothing is written until you approve.",
        }, Tone.Green);
        QueueChanged();
        Select(MemoryTabKind.Review);
    }

    // ---- state ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea) && _shell!.CurrentArea == Area.Memory)
            ApplyStatus();
    }

    /// <summary>The status bar on this page (Memory.png): the engine and the store's size, no session context.</summary>
    private void ApplyStatus()
    {
        if (_statusBar is not { } bar || _engine is not { } engine)
            return;
        bar.Engine = "";
        bar.Gateway = "";
        bar.Generator = engine.Generator;
        bar.Decider = engine.Decider;
        bar.Context = "";
        bar.AreaFields = [new StatusField("memory", StatsText)];
        bar.Tags = [];
        bar.Scheduler = engine.Scheduler;
        bar.Branch = "";
        bar.Trailing = engine.Trailing;
    }

    private CoreBlockView BlockView(CoreBlock block) => new(block, _time)
    {
        HasPendingUpdate = BlockUpdates.Any(u => u.Update.Block == block.Name),
        OpenReview = _openReview,
    };

    private void QueueChanged()
    {
        Reset(CoreBlocks, CoreBlocks.Select(b => BlockView(b.Block)).ToList());
        Tabs[(int)MemoryTabKind.Review].Count = WaitingCount;
        Tabs[(int)MemoryTabKind.Lessons].Count = LessonCandidates.Count;
        _shell?.SetBadge(Area.Memory, WaitingCount);
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(HasNothingToReview));
        OnPropertyChanged(nameof(HasNoLessonCandidates));
        OnPropertyChanged(nameof(HasStagedFacts));
        OnPropertyChanged(nameof(HasBlockUpdates));
        OnPropertyChanged(nameof(HasLessonCandidates));
        OnPropertyChanged(nameof(StatsText));
        if (_shell?.CurrentArea == Area.Memory)
            ApplyStatus();
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

    private void Replace(FactRecord fact)
    {
        var index = _facts.FindIndex(f => f.Id == fact.Id);
        if (index >= 0)
            _facts.RemoveAt(index);
        _facts.Insert(0, fact);
    }

    /// <summary>
    /// Redraws the recent facts, and the Facts tab by running its search again through the service, so a write never
    /// leaves the tab matching by other rules than a search does. A newer refresh wins over one still in flight.
    /// </summary>
    private async Task RefreshFactsAsync(CancellationToken cancellationToken)
    {
        Reset(RecentFacts, _facts.Take(RecentCount).Select(f => new FactRowView(f)));
        var search = ++_searches;
        var query = _searchedFor;
        IReadOnlyList<FactRecord> found;
        try
        {
            found = await _service.SearchFactsAsync(query, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            if (search == _searches)
                Fail("The facts could not be searched", exception);
            return;
        }

        if (search != _searches)
            return;
        Reset(Facts, found.Select(f => new FactRowView(f)));
        RaiseFacts();
    }

    /// <summary>
    /// A block was written: the other updates staged to it were staged against the old text, so they are read again
    /// (their context and sizes follow the block) and their cards rebuilt.
    /// </summary>
    private async Task RestageBlockUpdatesAsync(string block, CancellationToken cancellationToken)
    {
        if (!BlockUpdates.Any(u => u.Update.Block == block))
            return;
        MemorySnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail($"The {SafeText.Clean(block)} block was written, but the updates still staged to it could not be read again", exception);
            return;
        }

        var zone = _time.LocalTimeZone;
        for (var i = 0; i < BlockUpdates.Count; i++)
        {
            var current = BlockUpdates[i];
            if (current.Update.Block == block && snapshot.BlockUpdates.FirstOrDefault(u => u.Id == current.Id) is { } fresh && fresh != current.Update)
            {
                // Remove and insert rather than replace in place: a moved or replaced item re-templated outside the
                // page's theme (src/Corvid.App/README.md).
                BlockUpdates.RemoveAt(i);
                BlockUpdates.Insert(i, new BlockUpdateView(this, fresh, zone));
            }
        }
    }

    private static bool Failed(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;

    /// <summary>An action failed: the outcome line says what and why, in red; nothing in the queue changes.</summary>
    private void Fail(string what, Exception exception) =>
        SetOutcome($"{what}: {SafeText.Clean(exception.Message)}", Tone.Red);

    private void SetLoadError(string text)
    {
        if (string.Equals(_loadError, text, StringComparison.Ordinal))
            return;
        _loadError = text;
        OnPropertyChanged(nameof(LoadError));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void RaiseFacts()
    {
        OnPropertyChanged(nameof(FactsSummary));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(HasNoFacts));
    }

    private void RefreshLessons() => Reset(Lessons, _lessons.Select(l => new LessonRowView(l)));

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
