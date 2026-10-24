using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Jobs;

/// <summary>
/// A row of the jobs table: name, schedule as written, next run, state and last run. Its text may have come from the model
/// (<c>cron_create</c>), so it is cleaned. Clicking it (or Up and Down on the page) selects it; the detail and the run
/// history follow the selection.
/// </summary>
public sealed class JobRowViewModel : ObservableObject
{
    private JobRecord _record;
    private bool _isSelected;
    private string _next = "";
    private string _last = "";

    internal JobRowViewModel(JobRecord record, Action<JobRowViewModel> select)
    {
        _record = record;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Id => _record.Id;

    public JobRecord Record => _record;

    public string Name => SafeText.Clean(_record.Name);

    public string Schedule => SafeText.Clean(_record.Schedule);

    /// <summary>"tomorrow 09:00", or "—" when paused or finished.</summary>
    public string Next => _next;

    public string StateText => JobsText.State(_record.State);

    public Tone StateTone => JobsText.StateTone(_record.State);

    /// <summary>"today 09:00 · ok · 41 s", or "not run yet".</summary>
    public string Last => _last;

    public bool LastFailed => _record.LastRun?.Status == JobRunStatus.Failed;

    public bool IsTainted => _record.CreatedTainted;

    public RelayCommand SelectCommand { get; }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public string AutomationName => $"{Name}, {Schedule}, {StateText}, next run {Next}, last run {Last}";

    internal void Update(JobRecord record, DateTimeOffset now, TimeZoneInfo zone)
    {
        _record = record;
        _next = record.NextRun is { } next ? JobsText.When(next, now, zone) : "—";
        _last = record.LastRun is { } run
            ? $"{JobsText.When(run.StartedAt, now, zone)} · {JobsText.Status(run.Status)} · {JobsText.Took(run)}"
            : "not run yet";
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// A row of a job's run history (<c>/cron runs</c>): when it was due, how long it took, how it went, what it did, and the
/// session it ran in, which opens read-only as the run was kept.
/// </summary>
public sealed class JobRunRowViewModel : ObservableObject
{
    private bool _isHighlighted;

    internal JobRunRowViewModel(JobRunRecord run, DateTimeOffset now, TimeZoneInfo zone, bool canOpen, Action<JobRunRowViewModel> open)
    {
        Run = run;
        When = JobsText.When(run.ScheduledFor, now, zone);
        Took = JobsText.Took(run);
        CanOpen = canOpen && run.SessionId.Length > 0;
        OpenCommand = new RelayCommand(() => open(this), () => CanOpen);
    }

    public JobRunRecord Run { get; }

    public string RunId => SafeText.Clean(Run.RunId);

    public string When { get; }

    public string Took { get; }

    public string StatusText => JobsText.Status(Run.Status);

    public Tone StatusTone => JobsText.StatusTone(Run.Status);

    public string Detail => SafeText.Clean(Run.Detail);

    public string SessionId => SafeText.Clean(Run.SessionId);

    /// <summary>The run's session is in the session list, so it can open; an older run's session may not be.</summary>
    public bool CanOpen { get; }

    public string OpenHelp => CanOpen ? $"Open {SessionId} read-only, as the run kept it" : "This run's session is not in the session list";

    public RelayCommand OpenCommand { get; }

    /// <summary>The run a link landed on (the Board's finished job).</summary>
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }

    public string AutomationName => $"Run {When}, {StatusText}, took {Took}. {Detail}";
}

/// <summary>A tag beside the selected job's title: tainted, no agent, a pre-run script.</summary>
public static class JobTags
{
    public static IReadOnlyList<StatusTag> Of(JobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var tags = new List<StatusTag> { new(JobsText.State(job.State), JobsText.StateTone(job.State)) };
        if (job.CreatedTainted)
            tags.Add(new StatusTag("tainted", Tone.Amber, "Created from a tainted session: its runs start tainted, so egress asks."));
        if (job.NoAgent)
            tags.Add(new StatusTag("no agent", Tone.Grey, "Runs only its script; no model turn."));
        else if (job.Script is not null)
            tags.Add(new StatusTag("script", Tone.Grey, "A pre-run script's output goes into the prompt."));
        return tags;
    }
}
