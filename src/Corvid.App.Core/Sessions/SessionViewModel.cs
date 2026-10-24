using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>Where the view should put keyboard focus when the session asks.</summary>
public enum SessionFocusTarget
{
    /// <summary>The composer: a question opened, or a focused approval card closed.</summary>
    Composer,
    /// <summary>The open approval card: the first Enter after it appeared.</summary>
    Approval,
}

/// <summary>
/// One open session as the Sessions page draws it (Main.png, Approval.png; docs/plan/ui.md §4.1 to §4.6): the header, the
/// transcript as typed blocks, the turn strip with the queue, the composer, the approval queue, the dock's panel contents
/// and the status bar's values. Its state is the fold of the session's updates (<see cref="SessionFold"/>); the view model
/// turns each new state into changes of the blocks it already drew, so streaming updates text in place and never moves
/// focus. Requests go to <see cref="ISessionService"/>, and what they change comes back as updates.
/// <para>
/// Everything here runs on the UI thread. A timer runs only while a turn runs or a prompt counts down, to refresh elapsed
/// times; with nothing running there is none.
/// </para>
/// </summary>
public sealed partial class SessionViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly ISessionService _service;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ShellViewModel? _shell;
    private readonly BlockContext _context;
    private readonly Dictionary<string, TranscriptBlock> _blocks = new(StringComparer.Ordinal);
    private readonly List<SessionUpdate> _early = [];
    private ISessionSubscription? _subscription;
    private SessionView _state;
    private ITimer? _ticker;
    private bool _loading;
    private bool _disposed;
    private bool _showAllDecisions;
    private string _error = "";
    private ApprovalCardBlock? _currentApproval;
    private QuestionBlock? _pendingQuestion;

    public SessionViewModel(string sessionId, ISessionService service, IDispatcher dispatcher, TimeProvider time, ShellViewModel? shell = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        SessionId = sessionId;
        _service = service;
        _dispatcher = dispatcher;
        _time = time;
        _shell = shell;
        _state = SessionFold.Start(sessionId);
        _context = new BlockContext(this, () => Now, time.LocalTimeZone);
        ToggleDecisionsCommand = new RelayCommand(() => ShowAllDecisions = !ShowAllDecisions);
        ToggleThoughtsCommand = new RelayCommand(ToggleThoughts);
        ExpandLatestOutputCommand = new RelayCommand(ExpandLatestOutput);
        SetGoalCommand = new RelayCommand<string>(goal => Run(() => _service.SetGoalAsync(SessionId, goal)), _ => !IsReadOnly);
        ClearGoalCommand = new RelayCommand(() => Run(() => _service.SetGoalAsync(SessionId, null)), () => !IsReadOnly && HasGoal);
        CompactCommand = new RelayCommand<string>(focus => Run(() => _service.CompactAsync(SessionId, string.IsNullOrWhiteSpace(focus) ? null : focus.Trim())), _ => !IsReadOnly && !IsRunning);
        RenameCommand = new RelayCommand<string>(title => Run(() => _service.RenameAsync(SessionId, title.Trim())), title => !string.IsNullOrWhiteSpace(title));
        ForkCommand = new RelayCommand<UserMessageBlock>(block => Fork(block.Position), block => block is not null);
        SetModeCommand = new RelayCommand<ApprovalMode>(mode => Run(() => _service.SetApprovalModeAsync(SessionId, mode)), _ => !IsReadOnly);
        InitializeComposer();
        InitializePanels();
    }

    public string SessionId { get; }

    /// <summary>Asks the page to put the caret in the composer (a prompt from the history was put there).</summary>
    public void RequestComposerFocus() => FocusRequested?.Invoke(this, SessionFocusTarget.Composer);

    /// <summary>The fold's current state, for tests and for views that need a raw value.</summary>
    public SessionView State => _state;

    /// <summary>The session has been opened and its history drawn.</summary>
    public bool IsOpen => _subscription is not null && !_loading;

    /// <summary>Raised after every update has been applied (the status bar feed listens).</summary>
    public event EventHandler? StateChanged;

    /// <summary>The session wants keyboard focus somewhere: the view moves it, and nothing else does.</summary>
    public event EventHandler<SessionFocusTarget>? FocusRequested;

    // ---- Header ----

    public string Title => _state.Title.Length > 0 ? _state.Title : SessionId;

    /// <summary>"fork of cli-8c11 at turn 9"; empty when the session is not a fork.</summary>
    public string ForkText => _state.ForkOf is { } from
        ? _state.ForkTurn is { } turn ? $"fork of {from} at turn {turn}" : $"fork of {from}"
        : "";

    public bool HasFork => ForkText.Length > 0;

    /// <summary>The standing goal, raw.</summary>
    public string Goal => _state.Goal ?? "";

    /// <summary>"goal: keep CI green on main".</summary>
    public string GoalText => _state.Goal is { } goal ? $"goal: {goal}" : "";

    public bool HasGoal => _state.Goal is not null;

    /// <summary>Another process drives the session: the composer is off, and the page says who holds it (G-12).</summary>
    public bool IsReadOnly => _state.ReadOnly;

    /// <summary>"Read-only: open in a terminal (corvid chat --session cli-c3d2); take over once it lets go."</summary>
    public string ReadOnlyNote => _state.ReadOnly ? $"Read-only: {_state.Holder ?? "another process drives this session"}." : "";

    // ---- Transcript ----

    /// <summary>The transcript, in reading order: one template per <see cref="TranscriptBlock.Kind"/>.</summary>
    public ObservableCollection<TranscriptBlock> Blocks { get; } = [];

    /// <summary>Every checkpoint's chips at its place (Ctrl+Shift+D), instead of one summary row per turn.</summary>
    public bool ShowAllDecisions
    {
        get => _showAllDecisions;
        set
        {
            if (SetProperty(ref _showAllDecisions, value))
                SyncTranscript();
        }
    }

    public RelayCommand ToggleDecisionsCommand { get; }

    /// <summary>Ctrl+T: expands every thought row, or collapses them when all are expanded.</summary>
    public RelayCommand ToggleThoughtsCommand { get; }

    /// <summary>Ctrl+O: expands the latest tool card that has output, or collapses it.</summary>
    public RelayCommand ExpandLatestOutputCommand { get; }

    /// <summary>The open approval card the user answers next; it is also one of <see cref="Blocks"/>.</summary>
    public ApprovalCardBlock? CurrentApproval
    {
        get => _currentApproval;
        private set
        {
            if (SetProperty(ref _currentApproval, value))
                OnPropertyChanged(nameof(HasApproval));
        }
    }

    public bool HasApproval => _currentApproval is not null;

    /// <summary>Open prompts, the current one first ("1 of 2").</summary>
    public int PendingApprovals => _state.Approvals.Count;

    /// <summary>The open clarifying question, pinned; also one of <see cref="Blocks"/>.</summary>
    public QuestionBlock? PendingQuestion
    {
        get => _pendingQuestion;
        private set
        {
            if (SetProperty(ref _pendingQuestion, value))
                OnPropertyChanged(nameof(HasQuestion));
        }
    }

    public bool HasQuestion => _pendingQuestion is not null;

    // ---- Requests ----

    public RelayCommand<string> SetGoalCommand { get; }

    public RelayCommand ClearGoalCommand { get; }

    /// <summary>"Compact…" with a focus (empty for none): <c>/compact &lt;focus&gt;</c>, between turns.</summary>
    public RelayCommand<string> CompactCommand { get; }

    public RelayCommand<string> RenameCommand { get; }

    /// <summary>"Branch from here" on a message: a fork through its position, opened in the rail.</summary>
    public RelayCommand<UserMessageBlock> ForkCommand { get; }

    /// <summary>The approval mode for this session (the status bar's segments route here).</summary>
    public RelayCommand<ApprovalMode> SetModeCommand { get; }

    /// <summary>The last request the engine refused, in a line; empty when all went through.</summary>
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    internal void ReportError(string message) => Error = message;

    /// <summary>Opens the session: draws its history, then follows its updates until disposed.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_subscription is not null || _loading)
            return;
        _loading = true;
        try
        {
            // The service delivers on the UI thread; anything that arrives elsewhere is moved there.
            var subscription = await _service.OpenAsync(SessionId, update =>
            {
                if (_dispatcher.HasThreadAccess)
                    OnUpdate(update);
                else
                    _dispatcher.Post(() => OnUpdate(update));
            }, cancellationToken).ConfigureAwait(true);
            if (_disposed)
            {
                subscription.Dispose();
                return;
            }

            _subscription = subscription;
            var state = SessionFold.Replay(SessionId, subscription.History);
            foreach (var early in _early)
                state = SessionFold.Apply(state, early);
            _early.Clear();
            _loading = false;
            Apply(state);
            OnPropertyChanged(nameof(IsOpen));
        }
        finally
        {
            _loading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription?.Dispose();
        _ticker?.Dispose();
        _ticker = null;
    }

    private void OnUpdate(SessionUpdate update)
    {
        if (_disposed)
            return;
        if (_loading)
        {
            _early.Add(update);
            return;
        }

        Apply(SessionFold.Apply(_state, update));
    }

    /// <summary>The clock as the session sees it: never behind its own latest update, so a frozen demo clock still moves with a scripted turn.</summary>
    internal DateTimeOffset Now
    {
        get
        {
            var now = _time.GetUtcNow();
            return _state.LastActivity > now ? _state.LastActivity : now;
        }
    }

    private void Apply(SessionView next)
    {
        var previous = _state;
        _state = next;
        SyncTranscript();
        RaiseHeader(previous);
        UpdateApprovalAndQuestion(previous);
        UpdateComposer(previous);
        UpdatePanels(previous);
        UpdateStatus();
        UpdateTicker();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseHeader(SessionView previous)
    {
        if (previous.Title != _state.Title)
            OnPropertyChanged(nameof(Title));
        if (previous.ForkOf != _state.ForkOf || previous.ForkTurn != _state.ForkTurn)
        {
            OnPropertyChanged(nameof(ForkText));
            OnPropertyChanged(nameof(HasFork));
        }

        if (previous.Goal != _state.Goal)
        {
            OnPropertyChanged(nameof(Goal));
            OnPropertyChanged(nameof(GoalText));
            OnPropertyChanged(nameof(HasGoal));
            ClearGoalCommand.NotifyCanExecuteChanged();
        }

        if (previous.ReadOnly != _state.ReadOnly || previous.Holder != _state.Holder)
        {
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(ReadOnlyNote));
            SetGoalCommand.NotifyCanExecuteChanged();
            SetModeCommand.NotifyCanExecuteChanged();
        }

        if (previous.Approvals.Count != _state.Approvals.Count)
            OnPropertyChanged(nameof(PendingApprovals));
    }

    private void UpdateApprovalAndQuestion(SessionView previous)
    {
        var wasFocused = _currentApproval?.IsFocused == true;
        var answered = _currentApproval is not null && _state.CurrentApproval?.ApprovalId != _currentApproval.ApprovalId;
        CurrentApproval = _state.CurrentApproval is { } open && _blocks.TryGetValue(SessionFold.ApprovalId(open.ApprovalId), out var card)
            ? (ApprovalCardBlock)card
            : null;
        // A focused card that closed hands the keys back to the composer; the next card waits for its own first Enter.
        if (answered && wasFocused)
            FocusRequested?.Invoke(this, SessionFocusTarget.Composer);

        var question = _state.Question is { } asked && _blocks.TryGetValue(SessionFold.QuestionId(asked.QuestionId), out var block)
            ? (QuestionBlock)block
            : null;
        var opened = question is not null && !ReferenceEquals(question, _pendingQuestion);
        PendingQuestion = question;
        if (opened && previous.Question?.QuestionId != _state.Question?.QuestionId)
            FocusRequested?.Invoke(this, SessionFocusTarget.Composer);
    }

    /// <summary>Brings the blocks in line with the transcript, reusing every block whose entry is still there.</summary>
    private void SyncTranscript()
    {
        var entries = _state.Transcript.Where(Visible).ToList();
        var wanted = new HashSet<string>(entries.Select(entry => entry.Id), StringComparer.Ordinal);
        for (var i = Blocks.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(Blocks[i].Id))
                continue;
            _blocks.Remove(Blocks[i].Id);
            Blocks.RemoveAt(i);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i < Blocks.Count && Blocks[i].Id == entry.Id)
            {
                Blocks[i].Update(entry, _context);
                continue;
            }

            if (_blocks.TryGetValue(entry.Id, out var block))
            {
                Blocks.Move(Blocks.IndexOf(block), i);
                block.Update(entry, _context);
            }
            else
            {
                block = Create(entry);
                _blocks[entry.Id] = block;
                Blocks.Insert(i, block);
            }
        }
    }

    private bool Visible(TranscriptEntry entry) => entry is not DecisionsEntry decisions || decisions.Summary != _showAllDecisions;

    private TranscriptBlock Create(TranscriptEntry entry) => entry switch
    {
        UserEntry user => new UserMessageBlock(user, _context),
        TurnHeaderEntry header => new TurnHeaderBlock(header, _context),
        DecisionsEntry decisions => new DecisionsBlock(decisions, _context),
        SwitchEntry change => new SwitchBlock(change, _context),
        ToolEntry tool => new ToolCardBlock(tool, _context),
        ApprovalEntry approval => new ApprovalCardBlock(approval, _context),
        TaintEntry taint => new TaintBlock(taint, _context),
        AnswerEntry answer => new AnswerBlock(answer, _context),
        ThoughtEntry thought => new ThoughtBlock(thought, _context),
        NoticeEntry notice => new NoticeBlock(notice, _context),
        DetectionEntry detection => new DetectionBlock(detection, _context),
        CompactionEntry compaction => new CompactionBlock(compaction, _context),
        RewindEntry rewind => new RewindBlock(rewind, _context),
        QuestionEntry question => new QuestionBlock(question, _context),
        CommandEntry command => new CommandBlock(command, _context),
        _ => throw new NotSupportedException($"No block draws a {entry.GetType().Name}."),
    };

    /// <summary>Refreshes what the clock words: running times, the turn's elapsed time, the deadline.</summary>
    internal void Tick()
    {
        if (_disposed)
            return;
        foreach (var block in Blocks)
        {
            if (block.Turn == _state.Turn || block.Kind == TranscriptBlockKind.Approval)
                block.Tick(_context);
        }

        UpdateStrip();
        UpdateStatus();
    }

    private void UpdateTicker()
    {
        var needed = !_disposed && (_state.Running || !_state.Approvals.IsEmpty);
        if (needed && _ticker is null)
            _ticker = _time.CreateTimer(_ => _dispatcher.Post(Tick), null, TickInterval, TickInterval);
        else if (!needed && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    /// <summary>Whether a refresh timer is running (tests: no timer while idle).</summary>
    internal bool IsTicking => _ticker is not null;

    private void ToggleThoughts()
    {
        var thoughts = Blocks.OfType<ThoughtBlock>().ToList();
        var expand = thoughts.Any(thought => !thought.IsExpanded);
        foreach (var thought in thoughts)
            thought.IsExpanded = expand;
    }

    private void ExpandLatestOutput()
    {
        if (Blocks.OfType<ToolCardBlock>().LastOrDefault(card => card.CanExpand) is { } card)
        {
            card.IsExpanded = !card.IsExpanded;
            if (card.IsExpanded)
                ShowOutput(card);
        }
    }

    // ---- Actions the blocks take ----

    internal void Navigate(Area area) => _shell?.Navigate(area);

    /// <summary>Follows a link into another area about this session (Audit's egress ledger, a detection's events).</summary>
    internal void Follow(AreaLinkTarget target, IReadOnlyList<long>? events = null, string? name = null) =>
        _shell?.Follow(new AreaLink(Area.Audit, SessionId, target) { Events = events ?? [], Name = name });

    internal void OpenOtherSession(string sessionId) => _shell?.OpenSession(sessionId);

    internal void Interrupt() => Run(() => _service.InterruptAsync(SessionId));

    internal void AnswerQuestion(string questionId, string answer) =>
        Run(() => _service.AnswerQuestionAsync(SessionId, questionId, answer));

    internal void AnswerApproval(ApprovalCardBlock card, ApprovalAnswer answer) =>
        Run(() => _service.AnswerApprovalAsync(SessionId, card.ApprovalId, answer), card.AnswerFailed);

    private void Fork(long? position) => Run(async () =>
    {
        var forked = await _service.ForkAsync(SessionId, position).ConfigureAwait(true);
        _shell?.OpenSession(forked);
    });

    /// <summary>Runs a request; a refusal shows in <see cref="Error"/> rather than escaping into the UI loop.</summary>
    private async void Run(Func<Task> request, Action? failed = null)
    {
        try
        {
            Error = "";
            await request().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = exception.Message;
            failed?.Invoke();
        }
    }
}
