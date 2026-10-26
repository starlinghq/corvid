using System.Collections.Concurrent;
using System.Collections.Immutable;
using Corvid.Abstractions.Sessions;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.Gateway;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using AppMode = Corvid.App.Core.Shell.ApprovalMode;
using LoggedAnswer = Corvid.Abstractions.Events.AssistantMessage;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;
using McpSessions = Corvid.Abstractions.Events.McpSessions;
using TitleSources = Corvid.Abstractions.Events.TitleSources;
using PipelineMode = Corvid.Approvals.ApprovalMode;
using SessionState = Corvid.App.Core.Shell.SessionState;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// The window's sessions over the in-process runtime: the event log's conversations as summaries, a session opened as its
/// replayed history followed by the live updates of the turns this window sends, and every request of
/// <see cref="ISessionService"/> the runtime can honour. Every callback reaches the views through the dispatcher, on the
/// UI thread; nothing here waits on it.
/// <para>
/// Each opened session has one channel: its updates so far (the replay translated, then every live update appended), the
/// state they fold to, and its subscribers. A second open of the same session gets the channel's updates as its history, so
/// two views of one session (the Sessions page and the Board's watch) always agree, and live updates follow exactly after
/// it. The history joins each run of streamed chunks into one. A channel nobody watches and no turn of the window's uses
/// is let go, and the session's next open replays the log (the hub keeps the sessions it opened, so this is the Board's
/// watch letting go). A session's summary for the rail and the Board is the log's, with the open channel's live state over
/// it (running, waiting on an approval with its deadline, the step and what runs).
/// </para>
/// <para>
/// A job's run (<c>job:…</c>) is the scheduler's: read-only here, as the demo's run is, and every request that would write to
/// it is refused.
/// </para>
/// <para>
/// The queue is the engine's (messages posted to the session actor cannot be taken back): Enter while a turn runs queues,
/// and when the turn ends the first queued message starts the next. Approvals go to the card: a prompter is attached to
/// the session for each turn the window runs (<see cref="CardPrompter"/>), and an answer completes the pipeline's prompt.
/// Ctrl+Enter steers the running turn (K-3) and a clarifying question waits for the person's answer (K-6), holding the
/// queue meanwhile (EngineSessionService.Turn.cs); the turn's steps and running tails come on its report.
/// </para>
/// <para>
/// Turns another process or surface runs in a session show when the session is opened again. Titles, recaps, pins, drafts
/// and the composer's <c>!</c> and <c>#</c> lines are in <c>EngineSessionService.Records.cs</c> (A4).
/// </para>
/// </summary>
public sealed partial class EngineSessionService : ISessionService, IDisposable
{
    /// <summary>The surface the window's turns are posted from; new sessions it makes are <c>app-…</c>.</summary>
    public const string Surface = "app";

    private const int TitleLength = 80;

