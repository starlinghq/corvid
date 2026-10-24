using System.Collections.Concurrent;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.Gateway;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;
using LoggedAnswer = Corvid.Abstractions.Events.AssistantMessage;
using McpSessions = Corvid.Abstractions.Events.McpSessions;
using SessionState = Corvid.App.Core.Shell.SessionState;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// The window's sessions over the in-process runtime (A3 core's end-to-end path; A3 sessions extends it): the event log's
/// conversations as summaries, a session opened as its replayed history followed by the live updates of the turns this
/// window sends, and a message sent as a turn. Every callback reaches the views through the dispatcher, on the UI thread.
/// <para>
/// Each opened session has one channel: its updates so far (the replay translated, then every live update appended) and
/// its subscribers. A second open of the same session gets the channel's updates as its history, so two views of one
/// session (the Sessions page and the Board's watch) always agree, and live updates follow exactly after it.
/// </para>
/// <para>
/// Not built yet, and refused with the group that brings it: the queue, steering (K-3), interrupt, approvals (attach the
/// card's prompter; until then a turn's approval prompts are denied and the transcript says so), questions (K-6), slash
/// commands, the approval mode (G-10), the goal, compaction, fork and rename (G-6), and new sessions. Turns another process
/// or surface runs in a session show when the session is opened again.
/// </para>
/// </summary>
public sealed class EngineSessionService : ISessionService, IDisposable
{
    /// <summary>The surface the window's turns are posted from; new sessions it makes would be <c>app-…</c>.</summary>
    public const string Surface = "app";

    private const int TitleLength = 80;

    private readonly AppEngine _engine;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _work = new();
    private volatile bool _disposed;

