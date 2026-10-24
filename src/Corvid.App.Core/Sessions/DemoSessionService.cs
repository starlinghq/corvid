using System.Collections.Immutable;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>Which moment of the mockups' scenario the demo opens at (<c>--scene</c>).</summary>
public enum DemoScene
{
    /// <summary>Main.png: turn 12 of "Fix the flaky sandbox test on CI" running its shell call, the answer streaming.</summary>
    Main,
    /// <summary>Approval.png: turn 13 waiting on approval a-32, a second prompt behind it.</summary>
    Approval,
}

/// <summary>
/// The sessions the mockups draw (Main, Approval, Board, Review, Rewind), served as update streams, with a scripted
/// engine behind them (docs/plan/ui.md §8: demo mode until Corvid.App.Engine exists).
/// <para>
/// The scenario opens at a moment of a mockup and holds still there, so screenshots are stable: cli-9f2e's turn 12 shows
/// its shell call running until the first message, steer or interrupt sets it going again. From then on it is live:
/// sending runs a scripted turn (ingress chips, a tool card that runs and finishes, a streamed answer); the first turn
/// after turn 12 asks approval a-32 and waits for the answer, with a-33 queued behind it; queue, steer and interrupt behave
/// as docs/plan/ui.md F6 says.
/// </para>
/// <para>
/// Time is <see cref="TimeProvider"/>'s: every scripted step is a one-shot timer, and updates are stamped with the
/// scenario's clock, which starts at the provider's now (frozen at 14:05 in demo mode) and moves forward by each step's
/// delay. Under a manual provider the whole script is deterministic. Everything is done on the UI thread through the
/// dispatcher, so requests and steps never race.
/// </para>
/// </summary>
public sealed partial class DemoSessionService : ISessionService
{
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DemoSession> _sessions = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _start;
    private DateTimeOffset _clock;
    private int _forks;

