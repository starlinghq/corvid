using System.Collections.ObjectModel;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Board;

/// <summary>A tile in the Board's grid: a session's card, or the dashed card that starts another.</summary>
public abstract class BoardTile : ObservableObject;

/// <summary>
/// The dashed card after the sessions (Board.dc.html): start another session in the current workspace, or open a
/// workspace. Both need the engine (no service starts a session yet), so their buttons are disabled with a reason.
/// </summary>
public sealed class NewSessionTile(IReadOnlyList<MarkdownRun> text, RelayCommand newSession, RelayCommand openWorkspace) : BoardTile
{
    /// <summary>"Start another session in <b>corvid · main</b>, or open a workspace. …"</summary>
    public IReadOnlyList<MarkdownRun> Text { get; } = text;

    public RelayCommand NewSessionCommand { get; } = newSession;

    public RelayCommand OpenWorkspaceCommand { get; } = openWorkspace;

    public string AutomationName => string.Concat(Text.Select(run => run.Text));
}

/// <summary>
/// One session as a Board card (docs/plan/ui.md §4.13, Board.dc.html <c>.scard</c>): title; workspace, branch and id;
/// the state with a coloured dot and what it waits on or runs; the step, model and time; tags for taint and where the
/// generator runs; and Open, Peek, Take over, or a job run's Open run and Jobs. It follows the session's summary and,
/// while watched, its folded state; the clock moves its times.
/// <para>
/// C-043: the card's kanban column, its one line on where the session is at (<see cref="StatusLine"/>), and the person's
/// moves: Mark done on an idle card (disabled with why on a running one or one with an open approval or question), Not
/// now on a your-turn, "Looks finished · Mark done" on a parked card whose answer reads finished, and Reopen in Done.
/// </para>
/// </summary>
public sealed class SessionCardViewModel : BoardTile
{
    private readonly TimeZoneInfo _zone;
    private SessionSummary _summary;
    private SessionView? _view;
    private DateTimeOffset _now;
    private string _title = "";
    private IReadOnlyList<MarkdownRun> _meta = [];
    private Tone _stateTone;
    private string _stateLabel = "";
    private IReadOnlyList<MarkdownRun> _stateDetail = [];
    private string _detail = "";
    private IReadOnlyList<StatusTag> _tags = [];
    private string _automationName = "";
    private string _foundBy = "";
    private BoardColumn _column;
    private string _statusLine = "";

