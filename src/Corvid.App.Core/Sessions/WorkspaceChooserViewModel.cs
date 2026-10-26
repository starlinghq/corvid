using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// A6 (G-4): "Open a workspace…": where a new session works. The person types a folder's absolute path, or picks one of
/// the workspaces this window's sessions work in, or asks for a new git worktree of the session in view. A session keeps
/// its workspace for its life. A worktree writes outside the workspace, so it is made only after the person approves it
/// on the session in view's card: Sessions shows that session while the card waits. What the engine refuses (a folder
/// that is not there, inside what Corvid protects; a worktree denied) is said here in a line.
/// </summary>
public sealed class WorkspaceChooserViewModel : ObservableObject
{
    private readonly SessionHub _hub;
    private readonly ShellViewModel _shell;
    private bool _isOpen;
    private string _path = "";
    private string _worktreeName = "";
    private string _error = "";

    public WorkspaceChooserViewModel(SessionHub hub, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(shell);
        _hub = hub;
        _shell = shell;
        OpenCommand = new RelayCommand(Open, () => _hub.CanChooseWorkspace);
        CancelCommand = new RelayCommand(() => IsOpen = false);
        StartCommand = new AsyncRelayCommand(() => StartAsync(new NewSessionRequest(_path.Trim())), () => _path.Trim().Length > 0);
        WorktreeCommand = new AsyncRelayCommand(WorktreeAsync, () => AskIn is not null);
    }

    /// <summary>Opens the chooser (Board's "Open a workspace…"), with the window's workspaces listed afresh.</summary>
    public RelayCommand OpenCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Starts a session in the typed folder.</summary>
    public AsyncRelayCommand StartCommand { get; }

    /// <summary>Asks for a new git worktree of the session in view's workspace, and starts a session in it once approved.</summary>
    public AsyncRelayCommand WorktreeCommand { get; }

    /// <summary>The workspaces this window's sessions work in, each a choice that starts a session there.</summary>
    public ObservableCollection<Choice> Known { get; } = [];

    public bool HasKnown => Known.Count > 0;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!SetProperty(ref _isOpen, value))
                return;
            if (!value)
                Error = "";
        }
    }

    /// <summary>The folder typed: an absolute path (<c>~/</c> is the home directory).</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value ?? ""))
                StartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The new worktree's name (its branch is <c>corvid/&lt;name&gt;</c>); one from the time when empty.</summary>
    public string WorktreeName { get => _worktreeName; set => SetProperty(ref _worktreeName, value ?? ""); }

    /// <summary>Why the last start was refused, cleaned; empty when it was not.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>"A new git worktree of corvid, on a branch of its own: you approve it on “Fix the flaky test” first."</summary>
    public string WorktreeText => AskIn is { } session
        ? $"A new git worktree of {SafeText.Clean(SessionHub.WorkspaceName(WorkspaceOf(session.SessionId)))}, on a branch of its own: you approve it on “{SafeText.Clean(session.Title)}” first."
        : "Open a session first: a worktree is made of its workspace, after you approve it there.";

    // The session whose card asks: the one in view, when the window may write to it.
    private SessionViewModel? AskIn => _hub.Current is { IsReadOnly: false } current ? current : null;

    private string WorkspaceOf(string sessionId) =>
        _hub.Summaries.FirstOrDefault(summary => summary.Id == sessionId)?.Workspace ?? "";

    private void Open()
    {
        Known.Clear();
        foreach (var workspace in _hub.Summaries.Where(summary => summary.Workspace.Length > 0)
                     .GroupBy(summary => summary.Workspace, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Max(summary => summary.LastActivity))
                     .Select(group => group.Key))
        {
            var shown = SafeText.Clean(workspace);
            Known.Add(new Choice(SafeText.Clean(SessionHub.WorkspaceName(workspace)), shown,
                new AsyncRelayCommand(() => StartAsync(new NewSessionRequest(workspace)))));
        }
        OnPropertyChanged(nameof(HasKnown));
        OnPropertyChanged(nameof(WorktreeText));
        WorktreeCommand.NotifyCanExecuteChanged();
        Error = "";
        IsOpen = true;
    }

    private Task WorktreeAsync()
    {
        if (AskIn is not { } session)
            return Task.CompletedTask;
        // The card waits on the session in view: Sessions shows it.
        _shell.Navigate(Area.Sessions);
        return StartAsync(new NewSessionRequest
        {
            Worktree = true,
            AskIn = session.SessionId,
            WorktreeName = _worktreeName.Trim() is { Length: > 0 } name ? name : null,
        });
    }

    private async Task StartAsync(NewSessionRequest request)
    {
        Error = "";
        try
        {
            await _hub.CreateAsync(request).ConfigureAwait(true);
            IsOpen = false;
            Path = "";
            WorktreeName = "";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = "No new session: " + SafeText.Clean(exception.Message);
            // A worktree's refusal is heard on Sessions, where its card was.
            if (request.Worktree)
                _shell.StatusBar.Tags = [.. _shell.StatusBar.Tags, new StatusTag("no worktree", Tone.Red, Error)];
        }
    }
}