    private readonly AppEngine _engine;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _work = new();
    // Forks made by this window, by the fork's id: the log copies events, not where they came from.
    private readonly ConcurrentDictionary<string, (string From, int AtTurn)> _forks = new(StringComparer.Ordinal);
    // Sessions this window started empty (a fork before the first message), listed until they hold an event.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _created = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public EngineSessionService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _dispatcher = dispatcher;
        _time = time;
        _engine.Started += OnEngineStarted;
        _ = WatchViewsAsync();
        _ = WatchJobsAsync();
        _ = WatchRecordsAsync();
        _ = WatchLogAsync();
    }

    public event EventHandler<SessionSummary>? SessionChanged;

    /// <summary>Raised once a runtime that could not start at first (models missing) is up: the window lists its sessions.</summary>
    public event EventHandler? ListChanged;

    /// <summary>The runtime started after the window opened (First run's downloads): the scheduler's runs are heard, and the list read again.</summary>
    private void OnEngineStarted(CorvidRuntime runtime)
    {
        if (_disposed)
            return;
        AddViewer(runtime);
        _ = WatchJobsAsync();
        _ = WatchLogAsync();
        _dispatcher.Post(() =>
        {
            if (!_disposed)
                ListChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private readonly Lock _viewerGate = new();
    private IDisposable? _viewer;
    private CorvidRuntime? _viewerOf;

    /// <summary>
    /// A6 fix: tells the runtime which sessions the window views, so removing a worktree lets go of an idle session here
    /// that the window holds but does not show (one the Board made), and never of one it shows.
    /// </summary>
    private async Task WatchViewsAsync()
    {
        try
        {
            AddViewer(await _engine.Runtime.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A runtime that could not start: OnEngineStarted adds the viewer to the one that starts later.
        }
    }

    private void AddViewer(CorvidRuntime runtime)
    {
        lock (_viewerGate)
        {
            if (_disposed || ReferenceEquals(_viewerOf, runtime))
                return;
            _viewer?.Dispose();
            _viewer = runtime.AddViewer(IsViewed);
            _viewerOf = runtime;
        }
    }

    /// <summary>The turns and summary refreshes in flight, for a test or a shutdown that waits for them; a turn the queue starts counts.</summary>
    public async Task IdleAsync()
    {
        while (!_work.IsEmpty)
            await Task.WhenAll(_work.Keys).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        // The log is SQLite, read synchronously: off the caller's thread, which is the UI's.
        return await Task.Run(() =>
        {
            var summaries = new List<SessionSummary>();
            var pinned = Pins();
            var heads = new Dictionary<string, long>(StringComparer.Ordinal);
            // Read before the heads, so a write between the two is seen as a change at the next look.
            var marker = runtime.Log.ChangeMarker(cancellationToken);
            foreach (var (id, head) in runtime.Log.ListHeads(cancellationToken).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsConversation(id))
                    continue;
                heads[id] = head;
                if (Summarize(runtime, id, pinned) is { } summary)
                    summaries.Add(Live(id, summary));
            }
            // G-12: what other processes write from here on reaches the list at the log's next look.
            Listed(heads, marker);
            // A6 review: a new session's log holds where it works from the start; with nothing else it is still new.
            foreach (var (id, created) in _created)
                if (SessionCatalogue.Describe(runtime.Log, id, cancellationToken) is null)
                    summaries.Add(Live(id, Empty(runtime, id, created)));
            return (IReadOnlyList<SessionSummary>)[.. summaries.OrderByDescending(summary => summary.LastActivity)];
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var subscription = new Subscription(this, channel, onUpdate);
            lock (channel.Gate)
            {
                // Dropped while idle between the load and here: open the fresh one.
                if (channel.Closed)
                    continue;
                subscription.History = channel.Updates;
                channel.Subscribers.Add(subscription);
            }
            return subscription;
        }
    }

    /// <summary>Sends a message, starting a turn; while one runs it is queued behind it, as Enter does.</summary>
    public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        QueueAsync(sessionId, text, cancellationToken);

    /// <summary>
    /// Queues a message behind the running turn; with none running it starts one at once. A message still in the queue
    /// with nothing running (its start failed) goes first: the new one queues behind it, never past it.
    /// </summary>
    public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        EnqueueAsync(sessionId, text, modeLines: true, cancellationToken);

    /// <param name="modeLines">Read a <c>!</c> or <c>#</c> line as the composer's mode (<see cref="ComposerLineAsync"/>); false
    /// for a question's answer, which is sent as given.</param>
    /// <param name="images">K-8 (A6): the images the message carries, checked by <see cref="SendAsync(string, string, IReadOnlyList{ImageAttachment}, CancellationToken)"/>.</param>
    private async Task<QueuedMessage> EnqueueAsync(string sessionId, string text, bool modeLines, CancellationToken cancellationToken,
        ImmutableArray<ImageAttachment> images = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        images = images.IsDefault ? [] : images;
        if (images.IsEmpty)
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // K-8: a ! or # line is a mode, not a message: it takes no images.
        if (!images.IsEmpty && modeLines && ComposerLine.Parse(text) is not null)
            throw new InvalidOperationException("A ! or # line cannot carry images; send them with a message. Nothing was sent.");
        // A4: a # line is staged at once, never queued; a ! line queues as a turn of its own (StartTurnAsync posts it). A
        // question's answer is sent as given.
        if (modeLines && await ComposerLineAsync(sessionId, text, cancellationToken).ConfigureAwait(false) is { } handled)
            return handled;
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var tools = await _engine.ToolsAsync().ConfigureAwait(false);
        while (true)
        {
            var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            Writable(channel);
            var shellLine = modeLines && ComposerLine.Parse(text) is ComposerLine.Shell;
            QueuedMessage message;
            QueuedMessage? head = null;
            bool start;
            lock (channel.Gate)
            {
                // Dropped while idle between the load and here: a fresh channel takes the message.
                if (channel.Closed)
                    continue;
                // With a question open the next message is its answer: a ! line would run its command as that answer, so it
                // is refused here as AnswerQuestionAsync refuses it; read under the channel's lock, as the start below reads
                // it (A4 verification).
                if (shellLine && channel.View?.Question is not null)
                    throw new InvalidOperationException("A question is open: answer it first, then send the ! line. Nothing was sent.");
                message = new QueuedMessage($"q-{++channel.QueueCounter}", text.Trim()) { Images = images };
                start = !channel.Running;
                // K-6: with a question open the session waits on this message, the person's answer: it starts now, and what
                // was queued before (typed before the question showed) waits behind it rather than answering it.
                if (start && (channel.Queue.IsEmpty || channel.View?.Question is not null))
                {
                    channel.Running = true;
                }
                else
                {
                    channel.Queue = channel.Queue.Add(message);
                    if (start)
                    {
                        // The queue's head goes first; this message waits behind it.
                        channel.Running = true;
                        head = channel.Queue[0];
                        channel.Queue = channel.Queue.RemoveAt(0);
                    }
                }
            }
            if (start && head is null)
            {
                try
                {
                    await StartTurnAsync(channel, runtime, tools, message.Text, cancellationToken, verbatim: !modeLines, images: message.Images).ConfigureAwait(false);
                }
                catch
                {
                    // The message was not sent (the caller keeps it); what queued behind it while it started goes next.
                    HandOff(channel);
                    throw;
                }
            }
            else
            {
                PublishQueue(channel);
                if (head is not null)
                    Track(StartQueuedAsync(channel, head));
            }
            return message;
        }
    }

    public async Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default)
    {
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // K-3: a steer chip is taken back from the turn too (or, already taken, it is in the transcript).
        var withdrawn = WithdrawSteerAsync(channel, queuedId);
        if (ChangeQueue(channel, queue => queue.FirstOrDefault(item => item.Id == queuedId) is { } item ? queue.Remove(item) : queue))
            PublishQueue(channel);
        await withdrawn.ConfigureAwait(false);
    }

    public async Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default)
    {
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (ChangeQueue(channel, queue =>
            {
                if (queue.FirstOrDefault(item => item.Id == queuedId) is not { } item)
                    return queue;
                var without = queue.Remove(item);
                return without.Insert(Math.Clamp(index, 0, without.Length), item);
            }))
            PublishQueue(channel);
    }

    public async Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default)
    {
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // K-3: a steer the turn has already taken is not given back (it was sent); its chip just goes. The chip leaves the
        // queue before the turn's answer is awaited, so nothing else sends it meanwhile.
        var withdrawn = WithdrawSteerAsync(channel, queuedId);
        QueuedMessage? taken = null;
        if (ChangeQueue(channel, queue =>
            {
                taken = queue.FirstOrDefault(item => item.Id == queuedId);
                return taken is null ? queue : queue.Remove(taken);
            }))
            PublishQueue(channel);
        var delivered = await withdrawn.ConfigureAwait(false);
        return delivered ? null : taken?.Text;
    }

    /// <summary>Tests only: awaited by an interrupt once it has marked the window cancelling, before it cancels the turn.</summary>
    internal Func<Task>? InterruptCancelling { get; set; }

    /// <summary>
    /// Interrupts the window's running turn: the work so far stays, open prompts close as cancelled, and the queue goes next.
    /// A turn whose message the session has not begun yet (it is starting, or waits behind another surface's turn) is
    /// cancelled as it begins, so the interrupt never reaches a turn the window did not start.
    /// </summary>
    public async Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        if (!_channels.TryGetValue(sessionId, out var channel))
            return;
        bool begun;
        lock (channel.Gate)
        {
            if (!channel.Running)
                return;
            channel.Cancelling = true;
            begun = channel.TurnBegun;
        }
        if (InterruptCancelling is { } hook)
            await hook().ConfigureAwait(false);
        // The turn's token first, so the pipeline records each open prompt as cancelled rather than denied; then the cards close.
        if (begun && runtime.TryGetSession(sessionId, out var session))
            session.Cancel();
        ClosePending(channel, "interrupted");
    }

    public async Task SetApprovalModeAsync(string sessionId, AppMode mode, CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        (await SessionAsync(runtime, sessionId, cancellationToken).ConfigureAwait(false)).ApprovalMode = mode switch
        {
            AppMode.Manual => PipelineMode.Manual,
            AppMode.Permissive => PipelineMode.Permissive,
            _ => PipelineMode.Smart,
        };
        Publish(channel, _ => [new ApprovalModeChanged(mode) { At = _time.GetUtcNow() }]);
    }

    public async Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        var session = await SessionAsync(runtime, sessionId, cancellationToken).ConfigureAwait(false);
        var kept =string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
        // A GoalSet event in the log: SQLite, off the UI thread.
        await Task.Run(() => session.SetGoal(kept), cancellationToken).ConfigureAwait(false);
        Publish(channel, _ => [new GoalChanged(kept) { At = _time.GetUtcNow() }]);
    }

    public async Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        lock (channel.Gate)
            if (channel.Running)
                throw new InvalidOperationException("Compaction runs between turns; this one is still running.");
        var session = await SessionAsync(runtime, sessionId, cancellationToken).ConfigureAwait(false);
        var focused = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();
        var result = await Task.Run(() => session.CompactAsync(focused, cancellationToken), cancellationToken).ConfigureAwait(false);
        Publish(channel, translator => [Compaction(translator, result, focused)]);
    }

    /// <summary>
    /// Forks the session into a new one. A position is a message's (the fork point "edit and resend" and "branch from here"
    /// pass): the fork holds everything before that message, so the message can be sent again, changed or not. Null forks
    /// at the head; while a turn runs, at its last completed step, without waiting for it (G-15).
    /// </summary>
    public async Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var source = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var through = throughPosition is { } position ? position - 1 : (long?)null;
        string id;
        if (through is <= 0)
        {
            // Before the first message there is nothing to copy: a new, empty session. Opening one writes to the log: off
            // the UI thread.
            id = await Task.Run(() => runtime.OpenSession(surface: Surface).Id, cancellationToken).ConfigureAwait(false);
            _created[id] = _time.GetUtcNow();
        }
        else
        {
            // A source not open in the runtime is copied inline, event by event, in SQLite: off the UI thread.
            var fork = await Task.Run(() => runtime.ForkSessionAsync(sessionId, through, Surface, cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            id = fork.Id;
        }
        int turn;
        lock (source.Gate)
            turn = through is { } last ? source.Translator.TurnAt(last) : source.Translator.Turn;
        _forks[id] = (sessionId, turn);
        Refresh(runtime, id);
        return id;
    }

    public bool CanCreate => true;

    /// <summary>A new, empty session in the runtime's workspace, listed as a new session until it holds a message.</summary>
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        // Opening a session writes to the log: off the UI thread.
        var id = await Task.Run(() => runtime.OpenSession(surface: Surface).Id, cancellationToken).ConfigureAwait(false);
        _created[id] = _time.GetUtcNow();
        Refresh(runtime, id);
        return id;
    }

    /// <summary>
    /// Stops every callback: subscribers hear nothing more, open prompts are denied, and turns still running finish in the
    /// runtime (whose own disposal ends them) without reporting here. Never waits: it runs on the UI thread.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _engine.Started -= OnEngineStarted;
        lock (_viewerGate)
        {
            _viewer?.Dispose();
            _viewer = null;
        }
        UnwatchJobs();
        UnwatchRecords();
        StopWatchingLog();
        StopTails();
        foreach (var id in _childPrompters.Keys)
            if (_childPrompters.TryRemove(id, out var child))
                child.Dispose();
        foreach (var channel in _channels.Values)
        {
            PendingApproval[] open;
            IDisposable? feed, prompter;
            lock (channel.Gate)
            {
                channel.Subscribers.Clear();
                open = [.. channel.Pending.Values];
                (feed, prompter) = (channel.Feed, channel.Prompter);
                (channel.Feed, channel.Prompter) = (null, null);
            }
            // The turn's feed subscription and prompter go with the window; a turn still running reports to nobody, and
            // its prompts fail closed in the pipeline (no surface attached).
            feed?.Dispose();
            prompter?.Dispose();
            foreach (var pending in open)
                pending.Answer.TrySetResult(null);
        }
    }

    /// <summary>
    /// A compaction's result as the transcript shows it: the marker with its record over the turns it replaced, or a notice
    /// saying why nothing was compacted.
    /// </summary>
    private SessionUpdate Compaction(SessionUpdateTranslator translator, Corvid.Kernel.Context.CompactionResult result, string? focus)
    {
        var at = _time.GetUtcNow();
        if (result.Status != Corvid.Kernel.Context.CompactionStatus.Compacted)
            return new Notice(result.Status == Corvid.Kernel.Context.CompactionStatus.NothingToCompact ? NoticeKind.Info : NoticeKind.Warning,
                result.Status == Corvid.Kernel.Context.CompactionStatus.NothingToCompact ? "Nothing to compact yet." : "The conversation was not compacted.")
            {
                Detail = SafeText.Clean(result.Describe()),
                At = at,
            };
        var record = result.Record;
        return new CompactionUpdate(Math.Max(1, translator.TurnAt(result.From)), Math.Max(1, translator.TurnAt(result.Through)))
        {
            At = at,
            Goal = record?.Goal,
            Constraints = record?.Constraints.IsDefault == false ? record.Constraints : [],
            Decisions = record?.Decisions.IsDefault == false ? record.Decisions : [],
            Files = record?.Files.IsDefault == false ? record.Files : [],
            OpenQuestions = record?.OpenQuestions.IsDefault == false ? record.OpenQuestions : [],
            NextStep = record?.NextStep,
            Summarizer = result.Summarizer,
            Focus = focus,
        };
    }

    // ---- Turns ----

    /// <summary>
    /// Starts a turn for a channel already marked running: the reporter on the feed, the card's prompter attached, the
    /// message posted. Returns once the session's actor has it; the turn's end is tracked by <see cref="FinishTurnAsync"/>.
    /// A turn that cannot start lets go of what it attached and throws; the caller hands the session on (<see cref="HandOff"/>).
    /// </summary>
    /// <param name="verbatim">Send the text as a message even when it reads as a <c>!</c> line (a question's answer).</param>
    private async Task StartTurnAsync(Channel channel, CorvidRuntime runtime, ToolPresentation tools, string text, CancellationToken cancellationToken,
        bool verbatim = false, ImmutableArray<ImageAttachment> images = default)
    {
        var sessionId = channel.SessionId;
        IDisposable? feed = null, prompter = null;
        try
        {
            await PauseAt("start").ConfigureAwait(false);
            // Opening the session and reading the head touch the log (SQLite): off the caller's thread, which is the UI's.
            var (session, head) = await Task.Run(() => (Session(runtime, sessionId), runtime.Log.GetHead(sessionId, cancellationToken)), cancellationToken)
                .ConfigureAwait(false);
            // A6 (G-4): the approval cards show the calls in the session's own workspace.
            tools = tools.In(session.Workspace);
            SessionUpdateReporter? reporter = null;
            reporter = new SessionUpdateReporter(runtime, sessionId, tools, head, update => Report(channel, session, reporter!, update),
                reason => Log("corvid app: could not report a session update: " + reason), _time, () => session.Tainted);
            lock (channel.Gate)
            {
                // A5: the session's own generator (its choice for the session lasts past the turn), not only the runtime's default.
                channel.Translator.Model = CurrentGenerator(runtime, session);
                // A4V2-5: a ! line's turn runs its command, no generator.
                channel.Translator.ShellLine = !verbatim && ComposerLine.Parse(text) is ComposerLine.Shell;
                channel.Translator.MaxSteps = runtime.Options.Turn.MaxSteps;
                channel.Translator.ContextWindow = ContextWindow(runtime, session);
                channel.TurnBegun = false;
                channel.Sent = text;
                channel.Reporter = reporter;
            }
            feed = runtime.Feed.Subscribe(sessionId, reporter);
            // A5: and the turn's ask_user questions, answered inside the turn while this window runs it.
            prompter = new Attached(runtime.AttachPrompter(sessionId, new CardPrompter(this, channel, runtime, tools, reporter)),
                runtime.AttachQuestioner(sessionId, new WindowQuestioner(this, channel, reporter)));
            // Kept on the channel too, so closing the window lets go of them even while the turn runs on.
            lock (channel.Gate)
                (channel.Feed, channel.Prompter) = (feed, prompter);
            var started = _time.GetTimestamp();
            // A session's first message is its title until the local generator writes one after the first exchange (K-9) or
            // the person renames it (G-6), as the rail shows it.
            if (Line(text) is { Length: > 0 } title)
                Publish(channel, _ => channel.View is { Title.Length: > 0 } ? [] : [new TitleChanged(title) { At = _time.GetUtcNow() }]);
            await PauseAt("post").ConfigureAwait(false);
            // Returns once the session's actor has the message (or the ! line's command); the turn runs on, and its end is
            // tracked below.
            var delivery = await PostLineAsync(session, text, verbatim, cancellationToken, images).ConfigureAwait(false);
            Track(FinishTurnAsync(channel, reporter, delivery, feed, prompter, started));
            feed = prompter = null;
        }
        catch
        {
            lock (channel.Gate)
                (channel.Feed, channel.Prompter) = (null, null);
            feed?.Dispose();
            prompter?.Dispose();
            feed = prompter = null;
            throw;
        }
        finally
        {
            feed?.Dispose();
            prompter?.Dispose();
        }
        Refresh(runtime, sessionId);
    }

    /// <summary>
    /// Waits for the turn's delivery, ends its report and its turn, and gives the session to the first queued message, or
    /// back for the next one.
    /// </summary>
    private async Task FinishTurnAsync(Channel channel, SessionUpdateReporter reporter, Task<Delivery> delivery, IDisposable feed,
        IDisposable prompter, long started)
    {
        TurnOutcome outcome;
        string? error = null;
        try
        {
            var done = await delivery.ConfigureAwait(false);
            await reporter.FinishAsync(done.Result?.Text).ConfigureAwait(false);
            // A turn that called tools (memory_add) or whose remember checkpoint wrote may have staged memory (A4V2-3).
            if (done.Result is { MemoryWrite: not null } or { ToolCalls: > 0 })
                _engine.NotifyMemoryChanged();
            (outcome, error) = done switch
            {
                { Error: { } failure } => (TurnOutcome.Failed, failure.Message),
                { Result: { } result } => (Map(result.Status), null),
                _ => (TurnOutcome.Failed, "The turn ended without a result."),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await reporter.DrainAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            (outcome, error) = (exception is OperationCanceledException ? TurnOutcome.Cancelled : TurnOutcome.Failed, exception.Message);
        }
        finally
        {
            lock (channel.Gate)
                if (ReferenceEquals(channel.Feed, feed))
                    (channel.Feed, channel.Prompter) = (null, null);
            feed.Dispose();
            prompter.Dispose();
        }

        // K-3: every steer has its answer once the delivery is in: taken ones leave the queue, the rest go to its head.
        await SettleSteersAsync(channel).ConfigureAwait(false);
        await PauseAt("settled").ConfigureAwait(false);
        var elapsed = _time.GetElapsedTime(started);
        bool interrupted;
        lock (channel.Gate)
            interrupted = channel.Cancelling;
        // A prompt the turn left open (its cancellation raced the prompt's own) closes before the turn does.
        ClosePending(channel, interrupted ? "interrupted" : "the turn ended");
        Publish(channel, translator =>
        {
            var ended = translator.EndTurn(outcome, _time.GetUtcNow(), elapsed, error ?? (interrupted && outcome == TurnOutcome.Cancelled ? "interrupted" : null));
            // A message that never reached the log opened no turn; its failure still shows.
            return ended.Count > 0 || error is null
                ? ended
                : [new Notice(NoticeKind.Warning, "The message was not sent.") { Detail = SafeText.Clean(error), At = _time.GetUtcNow() }];
        });

        HandOff(channel);
        // G-7: what the turn of a session the person is looking at did has been seen.
        if (_viewing.ContainsKey(channel.SessionId))
            Seen(channel.SessionId);
        if (_engine.Runtime.IsCompletedSuccessfully)
        {
            Refresh(_engine.Runtime.Result, channel.SessionId);
            // The dock's Changes and the Review badge: what the turn's calls changed, from the write ledger.
            PublishChanges(_engine.Runtime.Result, channel);
        }
    }

    /// <summary>
    /// The turn is over (it ended, or it could not start): the session goes to the first queued message, staying running
    /// for it, or back for the next one. The one place a turn lets go of the session, so the queue's order always holds.
    /// <paramref name="next"/> false lets go without starting the queue (a queued message that could not start is back at its
    /// head, and retrying it at once could fail without end: the next message sent starts it first).
    /// </summary>
    private void HandOff(Channel channel, bool next = true)
    {
        QueuedMessage? head = null;
        lock (channel.Gate)
        {
            channel.Reporter = null;
            channel.Cancelling = false;
            channel.TurnBegun = false;
            channel.OpenCalls.Clear();
            DropStaleSteers(channel);
            // K-6: an open question holds the queue: only the person's answer goes next, never a message queued before it.
            if (next && !_disposed && !channel.Queue.IsEmpty && channel.View?.Question is null)
            {
                head = channel.Queue[0];
                channel.Queue = channel.Queue.RemoveAt(0);
            }
            else
            {
                channel.Running = false;
            }
        }
        if (head is null)
        {
            // Idle with nobody watching: the channel goes, and the next open replays the log.
            DropIfIdle(channel);
            return;
        }
        PublishQueue(channel);
        Track(StartQueuedAsync(channel, head));
    }

    /// <summary>
    /// Starts the queued message's turn. One that cannot start goes back to the head of the queue and the session is let go
    /// (<see cref="HandOff"/>), so the next message sent starts it first; the transcript says why.
    /// </summary>
    private async Task StartQueuedAsync(Channel channel, QueuedMessage message)
    {
        try
        {
            var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
            var tools = await _engine.ToolsAsync().ConfigureAwait(false);
            await StartTurnAsync(channel, runtime, tools, message.Text, CancellationToken.None, images: message.Images).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ChangeQueue(channel, queue => queue.Insert(0, message));
            // Let go before saying so: a message sent once the notice shows starts the one put back.
            HandOff(channel, next: false);
            Publish(channel, _ =>
            [
                new Notice(NoticeKind.Warning, "The queued message could not be sent; it is back in the queue.")
                {
                    Detail = SafeText.Clean(exception.Message),
                    At = _time.GetUtcNow(),
                },
                new QueueChanged(channel.Queue) { At = _time.GetUtcNow() },
            ]);
        }
    }

    /// <summary>
    /// A live update of the turn, translated; a taint the reporter notes shows when it differs from the window's. The turn's first
    /// prompt is the session beginning the window's message: an interrupt asked for before it is carried out now.
    /// </summary>
    private void Report(Channel channel, CorvidSession session, SessionUpdateReporter reporter, SessionStreamUpdate update)
    {
        var cancel = false;
        // Only the window's own message begins its turn: another surface may post between the head the reporter reads from
        // and the window's post, and its turn runs first; an interrupt asked for meanwhile must not reach it. The log keeps a
        // user's message as it was sent (it is not scrubbed), so its text tells the two apart.
        if (update is PromptSent prompt)
        {
            lock (channel.Gate)
            {
                if (ReferenceEquals(channel.Reporter, reporter) && !channel.TurnBegun && string.Equals(prompt.Text, channel.Sent, StringComparison.Ordinal))
                {
                    channel.TurnBegun = true;
                    cancel = channel.Cancelling;
                }
            }
        }
        // Before the prompt shows, so nothing of the interrupted turn runs on after the person sees it begin.
        if (cancel)
            session.Cancel();
        // The kernel taints the session when a network tool's result or outside content enters (plan §10.3), and clears it
        // when the user's own message starts a turn. The reporter reads it in order with the turn and says so after the
        // update it came with (a call's result names the call); read here instead, as the chain got to an update, a busy
        // machine showed a call's taint on the call's start (no source), or before it.
        if (update is TaintNoted noted)
        {
            Publish(channel, translator =>
            {
                if (noted.Tainted == channel.Tainted)
                    return [];
                channel.Tainted = noted.Tainted;
                if (noted.Tainted)
                    channel.TaintSource = noted.Source;
                return translator.Translate(noted);
            });
            return;
        }
        Publish(channel, translator => translator.Translate(update));
    }

    private static TurnOutcome Map(TurnStatus status) => status switch
    {
        TurnStatus.Answered => TurnOutcome.Answered,
        TurnStatus.Clarifying => TurnOutcome.Clarifying,
        TurnStatus.Stuck => TurnOutcome.Stuck,
        TurnStatus.StepBudgetExhausted => TurnOutcome.StepBudgetExhausted,
        TurnStatus.Cancelled => TurnOutcome.Cancelled,
        _ => TurnOutcome.Failed,
    };

    // ---- Channels ----

    /// <summary>
    /// The session's channel, its history replayed from the log on first use (once, however many ask at once). A channel
    /// dropped while idle (<see cref="DropIfIdle"/>) is never handed out: the next one replays the log again.
    /// </summary>
    private async Task<Channel> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var tools = await _engine.ToolsAsync().ConfigureAwait(false);
        while (true)
        {
            var channel = _channels.GetOrAdd(sessionId, id => new Channel(id, new SessionUpdateTranslator(tools.WorkspaceRoot) { IsChildRunning = runtime.IsChildRunning }));
            Task loading;
            lock (channel.Gate)
            {
                if (channel.Closed)
                    continue;
                loading = channel.Loading ??= Task.Run(() => ReplayAsync(runtime, tools, channel), CancellationToken.None);
            }
            try
            {
                await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed replay is not kept: the next open tries again.
                lock (channel.Gate)
                    if (channel.Loading == loading)
                        channel.Loading = null;
                throw;
            }
            lock (channel.Gate)
                if (!channel.Closed)
                    return channel;
        }
    }

    /// <summary>
    /// Whether a replay leaves its last turn open: the window's own turn runs, or a turn of the session runs here or in the
    /// process that drives it and the log does not end on its answer yet. A turn the log shows answered is over even while
    /// the actor still counts it running: the actor hands a turn's delivery to its sink before it marks the turn finished,
    /// so a window opened in between used to leave the answered turn open (no <see cref="TurnEnded"/>).
    /// </summary>
    internal static bool ReplayEndsRunning(bool windowRunning, bool runningHere, bool elsewhere, IReadOnlyList<SessionStreamUpdate> history)
    {
        var answered = history.Count > 0 && Answered(history);
        return windowRunning || (runningHere || elsewhere && history.Count > 0) && !answered;
    }

    private async Task ReplayAsync(CorvidRuntime runtime, ToolPresentation tools, Channel channel)
    {
        var id = channel.SessionId;
        var open = runtime.TryGetSession(id, out var session);
        if (runtime.Log.GetHead(id) == 0 && !open)
            throw new KeyNotFoundException($"There is no session '{id}' in Corvid's log.");
        // A6 (G-4): the session's own workspace: its calls' paths are shown relative to it, and the header names it.
        tools = tools.ForSessionOrSelf(id);
        var bound = runtime.WorkspaceOf(id);
        lock (channel.Gate)
            channel.Translator.Workspace = tools.WorkspaceRoot;
        // A call with no result is cancelled only when no turn of the session is running: one opened mid-turn (a job
        // run from the Board, another surface's turn) is still working on it.
        // G-12: a session another process drives is followed read-only; its open calls are that process's, still running.
        var driven = IsJobRun(id) ? Corvid.Host.Composition.LockState.Free : runtime.Driver(id).State;
        var elsewhere = driven == Corvid.Host.Composition.LockState.HeldElsewhere;
        var runningHere = runtime.IsTurnRunning(id);
        // A5: the log's model switches (their rows, the per-step model and the slots' state) and its children. A child runs in
        // its parent's turn, here or not: its open calls are still running, as another process's are.
        var options = elsewhere || IsChild(id) && runningHere ? TailOptions(0)
            : (runningHere ? ReplayOptions.Full with { UnfinishedCalls = false } : ReplayOptions.Full) with { Models = true, Children = true };
        var history = await SessionReplay.ReadAsync(runtime, id, tools, options).ConfigureAwait(false);
        var goal = Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, id).Goal;
        var summary = Summarize(runtime, id) ?? Empty(runtime, id, _time.GetUtcNow());
        var described = SessionCatalogue.Describe(runtime.Log, id);
        var commands = Commands();
        var mode = Mode(open ? session.ApprovalMode : runtime.Options.Approvals.Mode);
        var changes = await ChangesAsync(runtime, id, CancellationToken.None).ConfigureAwait(false);
        await PauseAt("replay").ConfigureAwait(false);
        lock (channel.Gate)
        {
            var at = summary.LastActivity;
            var updates = new List<SessionUpdate>();
            // A job's run is the scheduler's: the window shows it as it ran and sends nothing into it.
            if (summary.ReadOnly)
                updates.Add(new DriverChanged(true) { Holder = summary.Holder, At = at });
            // A5: the replayed usage fills the context gauge against the window of the generator the next turn starts on.
            channel.Translator.ContextWindow ??= ContextWindow(runtime, open ? session : null);
            foreach (var update in history.Updates)
                updates.AddRange(channel.Translator.Translate(update));
            // A turn running in this runtime that the window did not start (a job's run, another surface's) is still running,
            // not interrupted: its header stays open until the session is opened again. So is one another process drives
            // whose log does not end on an answer yet (G-12).
            updates.AddRange(channel.Translator.EndReplay(ReplayEndsRunning(channel.Running, runningHere, elsewhere, history.Updates)));
            updates.Add(changes);
            // The rail's title (a rename, the generated title, else the first message) heads the session too.
            if (summary.Title != id && (summary.Turns > 0 || described?.Titled is not null))
                updates.Add(new TitleChanged(summary.Title) { At = at, Generated = described?.Titled?.Source == TitleSources.Generated });
            // G-6: the latest recap, when nothing has happened since it.
            if (RecapNotice(described, at) is { } recap)
                updates.Add(recap);
            // What the session is beside its transcript: where it was forked from (this window's forks only), its goal,
            // its approval mode, whether outside content is in its context, and the commands the composer offers.
            if (_forks.TryGetValue(id, out var fork))
                updates.Add(new SessionForked(fork.From, fork.AtTurn) { At = at });
            // A5: a child links its parent.
            if (summary.ParentId is { } parentId)
                updates.Add(new ChildOfSession(parentId, summary.Child?.Goal ?? "") { At = at });
            if (goal is not null)
                updates.Add(new GoalChanged(goal) { At = at });
            updates.Add(new ApprovalModeChanged(mode) { At = at });
            // The replay's taint rows show what the log holds (network reads); a session open here knows its own taint, which
            // also counts an untrusted description, a hub skill or a guest's message.
            var tainted = open ? session.Tainted : channel.Translator.Tainted;
            if (tainted != channel.Translator.Tainted)
                updates.Add(new TaintChanged(tainted) { At = at });
            channel.Tainted = tainted;
            // The status bar's generator before a turn runs: what the next one starts on.
            // A5: with the session's decider (per session now), for the header beside the generator; for a session not open
            // here, the choices its log holds, which it starts on when it opens.
            var choices = open ? null : runtime.ModelChoices(id);
            var decider = choices?.GetValueOrDefault(Corvid.Models.ModelSlots.Decider)?.Split('@')[0] ?? DeciderLabel(runtime, open ? session : null);
            // A6 (G-4): with where the session works, for the brand line and the status bar's branch.
            var (workspaceName, branch) = (WorkspaceText.Name(bound.Path), bound.Branch ?? WorkspaceText.Branch(bound.Path));
            updates.Add((choices?.GetValueOrDefault(Corvid.Models.ModelSlots.Generator)?.Split('@')[0] ?? CurrentGenerator(runtime, open ? session : null)) is { Length: > 0 } generator
                ? new EngineStatus { Model = generator, Decider = decider, Workspace = workspaceName, Branch = branch, At = at }
                : new EngineStatus { Decider = decider, Workspace = workspaceName, Branch = branch, At = at });
            updates.Add(new AvailableCommandsUpdate(commands) { At = at });
            channel.ReadOnly = summary.ReadOnly;
            channel.Holder = summary.Holder;
            foreach (var update in updates)
                Keep(channel, update);
            channel.LogSummary = summary;
            channel.Loaded = true;
            // What was published while the log was read (a restore from Rewind) follows the history it came after.
            var deferred = channel.Deferred.ToArray();
            channel.Deferred.Clear();
            foreach (var translate in deferred)
                foreach (var update in translate(channel.Translator))
                    Keep(channel, update);
        }
        // G-12: follow what another process appends to a session this one does not drive, and who drives it.
        if (!IsJobRun(id) && (driven != Corvid.Host.Composition.LockState.HeldHere || IsChild(id)))
            StartTail(runtime, tools, channel, history.Head);
    }

    /// <summary>
    /// Translates under the channel's lock, keeps the result as history and posts it to every subscriber in order. While the
    /// channel's history is still being read from the log the translation waits, and runs after the replay, in order.
    /// </summary>
    private void Publish(Channel channel, Func<SessionUpdateTranslator, IReadOnlyList<SessionUpdate>> translate)
    {
        if (_disposed)
            return;
        SessionSummary? changed = null;
        lock (channel.Gate)
        {
            if (!channel.Loaded)
            {
                channel.Deferred.Add(translate);
                return;
            }
            var updates = translate(channel.Translator);
            if (updates.Count == 0)
                return;
            foreach (var update in updates)
                Keep(channel, update);
            foreach (var subscriber in channel.Subscribers)
                subscriber.Post(updates);
            if (channel.LogSummary is { } logged && Overlay(logged, channel) is var summary && summary != channel.Raised)
                changed = channel.Raised = summary;
        }
        if (changed is not null)
            Raise(changed);
    }

    /// <summary>
    /// Keeps an update in the channel's history and folds it, under the channel's lock. A streamed chunk joins the chunk
    /// before it (keeping that one's time), so the history grows by run, not by token; the open calls are tracked for the
    /// summary's activity rather than searched for in the transcript.
    /// </summary>
    private static void Keep(Channel channel, SessionUpdate update)
    {
        var updates = channel.Updates;
        channel.Updates = updates.Count > 0 && (updates[^1], update) switch
        {
            (AgentMessageChunk last, AgentMessageChunk next) => updates.SetItem(updates.Count - 1, last with { Text = last.Text + next.Text }),
            (AgentThoughtChunk last, AgentThoughtChunk next) => updates.SetItem(updates.Count - 1, last with { Text = last.Text + next.Text }),
            // K-2: a running call's tail replaces the one before it, so the history keeps the latest, not every report.
            (ToolCallUpdate last, ToolCallUpdate next) when last.CallId == next.CallId && TailOnly(last) && TailOnly(next)
                => updates.SetItem(updates.Count - 1, next with { At = last.At }),
            _ => null,
        } is { } merged
            ? merged
            : updates.Add(update);
        channel.View = SessionFold.Apply(channel.View ?? SessionFold.Start(channel.SessionId), update);
        var calls = channel.OpenCalls;
        switch (update)
        {
            case TurnStarted or TurnEnded:
                calls.Clear();
                break;
            case ToolCall call:
                var index = calls.FindIndex(open => open.CallId == call.CallId);
                if (call.Status is ToolCallStatus.Pending or ToolCallStatus.InProgress)
                {
                    var target = call.Target ?? (index >= 0 ? calls[index].Target : null);
                    if (index >= 0)
                        calls[index] = (call.CallId, call.Tool, target);
                    else
                        calls.Add((call.CallId, call.Tool, target));
                }
                else if (index >= 0)
                {
                    calls.RemoveAt(index);
                }
                break;
            case ToolCallUpdate change:
                var at = calls.FindIndex(open => open.CallId == change.CallId);
                if (at < 0)
                    break;
                if (change.Status is { } status && status is not (ToolCallStatus.Pending or ToolCallStatus.InProgress))
                    calls.RemoveAt(at);
                else if (change.Target is { } changed)
                    calls[at] = calls[at] with { Target = changed };
                break;
        }
    }

    /// <summary>A running call's tail and nothing else (K-2).</summary>
    private static bool TailOnly(ToolCallUpdate update) =>
        update is { Tail: not null, Output: null, Outcome: null, Summary: null, Verdict: null, Elapsed: null }
        && update.Diffs.IsDefaultOrEmpty;

    /// <summary>
    /// Lets go of a channel nobody watches and no turn of the window's uses (nothing queued, no prompt open): its history
    /// goes with it, and the session's next open replays the log. What the window keeps beside the log (its forks, the
    /// sessions it made) lives outside the channel.
    /// </summary>
    private void DropIfIdle(Channel channel)
    {
        lock (channel.Gate)
        {
            if (channel.Closed || !channel.Loaded || channel.Subscribers.Count > 0 || channel.Running || !channel.Queue.IsEmpty
                || channel.Pending.Count > 0)
                return;
            channel.Closed = true;
        }
        StopTail(channel);
        _channels.TryRemove(new KeyValuePair<string, Channel>(channel.SessionId, channel));
        // M2 (A4 review): a session this window drove is let go of once nothing of it is open here, so a terminal's
        // `corvid chat --session` can drive it; the next request here opens it again. Not a gateway chat's or another surface's,
        // and not a new one with nothing in the log yet (it lives only in the runtime until its first message).
        if (_engine.Runtime is { IsCompletedSuccessfully: true } started && started.Result.TryGetSession(channel.SessionId, out var session)
            && session.Surface == Surface)
        {
            var runtime = started.Result;
            var id = channel.SessionId;
            Track(Task.Run(async () =>
            {
                if (SessionCatalogue.Describe(runtime.Log, id) is not null && !_channels.ContainsKey(id))
                    await runtime.ReleaseDriverAsync(id).ConfigureAwait(false);
            }));
        }
    }

    /// <summary>
    /// Whether the window views the session now (it has its channel), so a restore that opened it does not let go of it:
    /// the window's own requests use its handle, and it lets go itself once it stops viewing it (A4 verification).
    /// </summary>
    internal bool IsViewed(string sessionId) => _channels.TryGetValue(sessionId, out var channel) && !channel.Closed;

    /// <summary>The channels this window holds, for a test of their release.</summary>
    internal int ChannelCount => _channels.Count;

    /// <summary>Tests only: awaited at the named points where another request can come between two steps.</summary>
    internal Func<string, Task>? Pause { get; set; }

    private Task PauseAt(string point) => Pause?.Invoke(point) ?? Task.CompletedTask;

    /// <summary>
    /// A restore from Rewind reached the session (A3 integration): an open session's transcript and change lists follow it
    /// as the demo's do, and a conversation rollback lowers the turn the next prompt opens. A session not open in this
    /// window shows the conversation as it stands when it is opened.
    /// </summary>
    internal void PublishRewound(string sessionId, SessionRewound rewound)
    {
        ArgumentNullException.ThrowIfNull(rewound);
        if (!_channels.TryGetValue(sessionId, out var channel))
            return;
        Publish(channel, translator =>
        {
            if (!rewound.Refused && rewound.Scope != RewindScope.FilesOnly)
                translator.RewindTo(rewound.Turn);
            return [rewound];
        });
        // The change lists as the ledger now stands: a file the restore put back drops out, one an earlier call changed stays.
        if (!rewound.Refused && _engine.Runtime.IsCompletedSuccessfully)
            PublishChanges(_engine.Runtime.Result, channel);
    }

    private void PublishQueue(Channel channel) => Publish(channel, _ => [new QueueChanged(channel.Queue) { At = _time.GetUtcNow() }]);

    /// <summary>Changes the queue under the channel's lock; true when it changed.</summary>
    private static bool ChangeQueue(Channel channel, Func<ImmutableArray<QueuedMessage>, ImmutableArray<QueuedMessage>> change)
    {
        lock (channel.Gate)
        {
            var next = change(channel.Queue);
            if (next == channel.Queue)
                return false;
            channel.Queue = next;
            return true;
        }
    }

    // ---- Summaries ----

    /// <summary>Reads the session's summary again and raises <see cref="SessionChanged"/> with it on the UI thread.</summary>
    private void Refresh(CorvidRuntime runtime, string sessionId) => Track(Task.Run(() =>
    {
        if (_disposed)
            return;
        var summary = Summarize(runtime, sessionId)
            ?? (_created.TryGetValue(sessionId, out var created) ? Empty(runtime, sessionId, created) : null);
        if (summary is null)
            return;
        if (_channels.TryGetValue(sessionId, out var channel))
        {
            lock (channel.Gate)
            {
                channel.LogSummary = summary;
                summary = channel.Raised = Overlay(summary, channel);
            }
        }
        Raise(summary);
    }));

    private void Raise(SessionSummary summary) => _dispatcher.Post(() =>
    {
        if (!_disposed)
            SessionChanged?.Invoke(this, summary);
    });

    /// <summary>The log's summary with an open channel's live state over it.</summary>
    private SessionSummary Live(string id, SessionSummary logged)
    {
        if (!_channels.TryGetValue(id, out var channel))
            return logged;
        lock (channel.Gate)
            return channel.View is null ? logged : Overlay(logged, channel);
    }

    /// <summary>
    /// What the rail, the Board and the attention tray read of a session this window has open: running or waiting on the
    /// user, the step and what runs, the first open approval with its deadline, taint, and the fork it came from.
    /// </summary>
    private static SessionSummary Overlay(SessionSummary logged, Channel channel)
    {
        var view = channel.View;
        if (view is null)
            return logged;
        var running = channel.Running && view.Running;
        // An approval waits inside the running turn; a clarifying question (K-6) waits after the turn that asked it.
        var waiting = running && view.Approvals.Count > 0 || view.Question is not null && !running;
        (string CallId, string Tool, string? Target)? tool = running && channel.OpenCalls.Count > 0 ? channel.OpenCalls[^1] : null;
        return logged with
        {
            // A session with no message in the log yet ("New session", or its id) is titled by the first message the window
            // sent, while that message's turn has not begun or still runs.
            Title = logged.Turns == 0 && view.Title is { Length: > 0 } title ? title : logged.Title,
            State = waiting ? SessionState.Waiting : running ? SessionState.Running : logged.State,
            LastActivity = view.LastActivity > logged.LastActivity ? view.LastActivity : logged.LastActivity,
            StepStarted = running ? view.ActivitySince : null,
            Deadline = view.Approvals.Count > 0 ? view.Approvals[0].Deadline : null,
            Step = running ? view.Step : null,
            MaxSteps = running ? view.MaxSteps : null,
            Activity = waiting ? "waiting on you" : tool is { } call ? $"{call.Tool} {call.Target}".Trim() : null,
            Model = running && view.Model is { Length: > 0 } model ? model : logged.Model,
            Tainted = logged.Tainted || view.Tainted,
            ForkOf = view.ForkOf ?? logged.ForkOf,
            ForkTurn = view.ForkTurn ?? logged.ForkTurn,
            PendingApprovals = view.Approvals.Count,
            Approval = view.Approvals.Count > 0 ? view.Approvals[0].Text : null,
            PendingQuestions = view.Question is null ? 0 : 1,
            Question = view.Question is { } question ? question.Title ?? question.Text : null,
        };
    }

    private SessionSummary? Summarize(CorvidRuntime runtime, string id, IReadOnlySet<string>? pinned = null)
    {
        // G-6: the title every surface shows (a rename, the generated title, else the first message), from the host.
        if (SessionCatalogue.Describe(runtime.Log, id) is not { } described)
            return null;
        var (title, preview, turns, last) = (described.Title, described.Preview, described.Turns, described.LastActivity);
        // K-6: the question the session ended on, while no message has answered it.
        var question = described.OpenQuestion?.Text;
        var open = runtime.TryGetSession(id, out var session);
        var job = IsJobRun(id) ? JobOf(runtime, id) : default;
        // G-12: another process drives it (a terminal's corvid chat, an editor over ACP): shown read-only, saying which.
        var driver = job.Name is null ? runtime.Driver(id) : null;
        var drivenElsewhere = driver is { State: Corvid.Host.Composition.LockState.HeldElsewhere };
        if (drivenElsewhere)
            WatchHeld(runtime, id);
        // A5: a child is its parent's record: read-only here, shown under its parent.
        var child = ChildFacts(runtime, id);
        // A session this window made holding no message yet (a fork before the first one) is new, not its id.
        return new SessionSummary(id, title ?? (_created.ContainsKey(id) || _forks.ContainsKey(id) ? "New session" : id))
        {
            Child = child?.Child,
            ParentId = child?.ParentId,
            // G-7: pinned in ui.db, by this window or the CLI's /pin.
            Pinned = (pinned ?? Pins()).Contains(id),
            // A job's run is the scheduler's: read-only here, as the demo's run is, and listed as the job's.
            ReadOnly = job.Name is not null || drivenElsewhere || child is not null,
            Holder = job.Name is not null ? JobHolder : child is { } of ? ChildHolder(of.ParentId) : drivenElsewhere ? DriverHolder(driver!.Holder) : null,
            Job = job.Name,
            Schedule = job.Schedule,
            RunAt = job.Name is not null ? job.RunAt ?? last : null,
            // A6 (G-4): where this session works, with the branch of the worktree Corvid made for it.
            Workspace = runtime.WorkspaceOf(id).Path,
            WorkspaceGone = !Directory.Exists(runtime.WorkspaceOf(id).Path),
            Branch = runtime.WorkspaceOf(id).Branch ?? "",
            Surface = SurfaceOf(id),
            State = IsJobRun(id) ? SessionState.Job : question is not null ? SessionState.Waiting : SessionState.Idle,
            PendingQuestions = question is null || IsJobRun(id) ? 0 : 1,
            Question = question is null || IsJobRun(id) ? null : SessionUpdateTranslator.QuestionTitle(question),
            Activity = question is null || IsJobRun(id) ? null : "waiting on you",
            LastActivity = last,
            Turns = turns,
            Tainted = open && session.Tainted,
            Preview = preview,
            ForkOf = _forks.TryGetValue(id, out var fork) ? fork.From : null,
            ForkTurn = _forks.TryGetValue(id, out var at) ? at.AtTurn : null,
        };
    }

    /// <summary>
    /// The job a run's session belongs to (<c>job:&lt;job id&gt;:&lt;run id&gt;</c>, as the job store names it): its name and
    /// schedule, and when the run started; the job's id when the store no longer has it.
    /// </summary>
    private static (string? Name, string? Schedule, DateTimeOffset? RunAt) JobOf(CorvidRuntime runtime, string id)
    {
        var parts = id.Split(':', 3);
        var jobId = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : id;
        try
        {
            var store = runtime.Builtin.Jobs;
            var job = store.Get(jobId);
            var run = parts.Length > 2 ? store.GetRun(parts[2]) : null;
            return (job?.Name ?? jobId, job?.Schedule, run?.StartedAt);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The store unreadable: still the scheduler's run, read-only.
            return (jobId, null, null);
        }
    }

    /// <summary>A session this window started with nothing in it yet.</summary>
    private SessionSummary Empty(CorvidRuntime runtime, string id, DateTimeOffset created) => new(id, "New session")
    {
        Pinned = Pins().Contains(id),
        Workspace = runtime.WorkspaceOf(id).Path,
        WorkspaceGone = !Directory.Exists(runtime.WorkspaceOf(id).Path),
        Branch = runtime.WorkspaceOf(id).Branch ?? "",
        Surface = SurfaceOf(id),
        State = SessionState.Idle,
        LastActivity = created,
        ForkOf = _forks.TryGetValue(id, out var fork) ? fork.From : null,
        ForkTurn = _forks.TryGetValue(id, out var at) ? at.AtTurn : null,
    };

    private static string? Line(string? text)
    {
        if (text is null)
            return null;
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > TitleLength ? line[..(TitleLength - 1)] + "…" : line;
    }

    /// <summary>Where the session was started, from its id's prefix: <c>cli-…</c>, <c>acp-…</c>, <c>gateway:…</c>, <c>job:…</c>.</summary>
    internal static string SurfaceOf(string id)
    {
        var end = id.IndexOfAny(['-', ':']);
        return end > 0 ? id[..end] : "cli";
    }

    /// <summary>
    /// The conversations a person had, not the stores' own sessions (MCP pins, the gateway's delivery ledger, the session
    /// Memory's Hermes import stages its facts from).
    /// </summary>
    private static bool IsConversation(string id) => id is not (McpSessions.Pins or GatewaySessions.Ledger or Memory.EngineMemoryService.ImportSession);

    // ---- Helpers ----

    /// <summary>
    /// The session's handle in the runtime, opened for this window when no surface has it open yet. Opening one writes to
    /// the log: call it off the UI thread (<see cref="SessionAsync"/>).
    /// </summary>
    private CorvidSession Session(CorvidRuntime runtime, string sessionId)
    {
        RefuseChild(sessionId);
        if (runtime.TryGetSession(sessionId, out var session))
            return session;
        try
        {
            return runtime.OpenSession(sessionId, Surface);
        }
        catch (Corvid.Host.Composition.SessionHeldException exception)
        {
            // G-12: another process took it since it was loaded: the view turns read-only and follows it.
            Held(sessionId, exception);
            throw;
        }
    }

    /// <summary><see cref="Session"/> on the pool: the request methods are called on the UI thread.</summary>
    private Task<CorvidSession> SessionAsync(CorvidRuntime runtime, string sessionId, CancellationToken cancellationToken) =>
        !IsChild(sessionId) && runtime.TryGetSession(sessionId, out var session)
            ? Task.FromResult(session)
            : Task.Run(() => Session(runtime, sessionId), cancellationToken);

    /// <summary>A job's run (<c>job:&lt;job&gt;:&lt;run&gt;</c>): the scheduler's session, which the window only shows.</summary>
    internal static bool IsJobRun(string id) => id.StartsWith("job:", StringComparison.Ordinal);

    /// <summary>
    /// Refuses a request that would write to a session the window only shows: a job's run is the scheduler's, kept as it
    /// ran, and a message, a mode, a goal or a command from the window would change it (or, while the run goes on, attach
    /// the window's approval card to the scheduler's turn).
    /// </summary>
    private static void Writable(Channel channel)
    {
        bool readOnly;
        string? holder;
        lock (channel.Gate)
            (readOnly, holder) = (channel.ReadOnly, channel.Holder);
        if (readOnly || IsJobRun(channel.SessionId))
            throw new InvalidOperationException($"Session {channel.SessionId} is read-only here: {(IsJobRun(channel.SessionId) ? JobHolder : holder ?? JobHolder)}.");
    }

    /// <summary>What drives a job's run, for its read-only note.</summary>
    internal const string JobHolder = "a job's run, kept as it ran";

    /// <summary>The generator slot's model now ("qwen3.5-0.8b"): what a turn the window starts runs on first.</summary>
    private static string Generator(CorvidRuntime runtime) =>
        runtime.Registry.TryGet(ModelSlots.Generator, out var slot) ? slot.Default.Profile.Id.Split('@')[0] : "";

    /// <summary>The generator the session's next turn starts on: its own slot when it is open here (a <c>/model</c> switch), the runtime's otherwise.</summary>
    private static string CurrentGenerator(CorvidRuntime runtime, CorvidSession? session) =>
        session is not null && session.Slots.TryCurrent(ModelSlots.Generator, out var current) ? current.Profile.Id.Split('@')[0] : Generator(runtime);

    /// <summary>A5: the accepted context of the session's generator, or the registry default's for a session not open here.</summary>
    private static int? ContextWindow(CorvidRuntime runtime, CorvidSession? session)
    {
        if (session is not null && session.Slots.TryCurrent(ModelSlots.Generator, out var current))
            return current.Profile.AcceptedContext;
        return runtime.Registry.TryGet(ModelSlots.Generator, out var slot) ? slot.Default.Profile.AcceptedContext : null;
    }

    private static AppMode Mode(PipelineMode mode) => mode switch
    {
        PipelineMode.Manual => AppMode.Manual,
        PipelineMode.Permissive => AppMode.Permissive,
        _ => AppMode.Smart,
    };

    /// <summary>Keeps a background task until it ends; its failure is written to the log, never lost.</summary>
    private void Track(Task task)
    {
        _work[task] = 0;
        _ = task.ContinueWith(done =>
        {
            _work.TryRemove(done, out _);
            if (done.Exception is { } failure)
                Log("corvid app: " + failure.GetBaseException().Message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Log(string text) => Console.Error.WriteLine(text);

    private SlashCommandRegistry Registry => _engine.Services.GetRequiredService<SlashCommandRegistry>();

    /// <summary>One session's updates so far, the state they fold to, its queue and open prompts, and who hears the next updates.</summary>
    private sealed class Channel(string sessionId, SessionUpdateTranslator translator)
    {
        public Lock Gate { get; } = new();

        public string SessionId { get; } = sessionId;

        public SessionUpdateTranslator Translator { get; } = translator;

        /// <summary>The history so far: a second open gets it as it stands, without a copy.</summary>
        public ImmutableList<SessionUpdate> Updates { get; set; } = [];

        /// <summary>The history has been read from the log; until then a publish waits in <see cref="Deferred"/>.</summary>
        public bool Loaded { get; set; }

        public List<Func<SessionUpdateTranslator, IReadOnlyList<SessionUpdate>>> Deferred { get; } = [];

        /// <summary>Dropped while idle: no request uses it again, a fresh one replays the log.</summary>
        public bool Closed { get; set; }

        /// <summary>A job's run, or a session another process drives (G-12): the window shows it and sends nothing into it.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>What drives it while <see cref="ReadOnly"/>, for the refusal.</summary>
        public string? Holder { get; set; }

        /// <summary>The running turn's open calls in the order they came, for the summary's activity.</summary>
        public List<(string CallId, string Tool, string? Target)> OpenCalls { get; } = [];

        /// <summary>The change lists' reads begun, so only the latest is published.</summary>
        public int ChangesRead { get; set; }

        /// <summary>The session has begun the window's message (its prompt is in the log): an interrupt cancels it at once.</summary>
        public bool TurnBegun { get; set; }

        /// <summary>The text of the message the running turn posted, which tells its prompt from another surface's.</summary>
        public string? Sent { get; set; }

        public SessionView? View { get; set; }

        public List<Subscription> Subscribers { get; } = [];

        public Task? Loading { get; set; }

        /// <summary>A turn of this window's runs, or is about to start; the next message queues.</summary>
        public bool Running { get; set; }

        /// <summary>The running turn's reporter, which a prompt is ordered after.</summary>
        public SessionUpdateReporter? Reporter { get; set; }

        /// <summary>The running turn's feed subscription and attached prompter, released when it ends or the window closes.</summary>
        public IDisposable? Feed { get; set; }

        public IDisposable? Prompter { get; set; }

        /// <summary>A5: the question the running turn waits on inside the turn, or null.</summary>
        public InTurnQuestion? AskedInTurn { get; set; }

        /// <summary>
        /// A5: the in-turn question the kernel is about to log (<see cref="Corvid.Kernel.Turns.IQuestionAsker.Expect"/>), until its
        /// <see cref="Corvid.Kernel.Turns.IQuestionAsker.AskAsync"/> makes it <see cref="AskedInTurn"/>. A field for <see cref="Volatile"/>: it is
        /// written under the turn's lock, not the channel's.
        /// </summary>
        public InTurnQuestion? Expected;

        /// <summary>The running turn is being interrupted: its open prompts close as cancelled.</summary>
        public bool Cancelling { get; set; }

        public ImmutableArray<QueuedMessage> Queue { get; set; } = [];

        public int QueueCounter { get; set; }

        public Dictionary<string, PendingApproval> Pending { get; } = new(StringComparer.Ordinal);

        /// <summary>K-3: the steer chips in <see cref="Queue"/> with the turn's answer for each, by chip id.</summary>
        public Dictionary<string, PendingSteer> Steers { get; } = new(StringComparer.Ordinal);

        public bool Tainted { get; set; }

        public string? TaintSource { get; set; }

        /// <summary>The log's summary, read when the session was loaded and after each turn.</summary>
        public SessionSummary? LogSummary { get; set; }

        /// <summary>The summary last raised, so a change is raised once.</summary>
        public SessionSummary? Raised { get; set; }
    }

    private sealed class Subscription(EngineSessionService service, Channel channel, Action<SessionUpdate> onUpdate) : ISessionSubscription
    {
        private volatile bool _disposed;

        public string SessionId => channel.SessionId;

        public IReadOnlyList<SessionUpdate> History { get; set; } = [];

        /// <summary>Hands updates to the UI thread in one post, keeping their order; nothing after disposal.</summary>
        public void Post(IReadOnlyList<SessionUpdate> updates) => service._dispatcher.Post(() =>
        {
            foreach (var update in updates)
            {
                if (_disposed || service._disposed)
                    return;
                onUpdate(update);
            }
        });

        public void Dispose()
        {
            _disposed = true;
            lock (channel.Gate)
                channel.Subscribers.Remove(this);
            service.DropIfIdle(channel);
        }
    }
}