    public EngineSessionService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _dispatcher = dispatcher;
        _time = time;
    }

    public event EventHandler<SessionSummary>? SessionChanged;

    /// <summary>The turns and summary refreshes in flight, for a test or a shutdown that waits for them.</summary>
    public Task IdleAsync() => Task.WhenAll(_work.Keys);

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
                    summaries.Add(summary);
            }
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

    public async Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var tools = await _engine.ToolsAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        lock (channel.Gate)
        {
            if (channel.Running)
                throw new ServiceUnavailableException(Unavailable.ComesWith("sessions", "sending while a turn runs (the queue)"));
            channel.Running = true;
            channel.Translator.Model = Generator(runtime);
        }

        IDisposable? feed = null, prompter = null;
        try
        {
            var session = runtime.TryGetSession(sessionId, out var open) ? open : runtime.OpenSession(sessionId, Surface);
            var reporter = new SessionUpdateReporter(runtime, sessionId, tools, runtime.Log.GetHead(sessionId, cancellationToken),
                update => Publish(channel, translator => translator.Translate(update)),
                reason => Log("corvid app: could not report a session update: " + reason), _time);
            feed = runtime.Feed.Subscribe(sessionId, reporter);
            prompter = runtime.AttachPrompter(sessionId, new DenyingPrompter(action => Publish(channel, _ =>
            [
                new Notice(NoticeKind.Warning, "Denied: approvals come with A3 sessions, so this window cannot answer a prompt yet.")
                {
                    Detail = SafeText.Clean(action),
                    At = _time.GetUtcNow(),
                },
            ])));
            var started = _time.GetTimestamp();
            // Returns once the session's actor has the message; the turn runs on, and its end is tracked below.
            var delivery = await session.PostAsync(text, EnvelopeOrigin.User, cancellationToken).ConfigureAwait(false);
            Track(FinishTurnAsync(channel, reporter, delivery, feed, prompter, started));
            feed = prompter = null;
        }
        catch
        {
            lock (channel.Gate)
                channel.Running = false;
            throw;
        }
        finally
        {
            feed?.Dispose();
            prompter?.Dispose();
        }
        Refresh(runtime, sessionId);
    }

    public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        Refuse<QueuedMessage>("queueing a message behind the running turn");

    public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => Refuse("the queue");

    public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => Refuse("the queue");

    public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => Refuse<string?>("the queue");

    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => Refuse("steering a running turn (K-3)");

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => Refuse("interrupting a turn");

    public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) =>
        Refuse("answering approvals");

    public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) =>
        Refuse("clarifying questions (K-6)");

    public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) => Refuse("slash commands");

    public Task SetApprovalModeAsync(string sessionId, Corvid.App.Core.Shell.ApprovalMode mode, CancellationToken cancellationToken = default) =>
        Refuse("a session's approval mode (G-10)");

    public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => Refuse("the standing goal");

    public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => Refuse("compaction");

    public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => Refuse<string>("forking a session");

    public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => Refuse("renaming a session (G-6)");

    /// <summary>
    /// Stops every callback: subscribers hear nothing more, and turns still running finish in the runtime (whose own
    /// disposal ends them) without reporting here. Never waits: it runs on the UI thread.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (var channel in _channels.Values)
            lock (channel.Gate)
                channel.Subscribers.Clear();
    }

    private static Task Refuse(string what) => Task.FromException(new ServiceUnavailableException(Unavailable.ComesWith("sessions", what)));

    private static Task<T> Refuse<T>(string what) => Task.FromException<T>(new ServiceUnavailableException(Unavailable.ComesWith("sessions", what)));

    /// <summary>The session's channel, its history replayed from the log on first use (once, however many ask at once).</summary>
    private async Task<Channel> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
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

    private static async Task ReplayAsync(CorvidRuntime runtime, ToolPresentation tools, Channel channel)
    {
        if (runtime.Log.GetHead(channel.SessionId) == 0 && !runtime.TryGetSession(channel.SessionId, out _))
            throw new KeyNotFoundException($"There is no session '{channel.SessionId}' in Corvid's log.");
        var history = await SessionReplay.ReadAsync(runtime, channel.SessionId, tools, ReplayOptions.Full).ConfigureAwait(false);
        lock (channel.Gate)
        {
            foreach (var update in history.Updates)
                channel.Updates.AddRange(channel.Translator.Translate(update));
            channel.Updates.AddRange(channel.Translator.EndReplay(running: channel.Running));
        }
    }

    /// <summary>Waits for the turn's delivery, ends its report and its turn, and gives the session back for the next message.</summary>
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
            feed.Dispose();
            prompter.Dispose();
        }

        var elapsed = _time.GetElapsedTime(started);
        Publish(channel, translator =>
        {
            var ended = translator.EndTurn(outcome, _time.GetUtcNow(), elapsed, error);
            // A message that never reached the log opened no turn; its failure still shows.
            return ended.Count > 0 || error is null
                ? ended
                : [new Notice(NoticeKind.Warning, "The message was not sent.") { Detail = SafeText.Clean(error), At = _time.GetUtcNow() }];
        });
        lock (channel.Gate)
            channel.Running = false;
        if (_engine.Runtime.IsCompletedSuccessfully)
            Refresh(_engine.Runtime.Result, channel.SessionId);
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

    /// <summary>Translates under the channel's lock, keeps the result as history and posts it to every subscriber in order.</summary>
    private void Publish(Channel channel, Func<SessionUpdateTranslator, IReadOnlyList<SessionUpdate>> translate)
    {
        if (_disposed)
            return;
        lock (channel.Gate)
        {
            var updates = translate(channel.Translator);
            if (updates.Count == 0)
                return;
            channel.Updates.AddRange(updates);
            foreach (var subscriber in channel.Subscribers)
                subscriber.Post(updates);
        }
    }

    /// <summary>Reads the session's summary again and raises <see cref="SessionChanged"/> with it on the UI thread.</summary>
    private void Refresh(CorvidRuntime runtime, string sessionId) => Track(Task.Run(() =>
    {
        if (_disposed || Summarize(runtime, sessionId) is not { } summary)
            return;
        _dispatcher.Post(() =>
        {
            if (!_disposed)
                SessionChanged?.Invoke(this, summary);
        });
    }));

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

    /// <summary>The conversations a person had, not the stores' own sessions (MCP pins, the gateway's delivery ledger).</summary>
    private static bool IsConversation(string id) => id is not (McpSessions.Pins or GatewaySessions.Ledger);

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
        var running = _channels.TryGetValue(id, out var channel) && channel.Running;
        var open = runtime.TryGetSession(id, out var session);
        return new SessionSummary(id, Line(title) ?? id)
        {
            Workspace = runtime.Builtin.Environment.Workspace,
            Surface = SurfaceOf(id),
            State = running ? SessionState.Running : id.StartsWith("job:", StringComparison.Ordinal) ? SessionState.Job : SessionState.Idle,
            LastActivity = last,
            Turns = turns,
            Tainted = open && session.Tainted,
            Preview = Line(preview),
        };
    }

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

    /// <summary>The generator slot's model now ("qwen3.5-0.8b"): what a turn the window starts runs on first.</summary>
    private static string Generator(CorvidRuntime runtime) =>
        runtime.Registry.TryGet(ModelSlots.Generator, out var slot) ? slot.Default.Profile.Id.Split('@')[0] : "";

    /// <summary>One session's updates so far and who hears the next ones.</summary>
    private sealed class Channel(string sessionId, SessionUpdateTranslator translator)
    {
        public Lock Gate { get; } = new();

        public string SessionId { get; } = sessionId;

        public SessionUpdateTranslator Translator { get; } = translator;

        public List<SessionUpdate> Updates { get; } = [];

        public List<Subscription> Subscribers { get; } = [];

        public Task? Loading { get; set; }

        public bool Running { get; set; }
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