    public DemoSessionService(IDispatcher dispatcher, TimeProvider time, DemoScene scene = DemoScene.Main)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _dispatcher = dispatcher;
        _time = time;
        _start = time.GetUtcNow();
        _clock = _start;
        Scene = scene;
        BuildScenario();
    }

    public DemoScene Scene { get; }

    /// <summary>The scenario's clock: the provider's now, moved forward by the scripted steps that have run.</summary>
    public DateTimeOffset Clock => _clock;

    public event EventHandler<SessionSummary>? SessionChanged;

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionSummary> summaries = [.. _sessions.Values.Select(Summary).OrderByDescending(summary => summary.LastActivity)];
        return Task.FromResult(summaries);
    }

    public Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        var session = Get(sessionId);
        var subscription = new Subscription(session, onUpdate);
        session.Subscribers.Add(subscription);
        return Task.FromResult<ISessionSubscription>(subscription);
    }

    public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;
        if (session.State.Running)
            return QueueAsync(sessionId, text, cancellationToken);
        StartTurn(session, text.Trim());
        return Task.CompletedTask;
    }

    public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        var message = new QueuedMessage($"q-{++session.QueueCounter}", text.Trim());
        SetQueue(session, session.Queue.Add(message));
        if (!session.State.Running)
            StartNextQueued(session, TimeSpan.Zero);
        else
            ResumeScene(session);
        return Task.FromResult(message);
    }

    public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default)
    {
        var session = Get(sessionId);
        if (session.Queue.FirstOrDefault(message => message.Id == queuedId) is { } message)
            SetQueue(session, session.Queue.Remove(message));
        return Task.CompletedTask;
    }

    public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default)
    {
        var session = Get(sessionId);
        var message = session.Queue.FirstOrDefault(item => item.Id == queuedId);
        if (message is null)
            return Task.CompletedTask;
        var without = session.Queue.Remove(message);
        SetQueue(session, without.Insert(Math.Clamp(index, 0, without.Length), message));
        return Task.CompletedTask;
    }

    public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default)
    {
        var session = Get(sessionId);
        var message = session.Queue.FirstOrDefault(item => item.Id == queuedId);
        if (message is null)
            return Task.FromResult<string?>(null);
        SetQueue(session, session.Queue.Remove(message));
        return Task.FromResult<string?>(message.Text);
    }

    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        if (!session.State.Running)
            return SendAsync(sessionId, text, cancellationToken);
        SetQueue(session, session.Queue.Add(new QueuedMessage($"q-{++session.QueueCounter}", text.Trim()) { Steer = true }));
        ResumeScene(session);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        if (session.State.Running)
            Interrupt(session);
        return Task.CompletedTask;
    }

    public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var session = Writable(sessionId);
        // Nothing runs on a stale prompt: an answer to one that has closed changes nothing.
        if (session.State.Approvals.All(open => open.ApprovalId != approvalId))
            return Task.CompletedTask;
        if (session.Script?.Approvals.Remove(approvalId, out var handler) == true)
            handler(answer, false);
        else
            Emit(session, Resolution(approvalId, answer));
        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        if (session.State.Question?.QuestionId != questionId || string.IsNullOrWhiteSpace(answer))
            return Task.CompletedTask;
        Emit(session, new QuestionAnswered(questionId, answer.Trim()));
        if (session.QuestionHandler is { } handler)
        {
            session.QuestionHandler = null;
            handler(answer.Trim());
        }
        else if (!session.State.Running)
        {
            StartTurn(session, answer.Trim());
        }

        return Task.CompletedTask;
    }

    public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default)
    {
        Emit(Writable(sessionId), new ApprovalModeChanged(mode));
        return Task.CompletedTask;
    }

    public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default)
    {
        Emit(Writable(sessionId), new GoalChanged(string.IsNullOrWhiteSpace(goal) ? null : goal.Trim()));
        return Task.CompletedTask;
    }

    public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        if (session.State.Running)
            throw new InvalidOperationException("Compaction runs between turns; this one is still running.");
        Compact(session, focus);
        return Task.CompletedTask;
    }

    public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default)
    {
        var source = Get(sessionId);
        var id = $"cli-f{++_forks:x3}";
        var through = (int)Math.Clamp(throughPosition ?? source.History.Count - 1, -1, source.History.Count - 1);
        var copied = source.History.Take(through + 1).ToList();
        // A fork never splits a turn: one copied mid-turn stops before that turn began.
        var lastStart = copied.FindLastIndex(update => update is TurnStarted);
        if (lastStart >= 0 && !copied.Skip(lastStart).Any(update => update is TurnEnded))
            copied.RemoveRange(lastStart, copied.Count - lastStart);
        copied.RemoveAll(update => update is QueueChanged or ApprovalRequested or ApprovalResolved or QuestionAsked or QuestionAnswered);

        var fork = new DemoSession(id) { Base = source.Base with { Id = id, Pinned = false } };
        _sessions[id] = fork;
        foreach (var update in copied)
            Seed(fork, update);
        var forkTurn = fork.State.Turn;
        Emit(fork, new SessionForked(sessionId, forkTurn));
        Emit(fork, new TitleChanged($"{source.State.Title} (fork)"));
        return Task.FromResult(id);
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(title))
            Emit(Get(sessionId), new TitleChanged(title.Trim()));
        return Task.CompletedTask;
    }

    /// <summary>The session's state as the fold sees it (tests).</summary>
    internal SessionView StateOf(string sessionId) => Get(sessionId).State;

    /// <summary>The session's updates so far (tests).</summary>
    internal IReadOnlyList<SessionUpdate> HistoryOf(string sessionId) => Get(sessionId).History;

    // ---- Sessions, updates and summaries ----

    private DemoSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new KeyNotFoundException($"No session '{sessionId}' in the demo scenario.");

    private DemoSession Writable(string sessionId)
    {
        var session = Get(sessionId);
        if (session.State.ReadOnly)
            throw new InvalidOperationException($"Session {sessionId} is read-only here: {session.State.Holder ?? "another process drives it"}.");
        return session;
    }

    /// <summary>Adds an update to a session's history without telling anyone: the scenario before it is opened.</summary>
    private static void Seed(DemoSession session, SessionUpdate update)
    {
        if (update is UserMessage { Position: null } message)
            update = message with { Position = session.History.Count };
        session.History.Add(update);
        session.State = SessionFold.Apply(session.State, update);
    }

    /// <summary>Appends an update, stamped with the scenario's clock, and tells the session's subscribers and the lists.</summary>
    private void Emit(DemoSession session, SessionUpdate update)
    {
        if (update.At == default)
            update = update with { At = _clock };
        Seed(session, update);
        var delivered = session.History[^1];
        foreach (var subscriber in session.Subscribers.ToArray())
            subscriber.Deliver(delivered);
        var summary = Summary(session);
        if (summary != session.LastSummary)
        {
            session.LastSummary = summary;
            SessionChanged?.Invoke(this, summary);
        }
    }

    private void SetQueue(DemoSession session, ImmutableArray<QueuedMessage> queue)
    {
        session.Queue = queue;
        Emit(session, new QueueChanged(queue));
    }

    private SessionSummary Summary(DemoSession session)
    {
        var state = session.State;
        var running = state.Transcript.LastOrDefault(entry => entry is ToolEntry { Status: ToolCallStatus.InProgress or ToolCallStatus.Pending } tool
            && tool.Turn == state.Turn) as ToolEntry;
        var lastUser = state.Transcript.LastOrDefault(entry => entry is UserEntry) as UserEntry;
        return session.Base with
        {
            Title = state.Title.Length > 0 ? state.Title : session.Base.Title,
            State = session.Base.Job is not null ? SessionState.Job
                : state.WaitingOnUser ? SessionState.Waiting
                : state.Running ? SessionState.Running
                : SessionState.Idle,
            LastActivity = state.LastActivity,
            StepStarted = state.Running ? state.ActivitySince : null,
            Deadline = state.CurrentApproval?.Deadline,
            Step = state.Running ? state.Step : null,
            MaxSteps = state.Running ? state.MaxSteps : null,
            Activity = state.WaitingOnUser ? "waiting on you"
                : state.Running && running is not null ? $"{running.Tool} {running.Target}".Trim()
                : null,
            Model = state.Generator is { } generator ? generator.Escalated ? $"{generator.Model} (escalated)" : generator.Model : state.Model,
            Cloud = state.Generator?.Cloud ?? false,
            Turns = state.Turn,
            Tainted = state.Tainted,
            ReadOnly = state.ReadOnly,
            Holder = state.Holder,
            ForkOf = state.ForkOf,
            ForkTurn = state.ForkTurn,
            PendingApprovals = state.Approvals.Count,
            Approval = state.CurrentApproval?.Text,
            PendingQuestions = state.Question is null ? 0 : 1,
            Question = state.Question is { } question ? question.Title ?? question.Text : null,
            Preview = lastUser?.Text,
        };
    }

    // ---- Turns, the queue and interruption ----

    private void StartTurn(DemoSession session, string text)
    {
        if (session.ApprovalTurnNext)
        {
            session.ApprovalTurnNext = false;
            ApprovalTurn(session, text);
        }
        else
        {
            ScriptedTurn(session, text);
        }
    }

    /// <summary>The queue goes next: once a turn ends, its first message (a steer left over is sent the same way) starts one.</summary>
    private void StartNextQueued(DemoSession session, TimeSpan delay)
    {
        if (session.State.Running || session.Queue.IsEmpty || session.Script is { Ended: false })
            return;
        var starter = new Script(this, session);
        session.Script = starter;
        starter.Then(delay, () =>
        {
            if (session.State.Running || session.Queue.IsEmpty)
                return;
            var next = session.Queue[0];
            SetQueue(session, session.Queue.RemoveAt(0));
            starter.End();
            StartTurn(session, next.Text);
        }).Go();
    }

    /// <summary>The scenario's still moment starts moving on the first message or steer.</summary>
    private void ResumeScene(DemoSession session)
    {
        if (session.Resume is { } resume)
        {
            session.Resume = null;
            resume();
        }
    }

    private void Interrupt(DemoSession session)
    {
        session.Script?.End();
        session.Script = null;
        session.Resume = null;
        session.QuestionHandler = null;
        var state = session.State;
        foreach (var open in state.Approvals)
            Emit(session, new ApprovalResolved(open.ApprovalId, ApprovalOutcome.Cancelled) { Reason = "interrupted" });
        foreach (var running in state.Transcript.OfType<ToolEntry>().Where(tool => tool.Turn == state.Turn && tool.Status == ToolCallStatus.InProgress))
        {
            Emit(session, new ToolCallUpdate(running.CallId)
            {
                Status = ToolCallStatus.Failed,
                Outcome = ToolOutcome.Cancelled,
                Elapsed = _clock - running.StartedAt,
            });
        }

        Emit(session, new TurnEnded(state.Turn, TurnOutcome.Cancelled)
        {
            Elapsed = state.TurnStartedAt is { } started ? _clock - started : null,
        });
        StartNextQueued(session, TimeSpan.FromMilliseconds(300));
    }

    private static ApprovalResolved Resolution(string approvalId, ApprovalAnswer answer) => answer.Choice switch
    {
        ApprovalChoice.Deny => new ApprovalResolved(approvalId, ApprovalOutcome.Denied) { Reason = answer.Reason },
        ApprovalChoice.Session => new ApprovalResolved(approvalId, ApprovalOutcome.Approved) { Scope = ApprovalScope.Session },
        ApprovalChoice.Always => new ApprovalResolved(approvalId, ApprovalOutcome.Approved) { Scope = ApprovalScope.Always },
        _ => new ApprovalResolved(approvalId, ApprovalOutcome.Approved),
    };

    /// <summary>Moves the scenario's clock to a step's due time, never backwards and never behind the provider's.</summary>
    private void AdvanceClock(DateTimeOffset due)
    {
        var now = _time.GetUtcNow();
        var next = due > now ? due : now;
        if (next > _clock)
            _clock = next;
    }

    private sealed class DemoSession(string id)
    {
        public string Id { get; } = id;

        /// <summary>What the scenario says about the session beyond its updates: workspace, surface, pin, job.</summary>
        public SessionSummary Base { get; init; } = new(id, id);

        public List<SessionUpdate> History { get; } = [];

        public SessionView State { get; set; } = SessionFold.Start(id);

        public List<Subscription> Subscribers { get; } = [];

        public ImmutableArray<QueuedMessage> Queue { get; set; } = [];

        public int QueueCounter { get; set; }

        /// <summary>The scripted turn running now, or the step that starts the next queued message.</summary>
        public Script? Script { get; set; }

        /// <summary>Continues the scenario's still moment (turn 12 of cli-9f2e).</summary>
        public Action? Resume { get; set; }

        /// <summary>What answering the open question does: cli-a410's turn waits on it.</summary>
        public Action<string>? QuestionHandler { get; set; }

        /// <summary>cli-9f2e: the next turn is the one that asks approval a-32.</summary>
        public bool ApprovalTurnNext { get; set; }

        public SessionSummary? LastSummary { get; set; }

        public int Calls { get; set; }
    }

    private sealed class Subscription(DemoSession session, Action<SessionUpdate> onUpdate) : ISessionSubscription
    {
        private bool _disposed;

        public string SessionId => session.Id;

        public IReadOnlyList<SessionUpdate> History { get; } = [.. session.History];

        public void Deliver(SessionUpdate update)
        {
            if (!_disposed)
                onUpdate(update);
        }

        public void Dispose()
        {
            _disposed = true;
            session.Subscribers.Remove(this);
        }
    }

    /// <summary>
    /// A scripted turn: steps run one after another, each a one-shot timer of the provider's that posts to the UI thread.
    /// A step that asks the user calls <see cref="Wait"/>; the answer's handler adds the steps that follow and goes on.
    /// </summary>
    private sealed class Script(DemoSessionService service, DemoSession session)
    {
        private readonly LinkedList<(TimeSpan Delay, Action Step)> _steps = new();
        private readonly List<ITimer> _held = [];
        private ITimer? _timer;
        private DateTimeOffset _at = service._clock;

        public DemoSession Session { get; } = session;

        public bool Ended { get; private set; }

        public bool Waiting { get; private set; }

        /// <summary>What answering each open prompt does; the flag is true when the deadline answered it.</summary>
        public Dictionary<string, Action<ApprovalAnswer, bool>> Approvals { get; } = new(StringComparer.Ordinal);

        public Script Then(TimeSpan delay, Action step)
        {
            _steps.AddLast((delay, step));
            return this;
        }

        public Script Then(int milliseconds, Action step) => Then(TimeSpan.FromMilliseconds(milliseconds), step);

        /// <summary>Puts a step before the remaining ones: the next chunk of a stream that is running.</summary>
        public void Insert(int milliseconds, Action step) => _steps.AddFirst((TimeSpan.FromMilliseconds(milliseconds), step));

        /// <summary>Keeps a timer the script owns (a deadline), so ending the script stops it.</summary>
        public void Hold(ITimer timer) => _held.Add(timer);

        /// <summary>Stops after the current step until an answer calls <see cref="Go"/>.</summary>
        public void Wait() => Waiting = true;

        public void Go()
        {
            Waiting = false;
            Arm();
        }

        public void End()
        {
            Ended = true;
            _timer?.Dispose();
            _timer = null;
            foreach (var held in _held)
                held.Dispose();
            _held.Clear();
            _steps.Clear();
            Approvals.Clear();
        }

        private void Arm()
        {
            if (Ended || Waiting || _timer is not null || _steps.Count == 0)
                return;
            var (delay, step) = _steps.First!.Value;
            _steps.RemoveFirst();
            var due = _at + delay;
            _timer = service._time.CreateTimer(_ => service._dispatcher.Post(() => Fire(due, step)), null, delay, Timeout.InfiniteTimeSpan);
        }

        private void Fire(DateTimeOffset due, Action step)
        {
            _timer?.Dispose();
            _timer = null;
            if (Ended)
                return;
            service.AdvanceClock(due);
            _at = service._clock;
            step();
            Arm();
        }
    }
}
