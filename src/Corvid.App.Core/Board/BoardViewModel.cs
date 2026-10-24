using System.Collections.ObjectModel;
using System.ComponentModel;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Board;

/// <summary>Which sessions the Board shows (the header's segmented control).</summary>
public enum BoardFilter
{
    All,
    Running,
    Waiting,
    Jobs,
}

/// <summary>
/// The Board area (docs/plan/ui.md §4.13, F18; Board.dc.html): every session as a card with its state, step, model and
/// taint, filtered by the header's segments, and the attention tray beside them listing everything waiting on the user
/// across sessions, each item opening its full card.
/// <para>
/// Sessions come from <see cref="SessionHub.Summaries"/> and <see cref="ISessionService.SessionChanged"/>, and each one's
/// folded state through <see cref="ISessionWatcher"/> (tool calls, staged memory, the open question's options, a peek).
/// The other areas' attention items and the engine's state come from <see cref="IBoardService"/>. While the Board shows,
/// the status bar is the Board's: the engine, the decider, the mode, the scheduler, the gateway and how many sessions run
/// and wait. The bell counts the areas' items under <see cref="DemoShell.AreasAttention"/>; the hub counts the sessions'.
/// </para>
/// </summary>
public sealed class BoardViewModel : AreaViewModel, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private const string Unavailable = "Starting sessions and opening workspaces come with the engine (Corvid.App.Engine).";

    private const string NotifyUnavailable = "Native notifications while the window is unfocused come with the engine (Corvid.App.Engine).";

    private readonly ShellViewModel _shell;
    private readonly StatusBarLease _lease;
    private readonly SessionHub _hub;
    private readonly IBoardService _service;
    private readonly ISessionWatcher _watcher;
    private readonly TimeProvider _time;
    private readonly IDispatcher _dispatcher;
    private readonly Dictionary<string, SessionCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _watches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _watching = new(StringComparer.Ordinal);
    private IReadOnlyList<AttentionItem> _areaItems = [];
    private BoardStatus? _status;
    private BoardFilter _filter = BoardFilter.All;
    private string _counts = "";
    private string _attentionHead = "Attention";
    private string? _failure;
    private BoardPeekViewModel? _peek;
    private bool _notifyWhenUnfocused;
    private ITimer? _ticker;
    private bool _disposed;

    public BoardViewModel(ShellViewModel shell, SessionHub hub, IBoardService service, ISessionWatcher watcher, TimeProvider time, IDispatcher dispatcher)
        : base(Area.Board, "Every session as a card with its state, step, model and taint, and the attention tray beside them.")
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _shell = shell;
        _lease = new StatusBarLease(shell.StatusBar);
        _hub = hub;
        _service = service;
        _watcher = watcher;
        _time = time;
        _dispatcher = dispatcher;

        Filters = [.. Enum.GetValues<BoardFilter>().Select(filter =>
            new Choice(FilterLabel(filter), FilterHelp(filter), new RelayCommand(() => Filter = filter)))];
        Filters[0].IsSelected = true;
        NewSessionCommand = new RelayCommand(() => { }, () => false);
        OpenWorkspaceCommand = new RelayCommand(() => { }, () => false);
        ClosePeekCommand = new RelayCommand(ClosePeek, () => _peek is not null);
        // Nothing sends native notifications yet, so the toggle is off and says why, as New session does.
        ToggleNotifyCommand = new RelayCommand(() => NotifyWhenUnfocused = !NotifyWhenUnfocused, () => false);
        NewSession = new NewSessionTile(NewSessionText(_shell.Workspace), NewSessionCommand, OpenWorkspaceCommand);

        _hub.Service.SessionChanged += OnSessionChanged;
        _hub.Listed += OnListed;
        _service.AttentionChanged += OnAttentionChanged;
        _shell.PropertyChanged += OnShellChanged;
        foreach (var summary in _hub.Summaries)
            Upsert(summary);
        if (_shell.CurrentArea == Area.Board)
            _lease.Take();
        Rebuild();
        _ = LoadAsync();
    }

    /// <summary>The cards the filter lets through, in the Board's order, then the dashed card when the filter is All.</summary>
    public ObservableCollection<BoardTile> Tiles { get; } = [];

    /// <summary>Every session's card by id, whatever the filter.</summary>
    public IReadOnlyDictionary<string, SessionCardViewModel> Cards => _cards;

    public NewSessionTile NewSession { get; private set; }

    /// <summary>The header's segments: all, running, waiting on you, jobs.</summary>
    public IReadOnlyList<Choice> Filters { get; }

    public BoardFilter Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value))
                return;
            foreach (var (choice, filter) in Filters.Zip(Enum.GetValues<BoardFilter>()))
                choice.IsSelected = filter == value;
            Rebuild();
        }
    }

    /// <summary>The header's counts: "5 sessions · 2 workspaces · 1 job run".</summary>
    public string Counts { get => _counts; private set => SetProperty(ref _counts, value); }

    /// <summary>What the filter leaves when it leaves no card: one line on what would show here.</summary>
    public string EmptyText => _filter switch
    {
        BoardFilter.Running => "Nothing is running. A session shows here while a turn runs.",
        BoardFilter.Waiting => "Nothing waits on you. Approvals and questions show here as they arrive.",
        BoardFilter.Jobs => "No job has run yet. Jobs shows the schedule.",
        _ => "No sessions yet.",
    };

    public bool IsEmpty => !Tiles.OfType<SessionCardViewModel>().Any() && _filter != BoardFilter.All;

    /// <summary>The tray: the sessions' approvals and questions in the Board's order, then the areas' items.</summary>
    public ObservableCollection<AttentionItemViewModel> Attention { get; } = [];

    /// <summary>"Attention · 6".</summary>
    public string AttentionHead { get => _attentionHead; private set => SetProperty(ref _attentionHead, value); }

    public bool HasAttention => Attention.Count > 0;

    /// <summary>The open peek; null when none is.</summary>
    public BoardPeekViewModel? Peek
    {
        get => _peek;
        private set
        {
            if (!SetProperty(ref _peek, value))
                return;
            OnPropertyChanged(nameof(IsPeeking));
            ClosePeekCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsPeeking => _peek is not null;

    /// <summary>Native notifications for the tray's items while the window is unfocused (docs/plan/ui.md §4.5).</summary>
    public bool NotifyWhenUnfocused
    {
        get => _notifyWhenUnfocused;
        set
        {
            if (SetProperty(ref _notifyWhenUnfocused, value))
                OnPropertyChanged(nameof(NotifyLabel));
        }
    }

    public string NotifyLabel => _notifyWhenUnfocused ? "Notifying when unfocused" : "Notify when unfocused";

    public RelayCommand NewSessionCommand { get; }

    public RelayCommand OpenWorkspaceCommand { get; }

    /// <summary>Why New session and Open a workspace are disabled.</summary>
    public string UnavailableReason => Unavailable;

    /// <summary>Why "Notify when unfocused" is disabled.</summary>
    public string NotifyUnavailableReason => NotifyUnavailable;

    public RelayCommand ClosePeekCommand { get; }

    public RelayCommand ToggleNotifyCommand { get; }

    /// <summary>Opens a session: attaches it as the current one in Sessions (a read-only one shows as a live view there).</summary>
    public void Open(string sessionId) => _hub.Show(sessionId);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hub.Service.SessionChanged -= OnSessionChanged;
        _hub.Listed -= OnListed;
        _service.AttentionChanged -= OnAttentionChanged;
        _shell.PropertyChanged -= OnShellChanged;
        _ticker?.Dispose();
        _peek?.Dispose();
        foreach (var watch in _watches.Values)
            watch.Dispose();
        _watches.Clear();
    }

    private async Task LoadAsync()
    {
        try
        {
            _areaItems = await _service.ListAttentionAsync().ConfigureAwait(true);
            _status = await _service.GetStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _areaItems = [];
            _status = new BoardStatus("unreachable", "", "", "");
            _failure = $"could not load: {SafeText.Clean(exception.Message)}";
        }

        RebuildAttention();
        ApplyStatus();
    }

    // ---- Sessions ----

    private void OnSessionChanged(object? sender, SessionSummary summary)
    {
        if (_disposed)
            return;
        Upsert(summary);
        Rebuild();
    }

    /// <summary>The hub's start-up list arrived after this was made (the engine lists once its runtime is up).</summary>
    private void OnListed(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        foreach (var summary in _hub.Summaries)
            Upsert(summary);
        Rebuild();
    }

    private void Upsert(SessionSummary summary)
    {
        if (_cards.TryGetValue(summary.Id, out var card))
        {
            card.Update(summary);
        }
        else
        {
            card = new SessionCardViewModel(summary, _time.GetUtcNow(), _time.LocalTimeZone, card => Open(card.Id), OpenPeek,
                // A job run's link to Jobs names the run, so Jobs lands on its job with the run marked.
                () => _shell.Follow(new AreaLink(Area.Jobs, summary.Id)));
            _cards[summary.Id] = card;
            card.PropertyChanged += OnCardChanged;
            _ = WatchAsync(card);
        }
    }

    private async Task WatchAsync(SessionCardViewModel card)
    {
        if (!_watching.Add(card.Id))
            return;
        try
        {
            var watch = await _watcher.WatchAsync(card.Id, view => _dispatcher.Post(() => card.Update(view))).ConfigureAwait(true);
            if (_disposed)
                watch.Dispose();
            else
                _watches[card.Id] = watch;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The card keeps its summary's lines; only what the log adds (tool calls, staged memory, options) is missing.
            _watching.Remove(card.Id);
        }
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A question's options arrive with the session's state.
        if (e.PropertyName == nameof(SessionCardViewModel.View))
            RebuildAttention();
    }

    private IEnumerable<SessionCardViewModel> Ordered() =>
        _cards.Values.OrderBy(card => BoardText.Rank(card.Summary)).ThenByDescending(card => card.Summary.LastActivity).ThenBy(card => card.Id, StringComparer.Ordinal);

    private bool Passes(SessionCardViewModel card) => _filter switch
    {
        BoardFilter.Running => card.Summary.State == SessionState.Running,
        BoardFilter.Waiting => card.Summary.State == SessionState.Waiting,
        BoardFilter.Jobs => card.IsJob,
        _ => true,
    };

    private void Rebuild()
    {
        var wanted = new List<BoardTile>(Ordered().Where(Passes));
        if (_filter == BoardFilter.All)
            wanted.Add(NewSession);
        Sync(Tiles, wanted);
        Counts = BoardText.Counts(_cards.Values.Select(card => card.Summary).ToList());
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        RebuildAttention();
        ApplyStatus();
        UpdateTicker();
    }

    /// <summary>Reorders, inserts and removes only what changed, so a card that stays in place keeps its element and focus.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var at = target.IndexOf(item);
            if (at == i)
                continue;
            // A move is a removal and an insertion: Uno re-templates a moved item's container outside the page's theme,
            // and an inserted one inside it.
            if (at >= 0)
                target.RemoveAt(at);
            target.Insert(i, item);
        }

        while (target.Count > wanted.Count)
            target.RemoveAt(target.Count - 1);
    }

    // ---- The attention tray ----

    private void OnAttentionChanged(object? sender, EventArgs e) => _ = ReloadAttentionAsync();

    private async Task ReloadAttentionAsync()
    {
        try
        {
            _areaItems = await _service.ListAttentionAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failure = $"could not load: {SafeText.Clean(exception.Message)}";
            RebuildAttention();
            return;
        }

        RebuildAttention();
    }

    private void RebuildAttention()
    {
        var now = _time.GetUtcNow();
        var wanted = new List<AttentionItemViewModel>();
        foreach (var card in Ordered())
        {
            var summary = card.Summary;
            if (summary.PendingApprovals > 0)
                wanted.Add(Reuse(ApprovalItem(summary, now)));
            if (summary.PendingQuestions > 0)
                wanted.Add(Reuse(QuestionItem(summary, card.View)));
        }

        foreach (var item in _areaItems)
            wanted.Add(Reuse(AreaItem(item)));
        Sync(Attention, wanted);
        AttentionHead = _failure is null ? $"Attention · {Attention.Count}" : $"Attention · {Attention.Count} · {_failure}";
        OnPropertyChanged(nameof(HasAttention));
        // The bell counts the sessions' items itself (SessionHub); the areas' items are reported here.
        _shell.SetAttention(DemoShell.AreasAttention, _areaItems.Count);
    }

    /// <summary>An item already listed keeps its view model (and its row) with the new text and buttons.</summary>
    private AttentionItemViewModel Reuse(AttentionItemViewModel fresh)
    {
        // A question's id is in its item's id, so buttons that answer an earlier question are never kept for a new one.
        var existing = Attention.FirstOrDefault(item => item.Id == fresh.Id && item.Kind == fresh.Kind);
        if (existing is null)
            return fresh;
        existing.What = fresh.What;
        existing.Detail = fresh.Detail;
        if (!existing.Buttons.Select(button => button.Label).SequenceEqual(fresh.Buttons.Select(button => button.Label)))
            existing.Buttons = fresh.Buttons;
        return existing;
    }

    private AttentionItemViewModel ApprovalItem(SessionSummary summary, DateTimeOffset now)
    {
        var left = summary.Deadline is { } deadline ? $"{SessionText.Countdown(deadline - now)} left" : null;
        return new AttentionItemViewModel($"approval:{summary.Id}", AttentionKind.Approval,
            BoardText.Join("Approval", SafeText.Clean(summary.Approval)),
            BoardText.Join(BoardText.ShortTitle(summary.Title), left, "opens the full card"),
            [new AttentionButton("Open card", AttentionActionStyle.Default, "Open the session at its approval card: the parsed command, reasons and diff",
                new RelayCommand(() => Open(summary.Id)), isLink: true)])
        {
            SessionId = summary.Id,
        };
    }

    private AttentionItemViewModel QuestionItem(SessionSummary summary, SessionView? view)
    {
        var question = view?.Question;
        var buttons = new List<AttentionButton>();
        if (question is not null)
        {
            foreach (var option in question.Options)
            {
                var questionId = question.QuestionId;
                // One click sends the option as the answer, so its label shows every hidden character (SafeText.Reveal);
                // the answer sent is the option as the session gave it.
                var shown = SafeText.Reveal(option);
                buttons.Add(new AttentionButton(shown, AttentionActionStyle.Default, $"Answer \"{shown}\"",
                    new RelayCommand(() => _ = AnswerAsync(summary.Id, questionId, option))));
            }
        }

        buttons.Add(new AttentionButton("Answer…", AttentionActionStyle.Ghost, "Open the session to answer in your own words",
            new RelayCommand(() => Open(summary.Id))));
        var id = question is null ? $"question:{summary.Id}" : $"question:{summary.Id}:{question.QuestionId}";
        return new AttentionItemViewModel(id, AttentionKind.Question,
            BoardText.Join("Question", SafeText.Clean(summary.Question)),
            BoardText.Join(BoardText.ShortTitle(summary.Title), question is not null ? $"\"{SafeText.Clean(question.Text)}\"" : null),
            buttons)
        {
            SessionId = summary.Id,
        };
    }

    private async Task AnswerAsync(string sessionId, string questionId, string answer)
    {
        try
        {
            await _hub.Service.AnswerQuestionAsync(sessionId, questionId, answer).ConfigureAwait(true);
            _failure = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failure = $"could not answer: {SafeText.Clean(exception.Message)}";
        }

        RebuildAttention();
    }

    private AttentionItemViewModel AreaItem(AttentionItem item)
    {
        // An area's item can carry outside names (an MCP tool's, a pairing handle): drawn cleaned, since its buttons grant
        // nothing here; they lead to the area's card, which reveals what it grants. Their labels are the area's own words.
        var buttons = item.Actions.Select(action => new AttentionButton(action.Label, action.Style, SafeText.Clean(action.Help),
            new RelayCommand(() => Act(item, action)), action.IsLink)).ToList();
        return new AttentionItemViewModel(item.Id, item.Kind, SafeText.Clean(item.What), SafeText.Clean(item.Detail), buttons);
    }

    private void Act(AttentionItem item, AttentionAction action)
    {
        switch (action.Kind)
        {
            // A target is followed as a link, so the area lands on it (the held tool's card, the pairing's question).
            case AttentionActionKind.Navigate when action.Area is { } area && action.Target is { } target:
                _shell.Follow(new AreaLink(area, null) { Name = target });
                break;
            case AttentionActionKind.Navigate when action.Area is { } area:
                _shell.Navigate(area);
                break;
            case AttentionActionKind.OpenSession when action.SessionId is { } session:
                Open(session);
                break;
            case AttentionActionKind.Resolve:
                _ = ResolveAsync(item, action.Id);
                break;
        }
    }

    /// <summary>
    /// Answers an area's item. A refusal leaves the item listed and says why in the tray's head. Rail badges are not the
    /// tray's to write: each area counts its own (MCP's held tools come from the MCP service).
    /// </summary>
    private async Task ResolveAsync(AttentionItem item, string actionId)
    {
        try
        {
            await _service.ResolveAsync(item.Id, actionId).ConfigureAwait(true);
            _failure = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failure = $"could not answer: {SafeText.Clean(exception.Message)}";
        }

        RebuildAttention();
    }

    // ---- Peek ----

    private void OpenPeek(SessionCardViewModel card)
    {
        _peek?.Dispose();
        Peek = new BoardPeekViewModel(card, ClosePeekCommand);
    }

    private void ClosePeek()
    {
        _peek?.Dispose();
        Peek = null;
    }

    // ---- The shell: status bar, workspace, clock ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentArea):
                if (_shell.CurrentArea == Area.Board)
                {
                    _lease.Take();
                    // The clock stood still while the Board was hidden: bring the times up to now.
                    Tick();
                    ApplyStatus();
                }
                else
                {
                    // An area that writes no status of its own shows what the Board's replaced, not the Board's.
                    _lease.Release();
                }

                UpdateTicker();
                break;
            case nameof(ShellViewModel.Workspace):
                NewSession = new NewSessionTile(NewSessionText(_shell.Workspace), NewSessionCommand, OpenWorkspaceCommand);
                OnPropertyChanged(nameof(NewSession));
                Rebuild();
                break;
        }
    }

    /// <summary>
    /// The Board's status bar (Board.dc.html): the engine and its workspaces, the decider, the mode, the scheduler, the
    /// gateway, and how many sessions run and wait. Written only while the Board shows; leaving puts back what it replaced.
    /// </summary>
    private void ApplyStatus()
    {
        if (_shell.CurrentArea != Area.Board || _status is not { } status)
            return;
        _lease.Take();
        var summaries = _cards.Values.Select(card => card.Summary).ToList();
        var bar = _lease;
        var workspaces = BoardText.Workspaces(summaries);
        bar.Engine = SafeText.Clean(BoardText.Join(status.Engine, workspaces > 0 ? BoardText.Plural(workspaces, "workspace") : null));
        bar.Generator = "";
        bar.Decider = SafeText.Clean(status.Decider);
        bar.Context = "";
        bar.AreaFields = [];
        bar.Tags = [];
        bar.Scheduler = SafeText.Clean(status.Scheduler);
        bar.Gateway = SafeText.Clean(status.Gateway);
        bar.Branch = "";
        bar.Trailing = BoardText.Activity(summaries);
    }

    private static IReadOnlyList<MarkdownRun> NewSessionText(string workspace) =>
        workspace.Length == 0
            ? [new MarkdownRun("Start another session, or open a workspace. Each workspace runs its own engine until workspace-per-session lands (G-4).")]
            :
            [
                new MarkdownRun("Start another session in "),
                new MarkdownRun(SafeText.Clean(workspace), InlineStyle.Bold),
                new MarkdownRun(", or open a workspace. Each workspace runs its own engine until workspace-per-session lands (G-4)."),
            ];

    /// <summary>The 1 s tick moves countdowns and times on while the Board shows and a session runs or waits.</summary>
    private void UpdateTicker()
    {
        var needed = !_disposed && _shell.CurrentArea == Area.Board
            && _cards.Values.Any(card => card.Summary.State is SessionState.Running or SessionState.Waiting);
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
        var now = _time.GetUtcNow();
        foreach (var card in _cards.Values)
        {
            if (card.Summary.State is SessionState.Running or SessionState.Waiting)
                card.Tick(now);
        }

        RebuildAttention();
    }

    private static string FilterLabel(BoardFilter filter) => filter switch
    {
        BoardFilter.Running => "running",
        BoardFilter.Waiting => "waiting on you",
        BoardFilter.Jobs => "jobs",
        _ => "all",
    };

    private static string FilterHelp(BoardFilter filter) => filter switch
    {
        BoardFilter.Running => "Sessions with a turn running",
        BoardFilter.Waiting => "Sessions waiting on an approval or a question",
        BoardFilter.Jobs => "Scheduled jobs' runs",
        _ => "Every session and job run",
    };
}
