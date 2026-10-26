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

    /// <summary>
    /// A6V2-05: <see cref="Workspace"/> is no longer a directory on the engine's machine (a removed worktree), so nothing new
    /// can start there; the session is still listed.
    /// </summary>
    public bool WorkspaceGone { get; init; }

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

    /// <summary>
    /// A5: this session is a child of <see cref="ChildSessionChanged.ChildId"/>'s parent (<see cref="ParentId"/>): what it was
    /// started with and how it ended. The Board shows it under its parent; it is read-only.
    /// </summary>
    public ChildSessionChanged? Child { get; init; }

    /// <summary>A5: the session this one is a child of.</summary>
    public string? ParentId { get; init; }

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

/// <summary>A session's composer as kept in <c>ui.db</c> (G-7): its text and the draft put aside with <c>Ctrl+S</c>.</summary>
public sealed record SessionDraft(string Text, string? Stash = null)
{
    public bool IsEmpty => Text.Length == 0 && string.IsNullOrEmpty(Stash);
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

    /// <summary>Whether messages here can carry images (K-8, A6 default member: false; the engine's can).</summary>
    bool CanSendImages => false;

    /// <summary>
    /// K-8 (A6): sends a message with images (pasted, dropped or attached), queued behind a running turn as Enter does. The
    /// engine checks, strips and stores the images, or refuses the message with why. A default member: without images it
    /// is <see cref="SendAsync(string, string, CancellationToken)"/>; with any, a service without image support refuses.
    /// </summary>
    Task SendAsync(string sessionId, string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken = default) =>
        images is null || images.Count == 0 ? SendAsync(sessionId, text, cancellationToken)
            : Task.FromException(new Composition.ServiceUnavailableException("Images in messages come with the engine (K-8)."));

    /// <summary>
    /// K-8 (A6): an image file the person dropped or named in the attach box, read under the engine's rules (no credential
    /// file, no protected location, size checked before reading). A default member: refused without image support.
    /// </summary>
    /// <exception cref="InvalidOperationException">Why it was not read, for the person.</exception>
    Task<ImageAttachment> ReadImageFileAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
        Task.FromException<ImageAttachment>(new Composition.ServiceUnavailableException("Images in messages come with the engine (K-8)."));

    /// <summary>
    /// K-8 (A6): a message's image bytes by digest, for its thumbnail; null when none are stored (or the service keeps none).
    /// The bytes are untrusted: drawn bounded, never opened by anything else.
    /// </summary>
    Task<byte[]?> ReadImageAsync(string sessionId, string digest, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);

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

    /// <summary>
    /// A5: stops a running child session (plan §14): its turn is cancelled and its parent hears it ended cancelled. False when
    /// no such child runs where this service can reach it (the demo has none).
    /// </summary>
    Task<bool> StopChildAsync(string childId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>Renames the session (G-6).</summary>
    Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// The draft kept for a session (G-7, <c>ui.db</c>), shared with the CLI and kept across restarts; null when none is
    /// kept. Added in A4 as a default member: a service without <c>ui.db</c> keeps drafts in the window only.
    /// </summary>
    Task<SessionDraft?> LoadDraftAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<SessionDraft?>(null);

    /// <summary>Keeps a session's draft (G-7); an empty draft with nothing put aside forgets it. A no-op without <c>ui.db</c>.</summary>
    Task SaveDraftAsync(string sessionId, SessionDraft draft, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Whether <see cref="SetPinnedAsync"/> keeps pins (A4 default member: false).</summary>
    bool CanPin => false;

    /// <summary>
    /// Pins or unpins a session (G-7): pinned sessions head the rail (and the CLI's <c>/sessions</c>); the change comes back
    /// as a <see cref="SessionChanged"/> summary.
    /// </summary>
    Task SetPinnedAsync(string sessionId, bool pinned, CancellationToken cancellationToken = default) =>
        Task.FromException(new Composition.ServiceUnavailableException("Pinning comes with ui.db (G-7), which the engine keeps."));

    /// <summary>
    /// The person started (<paramref name="viewing"/> true) or stopped looking at a session in the Sessions area (G-6, G-7):
    /// the engine records when it was last seen in <c>ui.db</c>, and on a return after an absence has the local generator
    /// recap what happened. Peeks (the Board's watch) do not count. A no-op by default (A4).
    /// </summary>
    Task ViewedAsync(string sessionId, bool viewing, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Whether <see cref="RecapAsync"/> can write a recap (A4 default member: false).</summary>
    bool CanRecap => false;

    /// <summary>
    /// Has the local generator recap the session's events since its last recap (G-6); the recap arrives as a
    /// <see cref="Notice"/> of kind <see cref="NoticeKind.Recap"/>.
    /// </summary>
    Task RecapAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromException(new Composition.ServiceUnavailableException("Recaps come with the engine: its local generator writes them (G-6)."));

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

    /// <summary>
    /// A6 (G-4): whether <see cref="CreateAsync(NewSessionRequest, CancellationToken)"/> starts a session in a workspace the
    /// person picks, or in a new git worktree. A default member (false): the demo's scenario has one workspace.
    /// </summary>
    bool CanChooseWorkspace => false;

    /// <summary>
    /// A6 (G-4): starts a new, empty session where <paramref name="request"/> says and returns its id. A session keeps its
    /// workspace for its life. A worktree is made only after the person approves it on <see cref="NewSessionRequest.AskIn"/>'s
    /// card, since it writes outside the workspace; a refusal throws with why. The default member starts one in the
    /// window's workspace when the request names none, and refuses otherwise.
    /// </summary>
    Task<string> CreateAsync(NewSessionRequest request, CancellationToken cancellationToken = default) =>
        request is { Workspace: null, Worktree: false }
            ? CreateAsync(cancellationToken)
            : Task.FromException<string>(new Composition.ServiceUnavailableException(
                "Sessions in other workspaces come with the engine (G-4), which keeps a workspace per session."));
}

/// <summary>
/// A6 (G-4): where a new session works. <see cref="Workspace"/> null is the window's configured workspace;
/// <see cref="Worktree"/> makes a new git worktree of <see cref="AskIn"/>'s workspace and starts the session in it.
/// </summary>
/// <param name="Workspace">An absolute path of an existing directory, or null.</param>
public sealed record NewSessionRequest(string? Workspace = null)
{
    /// <summary>Make a new git worktree (of <see cref="AskIn"/>'s workspace) and start the session there.</summary>
    public bool Worktree { get; init; }

    /// <summary>The worktree's name (its branch is <c>corvid/&lt;name&gt;</c>); one from the time when null.</summary>
    public string? WorktreeName { get; init; }

    /// <summary>The session whose card asks the person to approve the worktree, and whose repository it is of.</summary>
    public string? AskIn { get; init; }
}
