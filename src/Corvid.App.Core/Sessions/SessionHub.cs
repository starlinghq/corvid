using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// The window's sessions: one <see cref="SessionViewModel"/> per session the window has shown, the current one following
/// the rail's selection, and the live feeds from <see cref="ISessionService"/> into the shell. The rail's Pinned and
/// Recent groups with their status dots and times, the Sessions badge and the sessions' attention items come from the
/// service's summaries; the status bar shows the current session's generator, context, mode, tags and turn time while a
/// session's area (Sessions, Review, Rewind) is open, and a mode chosen there goes to the session.
/// </summary>
public sealed class SessionHub : IDisposable
{
    /// <summary>The attention source the sessions report under: an approval queue is one item, each question one.</summary>
    public const string Attention = "sessions";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly ISessionService _service;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ShellViewModel _shell;
    private readonly StatusBarViewModel _statusBar;
    private readonly Dictionary<string, SessionViewModel> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionSummary> _summaries = new(StringComparer.Ordinal);
    private SessionViewModel? _current;
    private ITimer? _ticker;
    private bool _disposed;

    public SessionHub(ISessionService service, IDispatcher dispatcher, TimeProvider time, ShellViewModel shell, StatusBarViewModel statusBar)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        _service = service;
        _dispatcher = dispatcher;
        _time = time;
        _shell = shell;
        _statusBar = statusBar;
        _service.SessionChanged += OnSessionChanged;
        _shell.PropertyChanged += OnShellChanged;
        _statusBar.ModeRequested += OnModeRequested;
    }

    public ISessionService Service => _service;

    /// <summary>The session the rail selected, opened; null when none is selected.</summary>
    public SessionViewModel? Current => _current;

    /// <summary>Why the sessions could not be listed at start-up; empty when they were. Also shown as a status bar tag.</summary>
    public string StartError { get; private set; } = "";

    /// <summary>Raised when <see cref="Current"/> changes to another session.</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>Every session's latest summary, for the Board and the attention tray.</summary>
    public IReadOnlyCollection<SessionSummary> Summaries => _summaries.Values;

    /// <summary>
    /// Lists the sessions into the rail and selects one: <paramref name="select"/> when the rail lists it, otherwise
    /// <paramref name="fallback"/>. The demo service answers at once, so the rail is full when this returns.
    /// </summary>
    public async Task StartAsync(string? select, string? fallback, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionSummary> summaries;
        try
        {
            summaries = await _service.ListAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            // Called without awaiting at start-up: a failure must show, not vanish with the task.
            StartError = $"Sessions could not be listed: {SafeText.Clean(exception.Message)}";
            _statusBar.Tags = [new StatusTag("sessions unavailable", Tone.Red, StartError)];
            return;
        }

        foreach (var summary in summaries)
            _summaries[summary.Id] = summary;
        RebuildRail();
        var chosen = select is not null ? _shell.FindSession(select) : null;
        chosen ??= fallback is not null ? _shell.FindSession(fallback) : null;
        if (chosen is not null)
            _shell.SelectSession(chosen);
        else
            SetCurrent(_shell.CurrentSession?.Id);
        UpdateTicker();
    }

    /// <summary>The view model of a session, created and opened on first use.</summary>
    public SessionViewModel Get(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_open.TryGetValue(sessionId, out var session))
            return session;
        session = new SessionViewModel(sessionId, _service, _dispatcher, _time, _shell);
        _open[sessionId] = session;
        _ = OpenAsync(session);
        return session;
    }

    /// <summary>
    /// Shows a session in the Sessions area by id, including one the rail does not list (a session another process
    /// drives, peeked from the Board).
    /// </summary>
    public void Show(string sessionId)
    {
        if (_shell.OpenSession(sessionId))
            return;
        if (!_summaries.TryGetValue(sessionId, out var summary))
            return;
        _shell.SelectSession(_shell.CreateSession(sessionId, SafeText.Clean(summary.Title), summary.State, When(summary)));
        _shell.Navigate(Area.Sessions);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.SessionChanged -= OnSessionChanged;
        _shell.PropertyChanged -= OnShellChanged;
        _statusBar.ModeRequested -= OnModeRequested;
        _ticker?.Dispose();
        foreach (var session in _open.Values)
            session.Dispose();
    }

    private static async Task OpenAsync(SessionViewModel session)
    {
        try
        {
            await session.OpenAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            session.ReportError($"Could not open the session: {exception.Message}");
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentSession):
                SetCurrent(_shell.CurrentSession?.Id);
                break;
            case nameof(ShellViewModel.CurrentArea):
                ApplyStatus();
                break;
        }
    }

    private void SetCurrent(string? sessionId)
    {
        var next = sessionId is null ? null : Get(sessionId);
        if (ReferenceEquals(next, _current))
            return;
        if (_current is not null)
            _current.PropertyChanged -= OnCurrentChanged;
        _current = next;
        if (_current is not null)
            _current.PropertyChanged += OnCurrentChanged;
        UpdateWorkspace();
        ApplyStatus();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCurrentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.WorkspaceText))
            UpdateWorkspace();
        else if (e.PropertyName is null or "" or nameof(SessionViewModel.StatusGenerator) or nameof(SessionViewModel.StatusDecider)
                 or nameof(SessionViewModel.StatusContext) or nameof(SessionViewModel.Mode) or nameof(SessionViewModel.StatusTags)
                 or nameof(SessionViewModel.StatusScheduler) or nameof(SessionViewModel.StatusBranch) or nameof(SessionViewModel.StatusTrailing))
            ApplyStatus();
    }

    private void UpdateWorkspace()
    {
        if (_current?.WorkspaceText is { Length: > 0 } workspace)
            _shell.Workspace = workspace;
    }

    /// <summary>The areas whose status bar is the session's (Main, Approval, Review and Rewind draw it so).</summary>
    public static bool ShowsSessionStatus(Area area) => area is Area.Sessions or Area.Review or Area.Rewind;

    private void ApplyStatus()
    {
        if (_current is not { } session || !ShowsSessionStatus(_shell.CurrentArea))
            return;
        var bar = _statusBar;
        bar.Engine = "";
        bar.Gateway = "";
        bar.AreaFields = [];
        bar.Generator = session.StatusGenerator;
        bar.Decider = session.StatusDecider;
        bar.Context = session.StatusContext;
        bar.Mode = session.Mode;
        // A failed start-up listing stays in view whichever session shows.
        bar.Tags = StartError.Length > 0 ? [new StatusTag("sessions unavailable", Tone.Red, StartError), .. session.StatusTags] : session.StatusTags;
        bar.Scheduler = session.StatusScheduler;
        bar.Branch = session.StatusBranch;
        bar.Trailing = session.StatusTrailing;
    }

    private void OnModeRequested(object? sender, ApprovalMode mode)
    {
        // The engine answers with the mode in force; until a session is open, the request is the answer.
        if (_current is { IsReadOnly: false } session && session.SetModeCommand.CanExecute(mode))
            session.SetModeCommand.Execute(mode);
        else if (_current is null)
            _statusBar.Mode = mode;
    }

    private void OnSessionChanged(object? sender, SessionSummary summary)
    {
        var known = _summaries.TryGetValue(summary.Id, out var previous);
        _summaries[summary.Id] = summary;
        if (!known || previous!.Pinned != summary.Pinned || previous.ReadOnly != summary.ReadOnly || InRail(previous) != InRail(summary))
            RebuildRail();
        else if (_shell.FindSession(summary.Id) is { } item)
            Refresh(item, summary);
        UpdateCounts();
        UpdateTicker();
    }

    /// <summary>
    /// The rail lists the sessions this window drives (pinned, then the rest by latest activity) and job runs; one another
    /// process drives is on the Board as a read-only live view.
    /// </summary>
    private static bool InRail(SessionSummary summary) => !summary.ReadOnly || summary.Job is not null;

    private void RebuildRail()
    {
        var listed = _summaries.Values.Where(InRail).OrderByDescending(summary => summary.LastActivity).ToList();
        var pinned = listed.Where(summary => summary.Pinned).Select(Item).ToList();
        var recent = listed.Where(summary => !summary.Pinned).Select(Item).ToList();
        var groups = new List<SessionGroup>();
        if (pinned.Count > 0)
            groups.Add(new SessionGroup("Pinned", pinned));
        if (recent.Count > 0)
            groups.Add(new SessionGroup("Recent", recent));
        _shell.SetSessions(groups);
        UpdateCounts();
    }

    private SessionListItem Item(SessionSummary summary) => _shell.CreateSession(summary.Id, SafeText.Clean(summary.Title), summary.State, When(summary));

    private void Refresh(SessionListItem item, SessionSummary summary)
    {
        item.Title = SafeText.Clean(summary.Title);
        item.State = summary.State;
        item.When = When(summary);
    }

    private void UpdateCounts()
    {
        _shell.SetAttention(Attention, _summaries.Values.Sum(summary => summary.Attention));
        _shell.SetBadge(Area.Sessions, _summaries.Values.Count(summary => InRail(summary)
            && summary.State is SessionState.Running or SessionState.Waiting));
    }

    /// <summary>
    /// The rail's time: a running session's current step ("12 s"), a waiting one's deadline ("4:12"), an idle one's last
    /// activity ("1 h"), a job run's start ("09:00").
    /// </summary>
    internal string When(SessionSummary summary)
    {
        var now = _time.GetUtcNow();
        if (summary.LastActivity > now)
            now = summary.LastActivity;
        return summary.State switch
        {
            SessionState.Job when summary.RunAt is { } run => RelativeTime.ClockTime(run, _time.LocalTimeZone),
            SessionState.Waiting when summary.Deadline is { } deadline => SessionText.Countdown(deadline - now),
            SessionState.Running or SessionState.Waiting when summary.StepStarted is { } step => RelativeTime.Since(now, step),
            _ => RelativeTime.Since(now, summary.LastActivity),
        };
    }

    private void UpdateTicker()
    {
        var needed = !_disposed && _summaries.Values.Any(summary => summary.State is SessionState.Running or SessionState.Waiting);
        if (needed && _ticker is null)
            _ticker = _time.CreateTimer(_ => _dispatcher.Post(Tick), null, TickInterval, TickInterval);
        else if (!needed && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    private void Tick()
    {
        if (_disposed)
            return;
        foreach (var summary in _summaries.Values)
        {
            if (summary.State is SessionState.Running or SessionState.Waiting && _shell.FindSession(summary.Id) is { } item)
                item.When = When(summary);
        }
    }
}
