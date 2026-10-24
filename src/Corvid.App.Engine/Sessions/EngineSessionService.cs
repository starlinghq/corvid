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
/// it. A session's summary for the rail and the Board is the log's, with the open channel's live state over it (running,
/// waiting on an approval with its deadline, the step and what runs).
/// </para>
/// <para>
/// The queue is the engine's (messages posted to the session actor cannot be taken back): Enter while a turn runs queues,
/// and when the turn ends the first queued message starts the next. Approvals go to the card: a prompter is attached to
/// the session for each turn the window runs (<see cref="CardPrompter"/>), and an answer completes the pipeline's prompt.
/// </para>
/// <para>
/// Refused, with what brings each: steering (K-3), clarifying questions as their own block (K-6), renaming (G-6). Turns
/// another process or surface runs in a session show when the session is opened again.
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
        _ = WatchJobsAsync();
    }

    public event EventHandler<SessionSummary>? SessionChanged;

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
            foreach (var id in runtime.Log.ListSessions(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsConversation(id) && Summarize(runtime, id) is { } summary)
                    summaries.Add(Live(id, summary));
            }
            foreach (var (id, created) in _created)
                if (runtime.Log.GetHead(id, cancellationToken) == 0)
                    summaries.Add(Live(id, Empty(runtime, id, created)));
            return (IReadOnlyList<SessionSummary>)[.. summaries.OrderByDescending(summary => summary.LastActivity)];
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var subscription = new Subscription(this, channel, onUpdate);
        lock (channel.Gate)
        {
            subscription.History = [.. channel.Updates];
            channel.Subscribers.Add(subscription);
        }
        return subscription;
    }

    /// <summary>Sends a message, starting a turn; while one runs it is queued behind it, as Enter does.</summary>
    public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        QueueAsync(sessionId, text, cancellationToken);

    /// <summary>Queues a message behind the running turn; with none running it starts one at once.</summary>
    public async Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var tools = await _engine.ToolsAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        QueuedMessage message;
        bool start;
        lock (channel.Gate)
        {
            message = new QueuedMessage($"q-{++channel.QueueCounter}", text.Trim());
            start = !channel.Running;
            if (start)
                channel.Running = true;
            else
                channel.Queue = channel.Queue.Add(message);
        }
        if (start)
            await StartTurnAsync(channel, runtime, tools, message.Text, cancellationToken).ConfigureAwait(false);
        else
            PublishQueue(channel);
        return message;
    }

    public async Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default)
    {
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (ChangeQueue(channel, queue => queue.FirstOrDefault(item => item.Id == queuedId) is { } item ? queue.Remove(item) : queue))
            PublishQueue(channel);
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
        QueuedMessage? taken = null;
        if (ChangeQueue(channel, queue =>
            {
                taken = queue.FirstOrDefault(item => item.Id == queuedId);
                return taken is null ? queue : queue.Remove(taken);
            }))
            PublishQueue(channel);
        return taken?.Text;
    }

    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        Task.FromException(new ServiceUnavailableException(
            "Steering comes with K-3: a running turn cannot take a message at its next step checkpoint yet. Press Enter to queue it behind the turn."));

    /// <summary>Interrupts the window's running turn: the work so far stays, open prompts close as cancelled, and the queue goes next.</summary>
    public async Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        if (!_channels.TryGetValue(sessionId, out var channel))
            return;
        lock (channel.Gate)
        {
            if (channel.Reporter is null)
                return;
            channel.Cancelling = true;
        }
        // The turn's token first, so the pipeline records each open prompt as cancelled rather than denied; then the cards close.
        if (runtime.TryGetSession(sessionId, out var session))
            session.Cancel();
        ClosePending(channel, "interrupted");
    }

    public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) =>
        Task.FromException(new ServiceUnavailableException(
            "Clarifying questions come with K-6: a turn that asks one ends with it as its answer, so reply with a message."));

    public async Task SetApprovalModeAsync(string sessionId, AppMode mode, CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Session(runtime, sessionId).ApprovalMode = mode switch
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
        var session = Session(runtime, sessionId);
        var kept = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
        // A GoalSet event in the log: SQLite, off the UI thread.
        await Task.Run(() => session.SetGoal(kept), cancellationToken).ConfigureAwait(false);
        Publish(channel, _ => [new GoalChanged(kept) { At = _time.GetUtcNow() }]);
    }

    public async Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        lock (channel.Gate)
            if (channel.Running)
                throw new InvalidOperationException("Compaction runs between turns; this one is still running.");
        var session = Session(runtime, sessionId);
        var focused = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();
        var result = await Task.Run(() => session.CompactAsync(focused, cancellationToken), cancellationToken).ConfigureAwait(false);
        Publish(channel, translator => [Compaction(translator, result, focused)]);
    }

    /// <summary>
    /// Forks the session into a new one. A position is a message's (the fork point "edit and resend" and "branch from here"
    /// pass): the fork holds everything before that message, so the message can be sent again, changed or not. Null forks
    /// at the head, after the running turn when one runs.
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
            // Before the first message there is nothing to copy: a new, empty session.
            id = runtime.OpenSession(surface: Surface).Id;
            _created[id] = _time.GetUtcNow();
        }
        else
        {
            var fork = await runtime.ForkSessionAsync(sessionId, through, Surface, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        cancellationToken.ThrowIfCancellationRequested();
        var id = runtime.OpenSession(surface: Surface).Id;
        _created[id] = _time.GetUtcNow();
        Refresh(runtime, id);
        return id;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) =>
        Task.FromException(new ServiceUnavailableException(
            "Renaming comes with G-6: the event log has no title event yet, so a session's title is its first message."));

    /// <summary>
    /// Stops every callback: subscribers hear nothing more, open prompts are denied, and turns still running finish in the
    /// runtime (whose own disposal ends them) without reporting here. Never waits: it runs on the UI thread.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        UnwatchJobs();
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
    /// </summary>
    private async Task StartTurnAsync(Channel channel, CorvidRuntime runtime, ToolPresentation tools, string text, CancellationToken cancellationToken)
    {
        var sessionId = channel.SessionId;
        IDisposable? feed = null, prompter = null;
        try
        {
            var session = Session(runtime, sessionId);
            var head = await Task.Run(() => runtime.Log.GetHead(sessionId, cancellationToken), cancellationToken).ConfigureAwait(false);
            var reporter = new SessionUpdateReporter(runtime, sessionId, tools, head, update => Report(channel, session, update),
                reason => Log("corvid app: could not report a session update: " + reason), _time);
            lock (channel.Gate)
            {
                channel.Translator.Model = Generator(runtime);
                channel.Translator.MaxSteps = runtime.Options.Turn.MaxSteps;
                channel.Translator.ContextWindow = session.Slots.TryCurrent(ModelSlots.Generator, out var generator) ? generator.Profile.AcceptedContext : null;
                channel.Cancelling = false;
                channel.Reporter = reporter;
            }
            feed = runtime.Feed.Subscribe(sessionId, reporter);
            prompter = runtime.AttachPrompter(sessionId, new CardPrompter(this, channel, runtime, tools, reporter));
            // Kept on the channel too, so closing the window lets go of them even while the turn runs on.
            lock (channel.Gate)
                (channel.Feed, channel.Prompter) = (feed, prompter);
            var started = _time.GetTimestamp();
            // A session's first message is its title until K-9 titles and G-6 renames are events, as the rail shows it.
            if (Line(text) is { Length: > 0 } title)
                Publish(channel, _ => channel.View is { Title.Length: > 0 } ? [] : [new TitleChanged(title) { At = _time.GetUtcNow() }]);
            // Returns once the session's actor has the message; the turn runs on, and its end is tracked below.
            var delivery = await session.PostAsync(text, EnvelopeOrigin.User, cancellationToken).ConfigureAwait(false);
            Track(FinishTurnAsync(channel, reporter, delivery, feed, prompter, started));
            feed = prompter = null;
        }
        catch
        {
            lock (channel.Gate)
            {
                channel.Running = false;
                channel.Reporter = null;
                (channel.Feed, channel.Prompter) = (null, null);
            }
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

        QueuedMessage? next = null;
        lock (channel.Gate)
        {
            channel.Reporter = null;
            channel.Cancelling = false;
            if (!_disposed && !channel.Queue.IsEmpty)
            {
                // The queue goes next: the session stays running for it.
                next = channel.Queue[0];
                channel.Queue = channel.Queue.RemoveAt(0);
            }
            else
            {
                channel.Running = false;
            }
        }
        if (next is not null)
        {
            PublishQueue(channel);
            Track(StartQueuedAsync(channel, next));
        }
        if (_engine.Runtime.IsCompletedSuccessfully)
        {
            Refresh(_engine.Runtime.Result, channel.SessionId);
            // The dock's Changes and the Review badge: what the turn's calls changed, from the write ledger.
            PublishChanges(_engine.Runtime.Result, channel);
        }
    }

    /// <summary>Starts the queued message's turn; one that cannot start goes back to the head of the queue, and the transcript says why.</summary>
    private async Task StartQueuedAsync(Channel channel, QueuedMessage message)
    {
        try
        {
            var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
            var tools = await _engine.ToolsAsync().ConfigureAwait(false);
            await StartTurnAsync(channel, runtime, tools, message.Text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ChangeQueue(channel, queue => queue.Insert(0, message));
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

    /// <summary>A live update of the turn, translated, with a taint change when the session's taint moved with it.</summary>
    private void Report(Channel channel, CorvidSession session, SessionStreamUpdate update) => Publish(channel, translator =>
    {
        var output = new List<SessionUpdate>(translator.Translate(update));
        var tainted = session.Tainted;
        if (tainted != channel.Tainted)
        {
            channel.Tainted = tainted;
            // The kernel taints the session when a network tool's result or outside content enters (plan §10.3), and
            // clears it when the user's own message starts a turn; the call that finished is what brought it in.
            string? source = update is ToolCallChanged changed ? $"{changed.Tool} {ToolPresentation.Target(changed.Input)}".Trim() : null;
            if (tainted)
                channel.TaintSource = source;
            output.Add(new TaintChanged(tainted) { At = update.At, Source = tainted ? source : null });
        }
        return output;
    });

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

    /// <summary>The session's channel, its history replayed from the log on first use (once, however many ask at once).</summary>
    private async Task<Channel> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var tools = await _engine.ToolsAsync().ConfigureAwait(false);
        var channel = _channels.GetOrAdd(sessionId, id => new Channel(id, new SessionUpdateTranslator(tools.WorkspaceRoot)));
        Task loading;
        lock (channel.Gate)
            loading = channel.Loading ??= Task.Run(() => ReplayAsync(runtime, tools, channel), CancellationToken.None);
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
        return channel;
    }

    private async Task ReplayAsync(CorvidRuntime runtime, ToolPresentation tools, Channel channel)
    {
        var id = channel.SessionId;
        var open = runtime.TryGetSession(id, out var session);
        if (runtime.Log.GetHead(id) == 0 && !open)
            throw new KeyNotFoundException($"There is no session '{id}' in Corvid's log.");
        var history = await SessionReplay.ReadAsync(runtime, id, tools, ReplayOptions.Full).ConfigureAwait(false);
        var goal = Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, id).Goal;
        var summary = Summarize(runtime, id) ?? Empty(runtime, id, _time.GetUtcNow());
        var commands = Commands();
        var mode = Mode(open ? session.ApprovalMode : runtime.Options.Approvals.Mode);
        var changes = await ChangesAsync(runtime, id, CancellationToken.None).ConfigureAwait(false);
        lock (channel.Gate)
        {
            var at = summary.LastActivity;
            var updates = new List<SessionUpdate>();
            foreach (var update in history.Updates)
                updates.AddRange(channel.Translator.Translate(update));
            updates.AddRange(channel.Translator.EndReplay(running: channel.Running));
            updates.Add(changes);
            // The rail's title (the first message, until K-9 titles and G-6 renames are events) heads the session too.
            if (summary.Turns > 0 && summary.Title != id)
                updates.Add(new TitleChanged(summary.Title) { At = at });
            // What the session is beside its transcript: where it was forked from (this window's forks only), its goal,
            // its approval mode, whether outside content is in its context, and the commands the composer offers.
            if (_forks.TryGetValue(id, out var fork))
                updates.Add(new SessionForked(fork.From, fork.AtTurn) { At = at });
            if (goal is not null)
                updates.Add(new GoalChanged(goal) { At = at });
            updates.Add(new ApprovalModeChanged(mode) { At = at });
            channel.Tainted = open && session.Tainted;
            if (channel.Tainted)
                updates.Add(new TaintChanged(true) { At = at });
            updates.Add(new AvailableCommandsUpdate(commands) { At = at });
            channel.Updates.AddRange(updates);
            channel.View = SessionFold.Replay(id, channel.Updates);
            channel.LogSummary = summary;
        }
    }

    /// <summary>Translates under the channel's lock, keeps the result as history and posts it to every subscriber in order.</summary>
    private void Publish(Channel channel, Func<SessionUpdateTranslator, IReadOnlyList<SessionUpdate>> translate)
    {
        if (_disposed)
            return;
        SessionSummary? changed = null;
        lock (channel.Gate)
        {
            var updates = translate(channel.Translator);
            if (updates.Count == 0)
                return;
            channel.Updates.AddRange(updates);
            foreach (var update in updates)
                channel.View = SessionFold.Apply(channel.View ?? SessionFold.Start(channel.SessionId), update);
            foreach (var subscriber in channel.Subscribers)
                subscriber.Post(updates);
            if (channel.LogSummary is { } logged && Overlay(logged, channel) is var summary && summary != channel.Raised)
                changed = channel.Raised = summary;
        }
        if (changed is not null)
            Raise(changed);
    }

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
        var waiting = running && (view.Approvals.Count > 0 || view.Question is not null);
        var tool = running
            ? view.Transcript.LastOrDefault(entry => entry is ToolEntry { Status: ToolCallStatus.InProgress or ToolCallStatus.Pending } call && call.Turn == view.Turn) as ToolEntry
            : null;
        return logged with
        {
            State = waiting ? SessionState.Waiting : running ? SessionState.Running : logged.State,
            LastActivity = view.LastActivity > logged.LastActivity ? view.LastActivity : logged.LastActivity,
            StepStarted = running ? view.ActivitySince : null,
            Deadline = view.Approvals.Count > 0 ? view.Approvals[0].Deadline : null,
            Step = running ? view.Step : null,
            MaxSteps = running ? view.MaxSteps : null,
            Activity = waiting ? "waiting on you" : tool is not null ? $"{tool.Tool} {tool.Target}".Trim() : null,
            Model = running && view.Model is { Length: > 0 } model ? model : logged.Model,
            Tainted = logged.Tainted || view.Tainted,
            ForkOf = view.ForkOf ?? logged.ForkOf,
            ForkTurn = view.ForkTurn ?? logged.ForkTurn,
            PendingApprovals = view.Approvals.Count,
            Approval = view.Approvals.Count > 0 ? view.Approvals[0].Text : null,
        };
    }

    private SessionSummary? Summarize(CorvidRuntime runtime, string id)
    {
        var head = runtime.Log.GetHead(id);
        if (head == 0)
            return null;
        string? title = null, preview = null;
        var turns = 0;
        var last = default(DateTimeOffset);
        foreach (var item in Corvid.Kernel.Events.SessionProjection.ReadRange(runtime.Log, id, 0, head))
        {
            last = item.RecordedAt;
            switch (item.Payload)
            {
                case LoggedUserMessage user:
                    turns++;
                    title ??= user.Text;
                    break;
                case LoggedAnswer answer:
                    preview = answer.Text;
                    break;
            }
        }
        var open = runtime.TryGetSession(id, out var session);
        // A session this window made holding no message yet (a fork before the first one) is new, not its id.
        return new SessionSummary(id, Line(title) ?? (_created.ContainsKey(id) || _forks.ContainsKey(id) ? "New session" : id))
        {
            Workspace = runtime.Builtin.Environment.Workspace,
            Surface = SurfaceOf(id),
            State = id.StartsWith("job:", StringComparison.Ordinal) ? SessionState.Job : SessionState.Idle,
            LastActivity = last,
            Turns = turns,
            Tainted = open && session.Tainted,
            Preview = Line(preview),
            ForkOf = _forks.TryGetValue(id, out var fork) ? fork.From : null,
            ForkTurn = _forks.TryGetValue(id, out var at) ? at.AtTurn : null,
        };
    }

    /// <summary>A session this window started with nothing in it yet.</summary>
    private SessionSummary Empty(CorvidRuntime runtime, string id, DateTimeOffset created) => new(id, "New session")
    {
        Workspace = runtime.Builtin.Environment.Workspace,
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

    /// <summary>The conversations a person had, not the stores' own sessions (MCP pins, the gateway's delivery ledger).</summary>
    private static bool IsConversation(string id) => id is not (McpSessions.Pins or GatewaySessions.Ledger);

    // ---- Helpers ----

    /// <summary>The session's handle in the runtime, opened for this window when no surface has it open yet.</summary>
    private static CorvidSession Session(CorvidRuntime runtime, string sessionId) =>
        runtime.TryGetSession(sessionId, out var session) ? session : runtime.OpenSession(sessionId, Surface);

    /// <summary>The generator slot's model now ("qwen3.5-0.8b"): what a turn the window starts runs on first.</summary>
    private static string Generator(CorvidRuntime runtime) =>
        runtime.Registry.TryGet(ModelSlots.Generator, out var slot) ? slot.Default.Profile.Id.Split('@')[0] : "";

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

        public List<SessionUpdate> Updates { get; } = [];

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

        /// <summary>The running turn is being interrupted: its open prompts close as cancelled.</summary>
        public bool Cancelling { get; set; }

        public ImmutableArray<QueuedMessage> Queue { get; set; } = [];

        public int QueueCounter { get; set; }

        public Dictionary<string, PendingApproval> Pending { get; } = new(StringComparer.Ordinal);

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
        }
    }
}
