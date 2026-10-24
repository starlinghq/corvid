using System.Windows.Input;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Shell;

/// <summary>A session's live state, drawn as the rail's status dot and always said in words too.</summary>
public enum SessionState
{
    Idle,
    Running,
    Waiting,
    Job,
}

/// <summary>A session in the rail's pinned or recent group: title, status dot, time since its last activity.</summary>
public sealed class SessionListItem : ObservableObject
{
    private string _title;
    private SessionState _state;
    private string _when;
    private bool _isSelected;

    internal SessionListItem(string id, string title, SessionState state, string when, ICommand open)
    {
        Id = id;
        _title = title;
        _state = state;
        _when = when;
        Open = open;
    }

    /// <summary>The session's id in the event log, such as <c>cli-9f2e</c>.</summary>
    public string Id { get; }

    public ICommand Open { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public SessionState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>"12 s", "2 m", "1 h", or a job run's start time ("09:00").</summary>
    public string When
    {
        get => _when;
        set
        {
            if (SetProperty(ref _when, value))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>The dot's meaning in words, so colour never carries it alone.</summary>
    public string StateLabel => _state switch
    {
        SessionState.Running => "running",
        SessionState.Waiting => "waiting on you",
        SessionState.Job => "job run",
        _ => "idle",
    };

    public string AutomationName => $"{_title}, {StateLabel}, {_when}";
}

/// <summary>A titled group of sessions in the rail: Pinned, Recent, or the Board's workspaces.</summary>
public sealed class SessionGroup(string title, IReadOnlyList<SessionListItem> sessions)
{
    public string Title { get; } = title;

    public IReadOnlyList<SessionListItem> Sessions { get; } = sessions;
}
