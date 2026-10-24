using System.Collections.ObjectModel;
using System.ComponentModel;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Jobs;

/// <summary>
/// The Jobs area (docs/plan/ui.md §4.10, F13, F23): the scheduled jobs with schedule, next run, state and last outcome;
/// the selected job's detail with pause, resume and remove; its run history, where a run opens its session read-only; the
/// scheduler's state with what it means; and the create form, whose schedule is previewed as "runs next at" before
/// anything is written. Text from a job (its name, prompt and script may come from the model) is cleaned for display; a
/// draft holding hidden characters cannot be created. While the area shows, it writes the status bar's scheduler item.
/// </summary>
public sealed class JobsViewModel : AreaViewModel, IDisposable
{
    private readonly IJobsService _service;
    private readonly ShellViewModel _shell;
    private readonly StatusBarLease _lease;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, JobRowViewModel> _rows = new(StringComparer.Ordinal);
    private JobsSnapshot? _snapshot;
    private JobRowViewModel? _selected;
    private string? _highlightSession;
    private bool _active;
    private bool _disposed;
    private int _previewSequence;

    private IReadOnlyList<TextRun> _subtitle = [];
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private IReadOnlyList<ModelFact> _detail = [];
    private IReadOnlyList<StatusTag> _detailTags = [];
    private bool _confirmingRemove;
    private string _schedulerTag = "";
    private Tone _schedulerTone = Tone.Green;
    private IReadOnlyList<TextRun> _schedulerText = [];
    private bool _creating;
    private string _draftName = "";
    private string _draftSchedule = "";
    private string _draftPrompt = "";
    private string _draftScript = "";
    private bool _draftNoAgent;
    private string _draftDeliver = "";
    private SchedulePreview? _preview;
    private IReadOnlyList<TextRun> _previewText = [];
    private string _draftWarning = "";
    private IReadOnlyList<Choice> _destinations = [];

