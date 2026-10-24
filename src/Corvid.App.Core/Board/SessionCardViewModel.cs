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

    /// <summary>The card's whole text in one line, for a screen reader.</summary>
    public string AutomationName { get => _automationName; private set => SetProperty(ref _automationName, value); }

    /// <summary>Something waits on the user here: the card has the amber border.</summary>
    public bool IsWaiting => _summary.State == SessionState.Waiting;

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

    private void Refresh()
    {
        Title = BoardText.Title(_summary, _zone);
        Meta = BoardText.Meta(_summary);
        StateTone = BoardText.StateTone(_summary);
        StateLabel = BoardText.StateLabel(_summary, _view);
        StateDetail = BoardText.StateDetail(_summary, _view, _now, _zone);
        Detail = BoardText.Detail(_summary, _view, _now);
        var tags = BoardText.Tags(_summary);
        if (!tags.SequenceEqual(Tags))
            Tags = tags;
        AutomationName = BoardText.Join(Title, Text(Meta), $"{StateLabel}, {Text(StateDetail)}", Detail, string.Join(", ", Tags.Select(tag => tag.Text)));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsJob));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanPeek));
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
