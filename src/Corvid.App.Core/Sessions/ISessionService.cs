using System.Collections.Immutable;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// A session as lists show it (the rail, the Board, the attention tray): title, workspace, live state and what it waits
/// on. Built from the log and the running session; times are absolute, and the views word them against the clock.
/// </summary>
public sealed record SessionSummary(string Id, string Title)
{
    public string Workspace { get; init; } = "";

    public string Branch { get; init; } = "";

    /// <summary>Where it was started: cli, acp, http, gateway or job.</summary>
    public string Surface { get; init; } = "cli";

    /// <summary>Running, waiting on the user, idle, or a job run.</summary>
    public SessionState State { get; init; }

    /// <summary>The last event in the log.</summary>
    public DateTimeOffset LastActivity { get; init; }

    /// <summary>When the running turn's current step started; the rail counts from it ("12 s").</summary>
    public DateTimeOffset? StepStarted { get; init; }

    /// <summary>The first open approval's deadline; the rail counts down to it ("4:12").</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>The running turn's step ("step 2 of 6") and what it does ("write_file docs/releases/v0.2.md").</summary>
    public int? Step { get; init; }

    public int? MaxSteps { get; init; }

    public string? Activity { get; init; }

    /// <summary>The generator now ("claude (escalated)", "qwen3.5-0.8b").</summary>
    public string? Model { get; init; }

    /// <summary>The generator is a cloud model: requests leave the machine.</summary>
    public bool Cloud { get; init; }

    public int Turns { get; init; }

    public bool Tainted { get; init; }

    /// <summary>Kept at the top of the rail (UI state in <c>ui.db</c>, G-7).</summary>
    public bool Pinned { get; init; }

    /// <summary>Another process drives it; this window can only watch (G-12).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>What drives it when <see cref="ReadOnly"/> ("a terminal (corvid chat --session cli-c3d2)").</summary>
    public string? Holder { get; init; }

    public string? ForkOf { get; init; }

    public int? ForkTurn { get; init; }

    public int PendingApprovals { get; init; }

    /// <summary>The first open approval in a line ("rm -rf build/ &amp;&amp; dotnet test").</summary>
    public string? Approval { get; init; }

    /// <summary>Open clarifying questions.</summary>
    public int PendingQuestions { get; init; }

    /// <summary>The first open question's short title ("which version number?").</summary>
    public string? Question { get; init; }

    /// <summary>A line of the latest exchange, for search and the Board.</summary>
    public string? Preview { get; init; }

    /// <summary>For a job run: the job's name, its schedule in words and when the run started.</summary>
    public string? Job { get; init; }

    public string? Schedule { get; init; }

    public DateTimeOffset? RunAt { get; init; }

    /// <summary>Items waiting on the user here, as the attention tray counts them: an approval queue is one, each question one.</summary>
    public int Attention => (PendingApprovals > 0 ? 1 : 0) + PendingQuestions;
}

/// <summary>An answer to an approval prompt (docs/plan/ui.md §4.4).</summary>
public enum ApprovalChoice
{
    Once,
    Session,
    /// <summary>Writes the previewed rule, with any narrowed paths, then allows.</summary>
    Always,
    Deny,
}

/// <summary>What the user chose on a card.</summary>
/// <param name="Reason">For a denial: "deny and say why", sent as the next message.</param>
public sealed record ApprovalAnswer(ApprovalChoice Choice, string? Reason = null)
{
    /// <summary>
    /// For <see cref="ApprovalChoice.Always"/>: each previewed rule's paths as the user narrowed them, in the preview's
    /// order. Empty keeps the preview's paths. Needs G-11.
    /// </summary>
    public ImmutableArray<ImmutableArray<string>> RulePaths { get; init; } = [];

    public static ApprovalAnswer Once { get; } = new(ApprovalChoice.Once);

    public static ApprovalAnswer Deny { get; } = new(ApprovalChoice.Deny);
}

/// <summary>An open session: its history so far, and live updates until disposed.</summary>
public interface ISessionSubscription : IDisposable
{
    string SessionId { get; }

    /// <summary>
    /// Every update so far, in order: what replaying the log gives (<c>session/load</c>). Live updates that follow are
    /// exactly the ones after these.
    /// </summary>
    IReadOnlyList<SessionUpdate> History { get; }
}

