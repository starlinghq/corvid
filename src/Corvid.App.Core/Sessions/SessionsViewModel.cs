using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// The Sessions area (docs/plan/ui.md §4.1 to §4.6): the selected session's conversation, and its dock panels. Until the
/// conversation is built, the page shows which session the rail selected, so the rail and <c>--session</c> can be seen to
/// reach it, and the dock shows its tabs with one line each.
/// </summary>
public sealed class SessionsViewModel : AreaViewModel
{
    private readonly ShellViewModel _shell;
    private SessionPanel _selectedPanel;

    public SessionsViewModel(ShellViewModel shell)
        : base(Area.Sessions, "The conversation in a workspace: the transcript with its tool cards and approvals, the turn strip and the composer.")
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _shell.PropertyChanged += OnShellChanged;
        Panels =
        [
            Panel("Changes", "Files changed this turn and this session, with their counts and a link to Review."),
            Panel("Files", "The workspace tree under the file tools' rules, with what Corvid may not read greyed."),
            Panel("Tasks", "The session's task list as the model keeps it."),
            Panel("Decisions", "Each checkpoint's questions, confidences and actions."),
            Panel("Context", "The context gauge and what fills it: system prompt, memory, skills, results."),
            Panel("Output", "The full output of the selected tool call, searchable."),
            Panel("Session", "The session's workspace, project, forks and standing goal."),
        ];
        _selectedPanel = Panels[0];
        _selectedPanel.IsSelected = true;
        OpenRewindCommand = new RelayCommand(() => _shell.Navigate(Area.Rewind));
    }

    /// <summary>The dock's tabs, in the mockup's order.</summary>
    public IReadOnlyList<SessionPanel> Panels { get; }

    public SessionPanel SelectedPanel => _selectedPanel;

    /// <summary>The selected session's title; empty when none is open.</summary>
    public string SessionTitle => _shell.CurrentSession?.Title ?? "";

    /// <summary>The selected session's id and state in words ("cli-9f2e · running").</summary>
    public string SessionDetail => _shell.CurrentSession is { } session ? $"{session.Id} · {session.StateLabel}" : "No session is open.";

    /// <summary>The session header's Rewind: the checkpoint timeline (Esc Esc on an empty composer, later).</summary>
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

    private SessionPanel Panel(string label, string emptyText)
    {
        SessionPanel? panel = null;
        panel = new SessionPanel(label, emptyText, new RelayCommand(() => SelectPanel(panel!)));
        return panel;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentSession))
            return;
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(SessionDetail));
    }
}
