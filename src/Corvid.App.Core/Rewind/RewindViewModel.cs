using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Rewind;

/// <summary>
/// The Rewind area (docs/plan/ui.md §4.6, F5; Rewind.png): the selected session's checkpoint ledger as a timeline, newest
/// first; for the selected entry, the plan of going back to before it (the files put back, the person's edits it merges
/// with or stops on, the turns that leave) with a choice of files and conversation, conversation only or files only;
/// a preview of the full diff; and the last restore's result exactly as the kernel reports it.
/// </summary>
public sealed class RewindViewModel : AreaViewModel
{
    private readonly IRewindService _service;
    private readonly ShellViewModel _shell;
    private readonly TimeProvider _time;
    private string? _sessionId;
    private RewindTimeline? _timeline;
    private LedgerEntryView? _selected;
    private string? _preferred;
    private RestorePlan? _plan;
    private RewindScope _scope = RewindScope.FilesAndConversation;
    private bool _previewing;
    private string _error = "";

    public RewindViewModel(IRewindService service, ShellViewModel shell, TimeProvider time)
        : base(Area.Rewind, "The checkpoint ledger as a timeline: preview a restore point, then put back the files, the conversation or both.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _shell = shell;
        _time = time;
        BackToSessionCommand = new RelayCommand(() => _shell.Navigate(Area.Sessions));
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => _plan is not null && _sessionId is not null);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => _plan is not null && _sessionId is not null);
        ClosePreviewCommand = new RelayCommand(() => IsPreviewing = false);
        Options =
        [
            new RestoreOptionView(RewindScope.FilesAndConversation, "Files and conversation", "", new RelayCommand(() => Scope = RewindScope.FilesAndConversation)),
            new RestoreOptionView(RewindScope.ConversationOnly, "Conversation only", "", new RelayCommand(() => Scope = RewindScope.ConversationOnly)),
            new RestoreOptionView(RewindScope.FilesOnly, "Files only", "put the files back, keep the conversation", new RelayCommand(() => Scope = RewindScope.FilesOnly)),
        ];
        Options[0].IsSelected = true;
        _shell.PropertyChanged += OnShellChanged;
        _service.TimelineChanged += OnTimelineChanged;
        SetSession(_shell.CurrentSession?.Id);
    }

    /// <summary>The session whose ledger shows; empty when none is open.</summary>
    public string SessionTitle => _shell.CurrentSession?.Title ?? "";

    /// <summary>"Fix the flaky sandbox test on CI · 8 ledger entries".</summary>
    public string Subtitle => SessionTitle.Length == 0
        ? ""
        : $"{SessionTitle} · {Count(Entries.Count, "ledger entry", "ledger entries")}";

    public bool HasSession => _sessionId is not null;

    public RelayCommand BackToSessionCommand { get; }

    public ObservableCollection<LedgerEntryView> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    public string EmptyText => HasSession
        ? "No checkpoints yet. Every call that changes files takes one, and it shows here."
        : "Open a session to see its checkpoint ledger.";

    public LedgerEntryView? Selected => _selected;

    // ---- The plan ----

    public bool HasPlan => _plan is not null;

    /// <summary>"Select an entry …" while nothing is selected, or the reason a plan could not be made.</summary>
    public string PlanEmptyText => _error.Length > 0 ? _error : "Select an entry in the ledger to see what going back to before it puts back.";

    /// <summary>"Go back to before c-38 · turn 10, 13:59".</summary>
    public string PlanTitle => _plan is { } plan
        ? string.Create(CultureInfo.InvariantCulture, $"Go back to before {plan.CheckpointId} · turn {plan.Turn}, {Local(plan.At):HH:mm}")
        : "";

    /// <summary>"/rollback 4": the same restore from the composer.</summary>
    public string PlanCommand => _plan?.Command ?? "";

    public ObservableCollection<PutBackFileView> PlanFiles { get; } = [];

    public string PlanFileCount => PlanFiles.Count.ToString(CultureInfo.InvariantCulture);

    public bool HasPlanFiles => PlanFiles.Count > 0;

    public ObservableCollection<RestoreNoteView> PlanNotes { get; } = [];

    public bool HasPlanNotes => PlanNotes.Count > 0;

    /// <summary>"turns 10 to 12".</summary>
    public string ConversationRange => _plan is { TurnsLeaving: > 0 } plan ? ConversationRangeOf(plan) : "no turns";

    /// <summary>"Three turns leave the transcript. They stay in the log and in Audit, and the fork tree keeps a branch for them."</summary>
    public string ConversationText => _plan switch
    {
        null => "",
        { TurnsLeaving: 0 } => "No turns leave the transcript.",
        { TurnsLeaving: 1 } => "One turn leaves the transcript. It stays in the log and in Audit, and the fork tree keeps a branch for it.",
        var plan => $"{Word(plan.TurnsLeaving)} turns leave the transcript. They stay in the log and in Audit, and the fork tree keeps a branch for them.",
    };

    public IReadOnlyList<RestoreOptionView> Options { get; }

    public RewindScope Scope
    {
        get => _scope;
        set
        {
            if (!SetProperty(ref _scope, value))
                return;
            foreach (var option in Options)
                option.IsSelected = option.Scope == value;
        }
    }

    public AsyncRelayCommand RestoreCommand { get; }

    public AsyncRelayCommand PreviewCommand { get; }

    // ---- The preview ----

    /// <summary>The full diff of the selected restore shows in place of the plan.</summary>
    public bool IsPreviewing
    {
        get => _previewing;
        private set
        {
            if (SetProperty(ref _previewing, value))
                OnPropertyChanged(nameof(ShowPlan));
        }
    }

    public bool ShowPlan => !_previewing;

    public ObservableCollection<PreviewFileView> PreviewFiles { get; } = [];

    /// <summary>"Preview · back to before c-38".</summary>
    public string PreviewTitle => _plan is { } plan ? $"Preview · going back to before {plan.CheckpointId}" : "Preview";

    public RelayCommand ClosePreviewCommand { get; }

    // ---- The last restore ----

    public RestoreOutcome? LastRestore => _timeline?.LastRestore;

    public bool HasLastRestore => LastRestore is not null;

    /// <summary>"Last restore · 13:41 · undo of turn 8".</summary>
    public string LastRestoreTitle => LastRestore is { } last
        ? string.Create(CultureInfo.InvariantCulture, $"Last restore · {Local(last.At):HH:mm} · {last.Label}")
        : "";

    public string LastRestoreTag => LastRestore?.Refused == true ? "refused" : "done";

    public Tone LastRestoreTone => LastRestore?.Refused == true ? Tone.Red : Tone.Green;

    /// <summary>"restored 2 · merged 1 · refused 0 · restore checkpoint c-35", or the refusal's reason.</summary>
    public string LastRestoreSummary => LastRestore switch
    {
        null => "",
        { Refused: true } last => $"refused {last.Conflicts.Length} · nothing was restored or overwritten",
        var last => string.Create(CultureInfo.InvariantCulture,
            $"restored {last.Restored.Length} · merged {last.Merged.Length} · refused {last.Conflicts.Length} · restore checkpoint {last.CheckpointId}"),
    };

    public bool LastRestoreRefused => LastRestore?.Refused == true;

    public bool LastRestoreDone => LastRestore is { Refused: false };

    /// <summary>The paths of the last restore as <c>RestoreResult</c> reports them.</summary>
    public IReadOnlyList<OutcomeFileView> LastRestoreFiles => LastRestore is { } last
        ?
        [
            .. last.Restored.Select(path => new OutcomeFileView(path, "restored", false)),
            .. last.Merged.Select(path => new OutcomeFileView(path, "merged with your edit", false)),
            .. last.Conflicts.Select(conflict => new OutcomeFileView(conflict.Path, $"refused: {conflict.Reason}", true)),
        ]
        : [];

    /// <summary>The entry to select when the ledger next loads (the demo's moment selects 13:59, c-38).</summary>
    public void PreferEntry(string checkpointId)
    {
        _preferred = checkpointId;
        if (Entries.FirstOrDefault(entry => entry.CheckpointId == checkpointId) is { } found)
            Select(found);
    }

    /// <summary>Moves the selection up or down the ledger, over entries that cannot be selected.</summary>
    public LedgerEntryView? MoveSelection(int delta)
    {
        if (Entries.Count == 0)
            return null;
        var index = _selected is null ? (delta > 0 ? -1 : Entries.Count) : Entries.IndexOf(_selected);
        for (var next = index + delta; next >= 0 && next < Entries.Count; next += delta)
        {
            if (!Entries[next].IsSelectable)
                continue;
            Select(Entries[next]);
            return Entries[next];
        }

        return _selected;
    }

    /// <summary>Loads the ledger again for the current session, keeping the selection where it can.</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var sessionId = _sessionId;
        if (sessionId is null)
        {
            Apply(null);
            return;
        }

        try
        {
            var timeline = await _service.GetTimelineAsync(sessionId).ConfigureAwait(true);
            if (sessionId == _sessionId)
                Apply(timeline);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            Apply(null);
        }
    }

    private void Apply(RewindTimeline? timeline)
    {
        _timeline = timeline;
        var keep = _selected?.CheckpointId ?? _preferred;
        Entries.Clear();
        foreach (var entry in timeline?.Entries ?? [])
        {
            LedgerEntryView? view = null;
            view = new LedgerEntryView(entry, _time.LocalTimeZone, new RelayCommand(() => Select(view!)));
            Entries.Add(view);
        }

        _selected = null;
        var next = Entries.FirstOrDefault(entry => entry.CheckpointId == keep && entry.IsSelectable);
        Select(next, force: true);
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(LastRestore));
        OnPropertyChanged(nameof(HasLastRestore));
        OnPropertyChanged(nameof(LastRestoreTitle));
        OnPropertyChanged(nameof(LastRestoreTag));
        OnPropertyChanged(nameof(LastRestoreTone));
        OnPropertyChanged(nameof(LastRestoreSummary));
        OnPropertyChanged(nameof(LastRestoreRefused));
        OnPropertyChanged(nameof(LastRestoreDone));
        OnPropertyChanged(nameof(LastRestoreFiles));
    }

    private void Select(LedgerEntryView? entry, bool force = false)
    {
        if (entry is { IsSelectable: false })
            return;
        if (!force && ReferenceEquals(entry, _selected))
            return;
        if (_selected is not null)
            _selected.IsSelected = false;
        _selected = entry;
        if (entry is not null)
        {
            entry.IsSelected = true;
            _preferred = entry.CheckpointId;
        }

        OnPropertyChanged(nameof(Selected));
        _ = PlanAsync(entry);
    }

    private async Task PlanAsync(LedgerEntryView? entry)
    {
        RestorePlan? plan = null;
        _error = "";
        if (entry is not null && _sessionId is { } sessionId)
        {
            try
            {
                plan = await _service.PlanAsync(sessionId, entry.CheckpointId).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
            {
                _error = exception.Message;
            }

            if (!ReferenceEquals(entry, _selected))
                return;
        }

        SetPlan(plan);
    }

    private void SetPlan(RestorePlan? plan)
    {
        var samePlan = _plan?.CheckpointId == plan?.CheckpointId;
        _plan = plan;
        if (!samePlan)
            IsPreviewing = false;
        PlanFiles.Clear();
        PlanNotes.Clear();
        foreach (var file in plan?.Files ?? [])
        {
            PlanFiles.Add(new PutBackFileView(file, plan!.CheckpointId));
            if (file.Edit is { } edit)
            {
                var name = Path.GetFileName(file.Path);
                PlanNotes.Add(file.Kind == PutBackKind.Conflict
                    ? new RestoreNoteView(name, string.Create(CultureInfo.InvariantCulture,
                        $"was edited by you at {Local(edit.At):HH:mm}, after {edit.AfterCheckpoint}, and going back to {plan.CheckpointId} would delete it. A restore of the files stops here and changes nothing."), Tone.Red)
                    : new RestoreNoteView(name, string.Create(CultureInfo.InvariantCulture,
                        $"was edited by you at {Local(edit.At):HH:mm}, after {edit.AfterCheckpoint}. The restore merges three ways and stops on a conflict rather than overwrite your edit."), Tone.Amber));
            }
        }

        if (plan is not null)
        {
            Options[0].Detail = $"the workspace and the transcript as they were at {plan.CheckpointId}";
            Options[1].Detail = plan.TurnsLeaving > 0 ? $"keep the files, drop {ConversationRangeOf(plan)} from the context" : "keep the files; no turns leave";
        }

        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanEmptyText));
        OnPropertyChanged(nameof(PlanTitle));
        OnPropertyChanged(nameof(PlanCommand));
        OnPropertyChanged(nameof(PlanFileCount));
        OnPropertyChanged(nameof(HasPlanFiles));
        OnPropertyChanged(nameof(HasPlanNotes));
        OnPropertyChanged(nameof(ConversationRange));
        OnPropertyChanged(nameof(ConversationText));
        OnPropertyChanged(nameof(PreviewTitle));
        RestoreCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
    }

    private async Task PreviewAsync()
    {
        if (_plan is not { } plan || _sessionId is not { } sessionId)
            return;
        var files = await _service.PreviewAsync(sessionId, plan.CheckpointId).ConfigureAwait(true);
        PreviewFiles.Clear();
        foreach (var file in files)
            PreviewFiles.Add(new PreviewFileView(file));
        IsPreviewing = true;
    }

    private async Task RestoreAsync()
    {
        if (_plan is not { } plan || _sessionId is not { } sessionId)
            return;
        var outcome = await _service.RestoreAsync(sessionId, plan.CheckpointId, _scope).ConfigureAwait(true);
        if (outcome.Refused)
        {
            // Nothing changed: the plan stays, with the refusal in the last restore panel below it.
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        // The session went back; the old plan's turns are gone, so nothing stays selected.
        _preferred = null;
        if (_selected is not null)
            _selected.IsSelected = false;
        _selected = null;
        IsPreviewing = false;
        await RefreshAsync().ConfigureAwait(true);
    }

    private void SetSession(string? sessionId)
    {
        if (sessionId == _sessionId && _timeline is not null)
            return;
        if (_sessionId is not null && sessionId != _sessionId)
        {
            _preferred = null;
            _selected = null;
        }

        _sessionId = sessionId;
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(HasSession));
        Refresh();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentSession))
            SetSession(_shell.CurrentSession?.Id);
    }

    private void OnTimelineChanged(object? sender, string sessionId)
    {
        if (sessionId == _sessionId)
            Refresh();
    }

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);

    private static string ConversationRangeOf(RestorePlan plan) => plan.FromTurn == plan.ToTurn
        ? string.Create(CultureInfo.InvariantCulture, $"turn {plan.FromTurn}")
        : string.Create(CultureInfo.InvariantCulture, $"turns {plan.FromTurn} to {plan.ToTurn}");

    private static string Count(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

    /// <summary>The number as a word, capitalised, up to twelve: "Three turns leave".</summary>
    private static string Word(int number) => number switch
    {
        2 => "Two", 3 => "Three", 4 => "Four", 5 => "Five", 6 => "Six", 7 => "Seven", 8 => "Eight", 9 => "Nine", 10 => "Ten",
        11 => "Eleven", 12 => "Twelve",
        _ => number.ToString(CultureInfo.InvariantCulture),
    };
}
