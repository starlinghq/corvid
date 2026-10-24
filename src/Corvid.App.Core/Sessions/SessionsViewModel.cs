using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// The Sessions area (docs/plan/ui.md §4.1 to §4.6): the selected session (<see cref="Session"/>, a
/// <see cref="SessionViewModel"/> with its transcript, composer and panel contents) and the dock's tab strip. The tabs
/// belong to the area, so the selected tab stays when the rail selects another session; their badges are the current
/// session's (Changes: files it changed; Tasks: tasks not done).
/// </summary>
public sealed class SessionsViewModel : AreaViewModel
{
    private readonly ShellViewModel _shell;
    private readonly SessionHub? _hub;
    private SessionPanel _selectedPanel;
    private SessionViewModel? _session;

    public SessionsViewModel(ShellViewModel shell, SessionHub? hub = null)
        : base(Area.Sessions, "The conversation in a workspace: the transcript with its tool cards and approvals, the turn strip and the composer.")
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _hub = hub;
        _shell.PropertyChanged += OnShellChanged;
        Panels =
        [
            Panel(SessionPanels.Changes, "Files changed this turn and this session, with their counts and a link to Review."),
            Panel(SessionPanels.Files, "The workspace tree under the file tools' rules, with what Corvid may not read greyed."),
            Panel(SessionPanels.Tasks, "The session's task list as the model keeps it."),
            Panel(SessionPanels.Decisions, "Each checkpoint's questions, confidences and actions."),
            Panel(SessionPanels.Context, "The context gauge and what fills it: system prompt, memory, skills, results."),
            Panel(SessionPanels.Output, "The full output of the selected tool call, searchable."),
            Panel(SessionPanels.Session, "The session's workspace, project, forks and standing goal."),
        ];
        _selectedPanel = Panels[0];
        _selectedPanel.IsSelected = true;
        OpenRewindCommand = new RelayCommand(() => _shell.Navigate(Area.Rewind));
        if (_hub is not null)
        {
            _hub.CurrentChanged += (_, _) => Follow(_hub.Current);
            Follow(_hub.Current);
        }
    }

    /// <summary>The session the rail selected; null when none is open (no engine, or an empty workspace).</summary>
    public SessionViewModel? Session => _session;

    public bool HasSession => _session is not null;

    /// <summary>The dock's tabs, in the mockup's order: Changes, Files, Tasks, Decisions, Context, Output, Session.</summary>
    public IReadOnlyList<SessionPanel> Panels { get; }

    public SessionPanel SelectedPanel => _selectedPanel;

    /// <summary>The selected session's title; empty when none is open.</summary>
    public string SessionTitle => _shell.CurrentSession?.Title ?? "";

    /// <summary>The selected session's id and state in words ("cli-9f2e · running").</summary>
    public string SessionDetail => _shell.CurrentSession is { } session ? $"{session.Id} · {session.StateLabel}" : "No session is open.";

    /// <summary>The session header's Rewind: the checkpoint timeline (Esc Esc on an empty composer).</summary>
    public RelayCommand OpenRewindCommand { get; }

    /// <summary>Sets a dock tab's count (Changes: files changed; Tasks: open tasks).</summary>
    public void SetPanelBadge(string label, int count) =>
        Panels.First(panel => string.Equals(panel.Label, label, StringComparison.Ordinal)).Badge = count;

    public void SelectPanel(SessionPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (ReferenceEquals(panel, _selectedPanel) || !Panels.Contains(panel))
            return;
        _selectedPanel.IsSelected = false;
        _selectedPanel = panel;
        _selectedPanel.IsSelected = true;
        OnPropertyChanged(nameof(SelectedPanel));
    }

    /// <summary>Selects a tab by its label (Ctrl+1 to Ctrl+7 go by position: <c>Panels[n]</c>).</summary>
    public void SelectPanel(string label) => SelectPanel(Panels.First(panel => string.Equals(panel.Label, label, StringComparison.Ordinal)));

    private SessionPanel Panel(string label, string emptyText)
    {
        SessionPanel? panel = null;
        panel = new SessionPanel(label, emptyText, new RelayCommand(() => SelectPanel(panel!)));
        return panel;
    }

    private void Follow(SessionViewModel? session)
    {
        if (ReferenceEquals(session, _session))
            return;
        if (_session is not null)
            _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        if (_session is not null)
            _session.PropertyChanged += OnSessionChanged;
        UpdateBadges();
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasSession));
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(SessionViewModel.ChangesCount) or nameof(SessionViewModel.OpenTasks)
            or nameof(SessionViewModel.SessionFiles) or nameof(SessionViewModel.Tasks))
            UpdateBadges();
    }

    private void UpdateBadges()
    {
        SetPanelBadge(SessionPanels.Changes, _session?.ChangesCount ?? 0);
        SetPanelBadge(SessionPanels.Tasks, _session?.OpenTasks ?? 0);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentSession))
            return;
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(SessionDetail));
    }
}

/// <summary>The dock's tab labels.</summary>
public static class SessionPanels
{
    public const string Changes = "Changes";
    public const string Files = "Files";
    public const string Tasks = "Tasks";
    public const string Decisions = "Decisions";
    public const string Context = "Context";
    public const string Output = "Output";
    public const string Session = "Session";
}