/// <summary>
/// The app's one seam to the engine (docs/plan/ui.md U-1, §8): sessions as summaries and update streams, and the
/// requests a person makes of them. The in-process engine (Corvid.App.Engine), a remote engine in attached mode (G-1) and
/// the demo each implement it; views never touch the runtime. Everything a view hears comes through the dispatcher it was
/// given, on the UI thread. A request returns when the engine has taken it; what it changes arrives as updates.
/// </summary>
public interface ISessionService
{
    /// <summary>Every session, newest activity first, with its summary and live state.</summary>
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the composer's <c>!</c> shell line and <c>#</c> memory line exist (docs/plan/tui.md §4.1): the placeholder
    /// names them only when they do. The demo's do; a service without them refuses such a line rather than sending it.
    /// </summary>
    bool HasComposerModes => true;

    /// <summary>A session's summary changed, or a session appeared (a fork, a job run). Raised on the UI thread.</summary>
    event EventHandler<SessionSummary>? SessionChanged;

    /// <summary>
    /// The whole list may differ from the last <see cref="ListAsync"/>: the engine started after the window opened (First
    /// run's downloads finished), so a list that failed then can be read now. Raised on the UI thread. Added in A4 as a
    /// default member that is never raised (the demo lists at once).
    /// </summary>
    event EventHandler? ListChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Opens a session: its history, then each later update through <paramref name="onUpdate"/> on the UI thread, in
    /// order, until the subscription is disposed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No session has that id.</exception>
    Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default);

    /// <summary>Sends a message, starting a turn. While a turn runs, use <see cref="QueueAsync"/> or <see cref="SteerAsync"/>.</summary>
    Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>Queues a message behind the running turn (Enter while it runs); it is sent when the turn ends.</summary>
    Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>Removes a queued message unsent (the chip's ×).</summary>
    Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default);

    /// <summary>Moves a queued message to another place in the queue.</summary>
    Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default);

    /// <summary>Takes a queued message back out of the queue, returning its text for the editor (Up); null when it was sent.</summary>
    Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default);

    /// <summary>Delivers a message at the running turn's next step checkpoint, before the generator sees it (Ctrl+Enter, K-3).</summary>
    Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="SteerAsync"/> reaches a running turn here: the turn strip offers Ctrl+Enter only when it does, and
    /// Ctrl+Enter queues like Enter otherwise. Added in A4 as a default member (the demo steers); an engine without K-3 says false.
    /// </summary>
    bool CanSteer => true;

    /// <summary>Interrupts the running turn (Esc): the work so far stays, open prompts are denied, and the queue goes next.</summary>
    Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Answers an approval prompt. An answer to a prompt that has closed is ignored: nothing runs on a stale prompt.</summary>
    Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default);

    /// <summary>Answers a clarifying question with an option or free text (K-6).</summary>
    Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default);

    /// <summary>Runs a slash command line through the shared registry; its output arrives as a <see cref="CommandResult"/>.</summary>
    Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default);

    /// <summary>Sets the session's approval mode (G-10); the engine reports it back with <see cref="ApprovalModeChanged"/>.</summary>
    Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default);

    /// <summary>Sets or clears the standing goal (null or blank clears it).</summary>
    Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default);

    /// <summary>Compacts the conversation before the latest turn now, keeping what <paramref name="focus"/> names (<c>/compact</c>).</summary>
    Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forks the session through a log position (its head when null) into a new session and returns its id: "edit and
    /// resend", "branch from here", an aside.
    /// </summary>
    Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default);

    /// <summary>Renames the session (G-6).</summary>
    Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="CreateAsync"/> starts sessions here: the engine does, the demo's scripted scenario does not (its
    /// Board and Ctrl+N say what a new session comes with instead). Added for the A3 end-to-end pass, as a default member
    /// so every other implementation keeps refusing.
    /// </summary>
    bool CanCreate => false;

    /// <summary>
    /// Starts a new, empty session in the window's workspace and returns its id; the rail lists it as a new session until
    /// its first message gives it a title.
    /// </summary>
    Task<string> CreateAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new Composition.ServiceUnavailableException(
            "New sessions come with the engine, which opens one in the window's workspace."));
}