    public JobsViewModel(IJobsService service, ShellViewModel shell, StatusBarViewModel statusBar, TimeProvider time)
        : base(Area.Jobs, "Scheduled jobs with their next run and history, and the scheduler's state.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _shell = shell;
        _time = time;
        _lease = new StatusBarLease(statusBar);

        PauseCommand = new AsyncRelayCommand(PauseAsync, () => _selected?.Record.State == JobState.Active);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => _selected?.Record.State == JobState.Paused);
        AskRemoveCommand = new RelayCommand(() => IsConfirmingRemove = true, () => _selected is not null);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => _selected is not null);
        CancelRemoveCommand = new RelayCommand(() => IsConfirmingRemove = false);
        NewJobCommand = new RelayCommand(OpenForm);
        CancelNewCommand = new RelayCommand(CloseForm);
        CreateCommand = new AsyncRelayCommand(CreateAsync, CanCreate);
        DismissNoticeCommand = new RelayCommand(() => Say(true, ""));
        foreach (var command in new[] { PauseCommand, ResumeCommand, RemoveCommand, CreateCommand })
            ReportFailures(command);

        _service.Changed += OnServiceChanged;
        _shell.PropertyChanged += OnShellChanged;
        _ = RefreshAsync();
        UpdateActive();
    }

    public IReadOnlyList<TextRun> Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    /// <summary>The jobs, active first, then by next run; removed ones leave.</summary>
    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    public bool HasJobs => Jobs.Count > 0;

    public string EmptyText => "No jobs yet. New job… schedules one; in a chat, ask Corvid to (cron_create asks you first).";

    public JobRowViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (ReferenceEquals(_selected, value))
                return;
            if (_selected is not null)
                _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null)
                _selected.IsSelected = true;
            IsConfirmingRemove = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            ShowDetail();
            _ = LoadRunsAsync();
        }
    }

    public bool HasSelection => _selected is not null;

    // ---- The selected job ----

    public string DetailTitle => _selected?.Name ?? "";

    public IReadOnlyList<StatusTag> DetailTags { get => _detailTags; private set => SetProperty(ref _detailTags, value); }

    public IReadOnlyList<ModelFact> Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public bool CanPause => _selected?.Record.State == JobState.Active;

    public bool CanResume => _selected?.Record.State == JobState.Paused;

    public AsyncRelayCommand PauseCommand { get; }

    public AsyncRelayCommand ResumeCommand { get; }

    /// <summary>Remove… asks first, in the panel: what it means is said before it happens.</summary>
    public RelayCommand AskRemoveCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    public bool IsConfirmingRemove
    {
        get => _confirmingRemove;
        private set
        {
            if (SetProperty(ref _confirmingRemove, value))
                OnPropertyChanged(nameof(RemoveQuestion));
        }
    }

    public IReadOnlyList<TextRun> RemoveQuestion => Spans.Parse(_selected is null ? ""
        : $"Remove **{Spans.Escape(_selected.Name)}**? It will not run again; its runs stay recorded.");

    // ---- Run history ----

    public ObservableCollection<JobRunRowViewModel> Runs { get; } = [];

    public string RunsTitle => _selected is null ? "Runs" : $"Runs · {_selected.Name}";

    public string RunsNote => $"newest first · at most {JobsText.RunsShown} · a run opens read-only";

    public bool HasRuns => Runs.Count > 0;

    public string NoRunsText => _selected is null ? "Pick a job to see its runs." : $"{_selected.Name} has not run yet.";

    // ---- The scheduler ----

    public string SchedulerTag { get => _schedulerTag; private set => SetProperty(ref _schedulerTag, value); }

    public Tone SchedulerTone { get => _schedulerTone; private set => SetProperty(ref _schedulerTone, value); }

    public IReadOnlyList<TextRun> SchedulerText { get => _schedulerText; private set => SetProperty(ref _schedulerText, value); }

    // ---- The create form ----

    public bool IsCreating { get => _creating; private set => SetProperty(ref _creating, value); }

    public RelayCommand NewJobCommand { get; }

    public RelayCommand CancelNewCommand { get; }

    public AsyncRelayCommand CreateCommand { get; }

    public string DraftName { get => _draftName; set { if (SetProperty(ref _draftName, value ?? "")) DraftChanged(); } }

    public string DraftSchedule
    {
        get => _draftSchedule;
        set
        {
            if (SetProperty(ref _draftSchedule, value ?? ""))
                _ = PreviewAsync();
        }
    }

    public string DraftPrompt { get => _draftPrompt; set { if (SetProperty(ref _draftPrompt, value ?? "")) DraftChanged(); } }

    public string DraftScript { get => _draftScript; set { if (SetProperty(ref _draftScript, value ?? "")) DraftChanged(); } }

    /// <summary>A no-agent job runs only its script, with no model turn.</summary>
    public bool DraftNoAgent
    {
        get => _draftNoAgent;
        set
        {
            if (!SetProperty(ref _draftNoAgent, value))
                return;
            OnPropertyChanged(nameof(CanEditPrompt));
            DraftChanged();
        }
    }

    /// <summary>A no-agent job has no prompt to write.</summary>
    public bool CanEditPrompt => !_draftNoAgent;

    /// <summary>Where the result goes: one of the snapshot's destinations.</summary>
    public IReadOnlyList<Choice> Destinations { get => _destinations; private set => SetProperty(ref _destinations, value); }

    public string DraftDeliver => _draftDeliver;

    /// <summary>"runs next at tomorrow 09:00, then Mon 09:00 and Tue 09:00 · cron 0 9 * * 1-5", or why it cannot be read.</summary>
    public IReadOnlyList<TextRun> PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }

    public bool PreviewOk => _preview?.Valid == true;

    /// <summary>The preview's warning sign: a schedule was typed and does not read. An empty field shows only the hint.</summary>
    public bool PreviewWarns => !PreviewOk && _draftSchedule.Trim().Length > 0;

    /// <summary>A draft field holds hidden characters, shown revealed: creating waits until they are gone.</summary>
    public string DraftWarning { get => _draftWarning; private set { if (SetProperty(ref _draftWarning, value)) OnPropertyChanged(nameof(HasDraftWarning)); } }

    public bool HasDraftWarning => _draftWarning.Length > 0;

    // ---- Notice ----

    public IReadOnlyList<TextRun> Notice { get => _notice; private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }

    public bool HasNotice => _notice.Count > 0;

    public Tone NoticeTone { get => _noticeTone; private set => SetProperty(ref _noticeTone, value); }

    public RelayCommand DismissNoticeCommand { get; }

    // ---- Keys ----

    /// <summary>Up and Down on the page: the previous or next job.</summary>
    public void MoveSelection(int delta)
    {
        if (Jobs.Count == 0)
            return;
        var index = _selected is null ? (delta > 0 ? -1 : Jobs.Count) : Jobs.IndexOf(_selected);
        Selected = Jobs[Math.Clamp(index + delta, 0, Jobs.Count - 1)];
    }

    /// <summary>Esc: closes the remove question, then the form. False when there was nothing to close.</summary>
    public bool Close()
    {
        if (IsConfirmingRemove)
        {
            IsConfirmingRemove = false;
            return true;
        }

        if (IsCreating)
        {
            CloseForm();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Changed -= OnServiceChanged;
        _shell.PropertyChanged -= OnShellChanged;
        _lease.Release();
    }

    // ---- Loading ----

    private void OnServiceChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say(false, $"The jobs could not be loaded: {Spans.Escape(exception.Message)}");
            return;
        }

        if (_disposed)
            return;
        Sync(_snapshot);
        ShowScheduler(_snapshot.Scheduler);
        if (_destinations.Count == 0)
            Destinations = [.. _snapshot.Destinations.Select(destination =>
                new Choice(SafeText.Clean(destination), $"Deliver the result to {SafeText.Clean(destination)}", new RelayCommand(() => Deliver(destination))))];
        Subtitle = Spans.Parse(SubtitleText(_snapshot));
        if (_active)
            ApplyStatus();
        if (_selected is null || !_rows.ContainsKey(_selected.Id))
            Selected = Landing();
        else
        {
            ShowDetail();
            _ = LoadRunsAsync();
        }
    }

    /// <summary>Rebuilds the rows once per change: rows keep their identity by id, in the table's order.</summary>
    private void Sync(JobsSnapshot snapshot)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var wanted = new List<JobRowViewModel>();
        foreach (var job in snapshot.Jobs.Where(job => job.State != JobState.Removed)
                     .OrderBy(job => job.State == JobState.Active ? 0 : 1).ThenBy(job => job.NextRun ?? DateTimeOffset.MaxValue))
        {
            if (!_rows.TryGetValue(job.Id, out var row))
                _rows[job.Id] = row = new JobRowViewModel(job, select => Selected = select);
            row.Update(job, now, zone);
            wanted.Add(row);
        }

        foreach (var gone in _rows.Keys.Except(wanted.Select(row => row.Id)).ToList())
            _rows.Remove(gone);
        if (!wanted.SequenceEqual(Jobs))
        {
            Jobs.Clear();
            foreach (var row in wanted)
                Jobs.Add(row);
        }

        OnPropertyChanged(nameof(HasJobs));
    }

    /// <summary>Where the page lands: the job a link named a run of, or the job that ran last (the Board's finished job).</summary>
    private JobRowViewModel? Landing()
    {
        if (_highlightSession is { } session && _rows.Values.FirstOrDefault(row => row.Record.LastRun?.SessionId == session) is { } linked)
            return linked;
        return Jobs.Where(row => row.Record.LastRun is not null).OrderByDescending(row => row.Record.LastRun!.StartedAt).FirstOrDefault()
            ?? Jobs.FirstOrDefault();
    }

    private async Task LoadRunsAsync()
    {
        var job = _selected;
        IReadOnlyList<JobRunRecord> runs = [];
        if (job is not null)
        {
            try
            {
                runs = await _service.RunsAsync(job.Id, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Say(false, $"The runs could not be loaded: {Spans.Escape(exception.Message)}");
            }
        }

        if (!ReferenceEquals(job, _selected) || _disposed)
            return;
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        Runs.Clear();
        foreach (var run in runs.Take(JobsText.RunsShown))
        {
            var row = new JobRunRowViewModel(run, now, zone, _shell.FindSession(run.SessionId) is not null, OpenRun)
            {
                IsHighlighted = _highlightSession is { } session && run.SessionId == session,
            };
            Runs.Add(row);
        }

        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(RunsTitle));
        OnPropertyChanged(nameof(NoRunsText));
    }

    private void ShowDetail()
    {
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        AskRemoveCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        if (_selected is not { Record: var job })
        {
            Detail = [];
            DetailTags = [];
            return;
        }

        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var facts = new List<ModelFact>
        {
            Fact("schedule", $"{Spans.Escape(SafeText.Clean(job.Schedule))} ~· {Spans.Escape(SafeText.Clean(job.Parsed))} · {Spans.Escape(SafeText.Clean(job.Zone))}~"),
            Fact("next run", job.NextRun is { } next ? JobsText.When(next, now, zone)
                : job.State == JobState.Paused ? "none while paused" : "none: its schedule has no run left"),
        };
        if (!job.NoAgent)
            facts.Add(Fact("prompt", Spans.Escape(SafeText.Clean(job.Prompt))));
        if (job.Script is { } script)
            facts.Add(Fact(job.NoAgent ? "runs" : "pre-run", $"`{Spans.Escape(SafeText.Clean(script))}`"));
        facts.Add(Fact("delivers to", Spans.Escape(SafeText.Clean(job.Deliver))));
        facts.Add(Fact("created by", Spans.Escape(SafeText.Clean(job.CreatedBy))));
        if (job.CreatedTainted)
            facts.Add(Fact("taint", "made in a tainted session: each run starts tainted, so network egress asks"));
        Detail = facts;
        DetailTags = JobTags.Of(job);
        OnPropertyChanged(nameof(RemoveQuestion));
    }

    private static ModelFact Fact(string key, string markup) => new(key, Spans.Parse(markup));

    private void ShowScheduler(SchedulerStatus status)
    {
        var since = status.Since is { } at ? " since " + JobsText.When(at, _time.GetUtcNow(), _time.LocalTimeZone) : "";
        (SchedulerTag, SchedulerTone, var text) = status.Host switch
        {
            SchedulerHost.Here => ("running here", Tone.Green,
                $"This app holds the scheduler's lock{since}, so jobs run here while it is open. When it closes, another process "
                + "that runs the scheduler (`corvid serve`) takes over within its poll interval."),
            SchedulerHost.Elsewhere => ("held elsewhere", Tone.Amber,
                $"**{Spans.Escape(SafeText.Clean(status.Holder))}** holds the scheduler's lock{since}, so jobs run there, not here. "
                + "Pause, resume and remove still work: it picks the change up within its poll interval."),
            _ => ("off", Tone.Grey,
                "No process runs the scheduler, so no job runs. Start `corvid serve`, or let this app run it from Settings."),
        };
        SchedulerText = Spans.Parse(text);
    }

    private static string SubtitleText(JobsSnapshot snapshot)
    {
        var count = snapshot.Jobs.Count(job => job.State is JobState.Active or JobState.Paused);
        var where = snapshot.Scheduler.Host switch
        {
            SchedulerHost.Here => "scheduler running here",
            SchedulerHost.Elsewhere => $"scheduler held by {Spans.Escape(SafeText.Clean(snapshot.Scheduler.Holder))}",
            _ => "scheduler off",
        };
        return $"{where} · {count} job{(count == 1 ? "" : "s")} · times in local time";
    }

    // ---- Actions ----

    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } row)
            return;
        var job = await _service.PauseAsync(row.Id, cancellationToken).ConfigureAwait(true);
        Say(true, $"Paused **{Spans.Escape(SafeText.Clean(job.Name))}**: it will not run until you resume it.");
    }

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } row)
            return;
        var job = await _service.ResumeAsync(row.Id, cancellationToken).ConfigureAwait(true);
        var name = Spans.Escape(SafeText.Clean(job.Name));
        Say(true, job.NextRun is { } next
            ? $"Resumed **{name}**: next run {JobsText.When(next, _time.GetUtcNow(), _time.LocalTimeZone)}."
            : $"Resumed **{name}**: its schedule has no run left.");
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } row)
            return;
        var name = Spans.Escape(row.Name);
        IsConfirmingRemove = false;
        await _service.RemoveAsync(row.Id, cancellationToken).ConfigureAwait(true);
        Say(true, $"Removed **{name}**: it will not run again. Its runs stay recorded.");
    }

    private void OpenRun(JobRunRowViewModel run)
    {
        if (!_shell.OpenSession(run.Run.SessionId))
            Say(false, $"The run's session `{Spans.Escape(run.SessionId)}` is not in the session list.");
    }

    // ---- The form ----

    private void OpenForm()
    {
        DraftName = "";
        DraftSchedule = "";
        DraftPrompt = "";
        DraftScript = "";
        DraftNoAgent = false;
        if (_destinations.Count > 0)
            Deliver(_snapshot!.Destinations[0]);
        IsCreating = true;
        _ = PreviewAsync();
    }

    private void CloseForm() => IsCreating = false;

    private void Deliver(string destination)
    {
        _draftDeliver = destination;
        foreach (var choice in _destinations)
            choice.IsSelected = choice.Label == SafeText.Clean(destination);
        OnPropertyChanged(nameof(DraftDeliver));
    }

    private async Task PreviewAsync()
    {
        var sequence = ++_previewSequence;
        SchedulePreview preview;
        try
        {
            preview = await _service.PreviewAsync(_draftSchedule, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            preview = SchedulePreview.Invalid($"The schedule could not be read: {exception.Message}");
        }

        // A later keystroke's preview wins over an earlier one that finished after it.
        if (sequence != _previewSequence)
            return;
        _preview = preview;
        PreviewText = Spans.Parse(PreviewMarkup(preview));
        OnPropertyChanged(nameof(PreviewOk));
        OnPropertyChanged(nameof(PreviewWarns));
        DraftChanged();
    }

    private string PreviewMarkup(SchedulePreview preview)
    {
        if (!preview.Valid)
            return Spans.Escape(preview.Error);
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var runs = preview.Next.Select(at => JobsText.When(at, now, zone)).ToList();
        var then = runs.Count > 1 ? $", then {string.Join(" and ", runs.Skip(1))}" : "";
        return $"runs next at **{runs[0]}**{then} ~· {Spans.Escape(preview.Parsed)}~";
    }

    private void DraftChanged()
    {
        var hidden = new (string Field, string Text)[] { ("name", _draftName), ("schedule", _draftSchedule), ("prompt", _draftPrompt), ("script", _draftScript) }
            .FirstOrDefault(field => SafeText.HasHidden(field.Text));
        DraftWarning = hidden.Field is null ? ""
            : $"The {hidden.Field} holds hidden characters, shown here: {SafeText.Reveal(hidden.Text)}. Remove them to create the job.";
        CreateCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreate() =>
        _draftName.Trim().Length > 0
        && PreviewOk
        && (_draftNoAgent ? _draftScript.Trim().Length > 0 : _draftPrompt.Trim().Length > 0)
        && !HasDraftWarning;

    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        var draft = new JobDraft(_draftName, _draftSchedule, _draftPrompt, _draftScript.Trim().Length == 0 ? null : _draftScript, _draftNoAgent, _draftDeliver);
        var job = await _service.CreateAsync(draft, cancellationToken).ConfigureAwait(true);
        IsCreating = false;
        // The service's Changed has rebuilt the rows; the new one is the selection.
        if (_rows.TryGetValue(job.Id, out var row))
            Selected = row;
        var next = job.NextRun is { } at ? $": next run {JobsText.When(at, _time.GetUtcNow(), _time.LocalTimeZone)}" : "";
        Say(true, $"Created **{Spans.Escape(SafeText.Clean(job.Name))}**{next}.");
    }

    // ---- Shell, status bar, notices ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            UpdateActive();
        else if (e.PropertyName == nameof(ShellViewModel.Link) && _shell.Link is { Area: Area.Jobs, SessionId: { } session })
            Land(session);
    }

    /// <summary>A link into Jobs naming a run's session: select its job and mark the run.</summary>
    private void Land(string session)
    {
        _highlightSession = session;
        if (_rows.Values.FirstOrDefault(row => row.Record.LastRun?.SessionId == session) is { } row)
        {
            if (ReferenceEquals(row, _selected))
                _ = LoadRunsAsync();
            else
                Selected = row;
        }
    }

    private void UpdateActive()
    {
        var active = _shell.CurrentArea == Area.Jobs;
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            _lease.Take();
            ApplyStatus();
            // Arriving (from the rail or the Board's finished job) lands on the job that ran last unless one is picked.
            if (_selected is null && _snapshot is not null)
                Selected = Landing();
            return;
        }

        IsConfirmingRemove = false;
        _lease.Release();
    }

    private void ApplyStatus()
    {
        if (_snapshot is not { } snapshot)
            return;
        var count = snapshot.Jobs.Count(job => job.State is JobState.Active or JobState.Paused);
        var next = snapshot.Jobs.Where(job => job.NextRun is not null).OrderBy(job => job.NextRun).FirstOrDefault();
        var where = snapshot.Scheduler.Host switch
        {
            SchedulerHost.Here => "here",
            SchedulerHost.Elsewhere => "elsewhere",
            _ => "off",
        };
        var jobs = count == 1 ? "1 job" : $"{count} jobs";
        // The Board's own items (the engine line, how many sessions run) are not this page's to show.
        _lease.Engine = "";
        _lease.Trailing = "";
        _lease.Scheduler = SafeText.Clean(next is { NextRun: { } at }
            ? $"{where} · {jobs} · next: {next.Name}, {JobsText.When(at, _time.GetUtcNow(), _time.LocalTimeZone)}"
            : $"{where} · {jobs}");
    }

    private void ReportFailures(AsyncRelayCommand command) => command.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(AsyncRelayCommand.Error) && command.Error is { } error)
            Say(false, $"That did not work: {Spans.Escape(error.Message)}");
    };

    private void Say(bool ok, string markup)
    {
        NoticeTone = ok ? Tone.Green : Tone.Red;
        Notice = Spans.Parse(markup);
    }
}