    public SessionCardViewModel(SessionSummary summary, DateTimeOffset now, TimeZoneInfo zone, Action<SessionCardViewModel> open,
        Action<SessionCardViewModel> peek, Action openJobs)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(peek);
        ArgumentNullException.ThrowIfNull(openJobs);
        _summary = summary;
        _now = now;
        _zone = zone;
        OpenCommand = new RelayCommand(() => open(this), () => CanOpen);
        OpenRunCommand = new RelayCommand(() => open(this), () => IsJob);
        PeekCommand = new RelayCommand(() => peek(this), () => CanPeek);
        // Taking over a session another process drives needs it to let go (G-12); the engine will offer it then.
        TakeOverCommand = new RelayCommand(() => { }, () => false);
        OpenJobsCommand = new RelayCommand(openJobs);
        MarkDoneCommand = new RelayCommand(() => Mark(BoardMark.Done), () => CanMarkDone);
        NotNowCommand = new RelayCommand(() => Mark(BoardMark.Parked), () => Marker is not null && ShowsNotNow);
        ReopenCommand = new RelayCommand(() => Mark(null), () => Marker is not null && ShowsReopen);
        Refresh();
    }

    public string Id => _summary.Id;

    public SessionSummary Summary => _summary;

    /// <summary>The session's folded state, once watched; null until then.</summary>
    public SessionView? View => _view;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public IReadOnlyList<MarkdownRun> Meta { get => _meta; private set => SetRuns(ref _meta, value); }

    public Tone StateTone { get => _stateTone; private set => SetProperty(ref _stateTone, value); }

    public string StateLabel { get => _stateLabel; private set => SetProperty(ref _stateLabel, value); }

    /// <summary>What follows the state, after a dot ("· approval 1 of 2 · 4:12 left").</summary>
    public IReadOnlyList<MarkdownRun> StateDetail { get => _stateDetail; private set => SetRuns(ref _stateDetail, value); }

    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public IReadOnlyList<StatusTag> Tags { get => _tags; private set => SetProperty(ref _tags, value); }

    /// <summary>
    /// C-044: how the Board's filter found the card when its own text did not match ("by meaning", "by words"): shown as a
    /// tag while the filter holds it. Empty otherwise.
    /// </summary>
    public string FoundBy
    {
        get => _foundBy;
        set
        {
            if (SetProperty(ref _foundBy, value ?? ""))
                Refresh();
        }
    }

    /// <summary>The card's whole text in one line, for a screen reader.</summary>
    public string AutomationName { get => _automationName; private set => SetProperty(ref _automationName, value); }

    /// <summary>Something waits on the user here (C-043: a your-turn too): the card has the amber border.</summary>
    public bool IsWaiting => _summary.State == SessionState.Waiting || BoardText.YourTurn(_summary);

    public bool IsJob => _summary.Job is not null;

    /// <summary>Another process drives it: Peek and a disabled Take over instead of Open.</summary>
    public bool IsReadOnly => _summary.ReadOnly && !IsJob;

    /// <summary>Open attaches it: the Sessions area shows it as the current session.</summary>
    public bool CanOpen => !_summary.ReadOnly && !IsJob;

    /// <summary>Peek shows a live session read-only without attaching to it; an idle one has nothing to watch.</summary>
    public bool CanPeek => !IsJob && (_summary.ReadOnly || _summary.State is SessionState.Running or SessionState.Waiting);

    public RelayCommand OpenCommand { get; }

    public RelayCommand PeekCommand { get; }

    public RelayCommand TakeOverCommand { get; }

    public RelayCommand OpenRunCommand { get; }

    public RelayCommand OpenJobsCommand { get; }

    /// <summary>
    /// C-043: what writes the card's Board mark (the Board's, through the session service); null where marks are not kept
    /// (attached mode: <c>ui.db</c> is not served), so the buttons are disabled and say why.
    /// </summary>
    public Func<SessionCardViewModel, BoardMark?, Task>? Marker { get; init; }

    /// <summary>C-043: the kanban column the card is in.</summary>
    public BoardColumn Column { get => _column; private set => SetProperty(ref _column, value); }

    /// <summary>C-043: the one line on where the session is at (<see cref="BoardText.StatusLine"/>).</summary>
    public string StatusLine { get => _statusLine; private set => SetProperty(ref _statusLine, value); }

    /// <summary>
    /// C-043: the status line is drawn under the state when it says something the state line does not: an idle card's (a
    /// running or waiting card's state already names the call, the approval or the question).
    /// </summary>
    public bool ShowsStatusLine => _statusLine.Length > 0 && !IsJob
        && !(_summary.State is SessionState.Running or SessionState.Waiting || _summary.WorkingElsewhere);

    /// <summary>C-043: an idle session whose latest answer asks the person for something: "your turn".</summary>
    public bool IsYourTurn => BoardText.YourTurn(_summary);

    /// <summary>
    /// C-043: Mark done shows on every session card outside Done, except where the done suggestion shows instead; it is
    /// enabled only on an idle one with nothing open (<see cref="MarkDoneHelp"/> says why not).
    /// </summary>
    public bool ShowsMarkDone => !IsJob && _summary.Child is null && _column != BoardColumn.Done && !SuggestsDone;

    public bool CanMarkDone => Marker is not null && !IsJob && _summary.Child is null && _column != BoardColumn.Done && _summary.Turns > 0
        && _summary.State != SessionState.Running && !_summary.WorkingElsewhere && _summary.PendingApprovals == 0 && _summary.PendingQuestions == 0;

    /// <summary>C-043: what Mark done does, or why it is disabled.</summary>
    public string MarkDoneHelp =>
        Marker is null ? "The Board's Done is kept in ui.db, which the engine on this machine keeps"
        : _summary.PendingApprovals > 0 || _summary.PendingQuestions > 0 ? "Answer it first: an approval or question is open"
        : _summary.State == SessionState.Running || _summary.WorkingElsewhere ? "A turn is running: mark it done once it ends"
        // A new session holds nothing in the log yet, so there is nothing to mark through.
        : _summary.Turns == 0 ? "Nothing has been sent in it yet"
        : "Move it to Done until something new happens in it";

    /// <summary>C-043: Not now, on a your-turn: Parked until something new happens (a false "your turn" is put aside so).</summary>
    public bool ShowsNotNow => !IsJob && _summary.Child is null && _column == BoardColumn.NeedsYou && _summary is { Ending: TurnEnding.AsksYou, PendingApprovals: 0, PendingQuestions: 0 };

    /// <summary>
    /// C-043: the done suggestion: a parked card whose latest answer reads as a finished handoff shows "Looks finished ·
    /// Mark done". Nothing moves to Done by itself.
    /// </summary>
    public bool SuggestsDone => !IsJob && _summary.Child is null && _column == BoardColumn.Parked && _summary is { Ending: TurnEnding.Finished, Mark: null };

    /// <summary>C-043: Reopen, on a card the person marked done: back where it would be otherwise.</summary>
    public bool ShowsReopen => !IsJob && _column == BoardColumn.Done && _summary.Mark == BoardMark.Done;

    public RelayCommand MarkDoneCommand { get; }

    public RelayCommand NotNowCommand { get; }

    public RelayCommand ReopenCommand { get; }

    private void Mark(BoardMark? mark)
    {
        if (Marker is { } marker)
            _ = marker(this, mark);
    }

    /// <summary>A5: this session's children, as a tree under the card (docs/plan/ui.md §4.13): oldest first.</summary>
    public ObservableCollection<ChildRowViewModel> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    /// <summary>
    /// M5 (A5 review): the most child rows a card shows; the rest are counted in <see cref="MoreChildren"/>. Running children
    /// are shown first (they can be stopped), then the latest.
    /// </summary>
    public const int MaxChildRows = 8;

    private int _childCount, _runningCount;

    /// <summary>"2 child sessions · 1 running", over every child, the ones left out of the rows too.</summary>
    public string ChildrenHeader =>
        $"{_childCount} child session{(_childCount == 1 ? "" : "s")}" + (_runningCount > 0 ? $" · {_runningCount} running" : "");

    /// <summary>"+3 more" when the card shows only <see cref="MaxChildRows"/> of its children; empty otherwise.</summary>
    public string MoreChildren { get; private set; } = "";

    /// <summary>
    /// A5: the children the Board found for this card, in order: a row each, kept in place when it only changed, so focus
    /// stays on it.
    /// </summary>
    public void SetChildren(IReadOnlyList<ChildSessionChanged> all, Action<string> open, Func<string, Task>? stop)
    {
        ArgumentNullException.ThrowIfNull(all);
        _childCount = all.Count;
        _runningCount = all.Count(child => child.IsRunning);
        // M5: a capped tree, in order: the running children, then the latest, up to MaxChildRows.
        IReadOnlyList<ChildSessionChanged> children = all.Count <= MaxChildRows ? all
            : [.. all.Select((child, index) => (Child: child, Index: index)).OrderByDescending(item => item.Child.IsRunning)
                .ThenByDescending(item => item.Index).Take(MaxChildRows).OrderBy(item => item.Index).Select(item => item.Child)];
        MoreChildren = all.Count > children.Count ? $"+{all.Count - children.Count} more" : "";
        for (var i = 0; i < children.Count; i++)
        {
            var at = -1;
            for (var j = i; j < Children.Count; j++)
                if (Children[j].Id == children[i].ChildId)
                {
                    at = j;
                    break;
                }
            if (at < 0)
                Children.Insert(i, new ChildRowViewModel(children[i], open, stop));
            else
            {
                if (at != i)
                    Children.Move(at, i);
                if (Children[i].Child != children[i])
                    Children[i].Update(children[i]);
            }
        }
        while (Children.Count > children.Count)
            Children.RemoveAt(Children.Count - 1);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ChildrenHeader));
        OnPropertyChanged(nameof(MoreChildren));
    }

    public void Update(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _summary = summary;
        Refresh();
    }

    public void Update(SessionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        Refresh();
        OnPropertyChanged(nameof(View));
    }

    /// <summary>The clock moved: the countdown and the time on the step follow it.</summary>
    public void Tick(DateTimeOffset now)
    {
        _now = now;
        Refresh();
    }

    /// <summary>A new summary, read at <paramref name="now"/>.</summary>
    public void Tick(DateTimeOffset now, SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _now = now;
        Update(summary);
    }

    private void Refresh()
    {
        Title = BoardText.Title(_summary, _zone);
        Meta = BoardText.Meta(_summary);
        StateTone = BoardText.StateTone(_summary);
        StateLabel = BoardText.StateLabel(_summary, _view);
        StateDetail = BoardText.StateDetail(_summary, _view, _now, _zone);
        Detail = BoardText.Detail(_summary, _view, _now);
        Column = BoardText.Column(_summary);
        StatusLine = BoardText.StatusLine(_summary);
        var tags = BoardText.Tags(_summary);
        if (_foundBy.Length > 0)
            tags = [.. tags, new StatusTag(_foundBy, Tone.Grey, "The session search found it for the filter, though its title and preview do not match the words")];
        if (!tags.SequenceEqual(Tags))
            Tags = tags;
        AutomationName = BoardText.Join(Title, Text(Meta), $"{StateLabel}, {Text(StateDetail)}", ShowsStatusLine ? StatusLine : null, Detail,
            string.Join(", ", Tags.Select(tag => tag.Text)));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsJob));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanPeek));
        OnPropertyChanged(nameof(ShowsStatusLine));
        OnPropertyChanged(nameof(IsYourTurn));
        OnPropertyChanged(nameof(ShowsMarkDone));
        OnPropertyChanged(nameof(CanMarkDone));
        OnPropertyChanged(nameof(MarkDoneHelp));
        OnPropertyChanged(nameof(ShowsNotNow));
        OnPropertyChanged(nameof(SuggestsDone));
        OnPropertyChanged(nameof(ShowsReopen));
        MarkDoneCommand.NotifyCanExecuteChanged();
        NotNowCommand.NotifyCanExecuteChanged();
        ReopenCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        PeekCommand.NotifyCanExecuteChanged();
        OpenRunCommand.NotifyCanExecuteChanged();
    }

    private static string Text(IEnumerable<MarkdownRun> runs) => string.Concat(runs.Select(run => run.Text));

    /// <summary>Runs are rebuilt on every refresh; the view redraws only when the text did change.</summary>
    private void SetRuns(ref IReadOnlyList<MarkdownRun> field, IReadOnlyList<MarkdownRun> value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field.SequenceEqual(value))
            return;
        field = value;
        OnPropertyChanged(name);
    }
}
