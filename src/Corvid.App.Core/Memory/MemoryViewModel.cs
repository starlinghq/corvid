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
        ImportForReviewCommand = new AsyncRelayCommand(ct => HasPreview ? StageImportAsync(ct) : DryRunAsync(ct));
        StageImportCommand = new AsyncRelayCommand(StageImportAsync, () => ImportEntries.Any(e => !e.IsRefused));
        CancelImportCommand = new RelayCommand(() => { ImportEntries.Clear(); HasPreview = false; });
        DismissOutcomeCommand = new RelayCommand(() => SetOutcome("", Tone.Grey));
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellChanged;
            if (_shell.CurrentArea == Area.Memory)
                ApplyStatus();
        }

        _ = LoadAsync(CancellationToken.None);
    }

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

    /// <summary>Staged facts and block updates waiting: the Review tab's and the rail's count.</summary>
    public int ReviewCount => StagedFacts.Count + BlockUpdates.Count;

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

    /// <summary>"Dry run: 3 would be staged, 2 refused. Nothing is staged yet."</summary>
    public string PreviewSummary
    {
        get
        {
            var passed = ImportEntries.Count(e => !e.IsRefused);
            var refused = ImportEntries.Count - passed;
            return $"Dry run: {passed} would be staged, {refused} refused. Nothing is staged yet.";
        }
    }

    public string StageLabel => $"Stage {ImportEntries.Count(e => !e.IsRefused)} for review";

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

    /// <summary>Reads the store again and redraws the page.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        MemorySnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _loadError = "Could not read memory: " + exception.Message;
            OnPropertyChanged(nameof(LoadError));
            OnPropertyChanged(nameof(HasLoadError));
            return;
        }

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
        RefreshFacts();
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

    // ---- actions the cards call ----

    internal async Task ApproveFactAsync(StagedFactView fact, CancellationToken cancellationToken)
    {
        var operation = fact.Operation;
        var text = fact.TextToWrite;
        var corrected = fact.IsCorrected;
        var write = await _service.ApproveFactAsync(fact.Id, operation, text, fact.Target?.Fact.FactId, cancellationToken).ConfigureAwait(true);
        StagedFacts.Remove(fact);
        if (write.Closed is { } closed)
            Replace(closed);
        if (write.Written is { } written)
        {
            _facts.Insert(0, written);
            _stats = _stats with { Facts = _stats.Facts + 1 };
        }

        RefreshFacts();
        var plain = MemoryText.Plain(text);
        var how = corrected ? "Corrected and written" : "Written";
        SetOutcome(operation switch
        {
            MemoryOperation.Update when write.Closed is { } c => $"{how} as an update: “{plain}”. It replaces “{MemoryText.Plain(c.Text)}”, kept as history.",
            MemoryOperation.Invalidate when write.Closed is { } c => $"Invalidated “{MemoryText.Plain(c.Text)}” from today.",
            MemoryOperation.Noop => $"Nothing written for “{plain}”; the proposal is cleared.",
            _ => $"{how}: “{plain}”.",
        }, Tone.Green);
        QueueChanged();
    }

    internal async Task RejectFactAsync(StagedFactView fact, CancellationToken cancellationToken)
    {
        await _service.RejectAsync(fact.Id, "rejected in review", cancellationToken).ConfigureAwait(true);
        StagedFacts.Remove(fact);
        SetOutcome($"Rejected “{MemoryText.Plain(fact.Fact.Text)}”; the reason is recorded with the proposal.", Tone.Grey);
        QueueChanged();
    }

    internal async Task ApproveBlockAsync(BlockUpdateView update, CancellationToken cancellationToken)
    {
        var replaces = update.IsConsolidated;
        var text = update.IsEditing ? update.EditText : update.Added;
        var block = await _service.ApproveBlockUpdateAsync(update.Id, text, replaces, cancellationToken).ConfigureAwait(true);
        BlockUpdates.Remove(update);
        var index = CoreBlocks.ToList().FindIndex(b => b.Name == block.Name);
        var view = BlockView(block);
        if (index >= 0)
            CoreBlocks[index] = view;
        else
            CoreBlocks.Add(view);
        SetOutcome($"Wrote the {block.Name} block{(replaces ? ", consolidated" : "")}: {view.CountText} tokens, revision {block.Revision}.", Tone.Green);
        QueueChanged();
    }

    internal async Task RejectBlockAsync(BlockUpdateView update, CancellationToken cancellationToken)
    {
        await _service.RejectAsync(update.Id, "rejected in review", cancellationToken).ConfigureAwait(true);
        BlockUpdates.Remove(update);
        SetOutcome($"Rejected the {update.Update.Block} block update; the block stays as it was.", Tone.Grey);
        QueueChanged();
    }

    internal async Task ConsolidateAsync(BlockUpdateView update, CancellationToken cancellationToken)
    {
        var consolidation = await _service.ConsolidateAsync(update.Id, cancellationToken).ConfigureAwait(true);
        update.ShowConsolidation(consolidation);
    }

    internal Task<int> CountTokensAsync(string text) => _service.CountTokensAsync(text, CancellationToken.None);

    internal async Task ApproveLessonAsync(LessonCandidateView lesson, string? supersedesId, CancellationToken cancellationToken)
    {
        var approved = await _service.ApproveLessonAsync(lesson.Id, supersedesId, cancellationToken).ConfigureAwait(true);
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
        SetOutcome(replaced is null
            ? $"Lesson approved for project {approved.Project}: “{approved.Rule}”"
            : $"Lesson approved for project {approved.Project}; it supersedes “{MemoryText.Plain(replaced)}”.", Tone.Green);
        QueueChanged();
    }

    internal async Task RejectLessonAsync(LessonCandidateView lesson, string reason, CancellationToken cancellationToken)
    {
        await _service.RejectLessonAsync(lesson.Id, reason, cancellationToken).ConfigureAwait(true);
        LessonCandidates.Remove(lesson);
        SetOutcome($"Lesson rejected: “{reason}”.", Tone.Grey);
        QueueChanged();
    }

    /// <summary>The evidence link: the events are in Audit.</summary>
    internal void OpenEvidence() => _shell?.Navigate(Area.Audit);

    // ---- search and import ----

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var query = _searchText.Trim();
        var found = await _service.SearchFactsAsync(query, cancellationToken).ConfigureAwait(true);
        _searchedFor = query;
        Reset(Facts, found.Select(f => new FactRowView(f)));
        RaiseFacts();
        Select(MemoryTabKind.Facts);
    }

    private async Task DryRunAsync(CancellationToken cancellationToken)
    {
        var entries = await _service.DryRunImportAsync(cancellationToken).ConfigureAwait(true);
        Reset(ImportEntries, entries.Select(e => new ImportEntryView(e)));
        HasPreview = false;
        HasPreview = true;
        OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged(nameof(StageLabel));
    }

    private async Task StageImportAsync(CancellationToken cancellationToken)
    {
        var staged = await _service.ImportForReviewAsync(cancellationToken).ConfigureAwait(true);
        var zone = _time.LocalTimeZone;
        foreach (var fact in staged)
            StagedFacts.Add(new StagedFactView(this, fact, zone));
        ImportEntries.Clear();
        HasPreview = false;
        SetOutcome(staged.Count == 0
            ? "Nothing new to stage: this import is already in the queue."
            : $"Staged {staged.Count} {(staged.Count == 1 ? "entry" : "entries")} from {_import?.Name ?? "the import"} for review; nothing is written until you approve.", Tone.Green);
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
        Tabs[(int)MemoryTabKind.Review].Count = ReviewCount;
        Tabs[(int)MemoryTabKind.Lessons].Count = LessonCandidates.Count;
        _shell?.SetBadge(Area.Memory, ReviewCount);
        OnPropertyChanged(nameof(ReviewCount));
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
    }

    private void Replace(FactRecord fact)
    {
        var index = _facts.FindIndex(f => f.Id == fact.Id);
        if (index >= 0)
            _facts.RemoveAt(index);
        _facts.Insert(0, fact);
    }

    private void RefreshFacts()
    {
        Reset(RecentFacts, _facts.Take(RecentCount).Select(f => new FactRowView(f)));
        var words = _searchedFor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Reset(Facts, _facts.Where(f => words.All(w => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(f => new FactRowView(f)));
        RaiseFacts();
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
