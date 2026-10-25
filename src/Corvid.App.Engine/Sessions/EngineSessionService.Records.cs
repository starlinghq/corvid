using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using Corvid.Abstractions.Sessions;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Host.State;
using Corvid.Kernel.Sessions;
using Corvid.Memory;
using Microsoft.Extensions.DependencyInjection;
using RecapSources = Corvid.Abstractions.Events.RecapSources;
using SessionRecapped = Corvid.Abstractions.Events.SessionRecapped;
using SessionTitled = Corvid.Abstractions.Events.SessionTitled;
using TitleSources = Corvid.Abstractions.Events.TitleSources;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// A4's records on the engine: titles and recaps from the log (G-6, extending K-9), pins, drafts and when a session was
/// last seen in <c>ui.db</c> (G-7, shared with the CLI), and the composer's <c>!</c> and <c>#</c> lines
/// (docs/plan/tui.md §4.1), read by the host's <see cref="ComposerLine"/> as the CLI reads them.
/// </summary>
public sealed partial class EngineSessionService
{
    private Action? _unwatchRecords;
    // Sessions the person is looking at in the Sessions area (not the Board's peeks).
    private readonly ConcurrentDictionary<string, byte> _viewing = new(StringComparer.Ordinal);

    private UiStateStore Ui => _engine.Services.GetRequiredService<UiStateStore>();

    /// <summary>The <c>!</c> shell line and the <c>#</c> memory line are built (A4): the placeholder names them.</summary>
    public bool HasComposerModes => true;

    public bool CanPin => true;

    public bool CanRecap => true;

    /// <summary>
    /// Renames the session (G-6): a <c>SessionTitled</c> by the user in the log, which the CLI's <c>/sessions</c> and ACP's
    /// list read too. The header and the rail follow through the runtime's <see cref="CorvidRuntime.TitleChanged"/>.
    /// </summary>
    public async Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (IsJobRun(sessionId))
            throw new InvalidOperationException($"Session {sessionId} is read-only here: {JobHolder}.");
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        // The log is SQLite: off the UI thread. A title never starts a session of its own.
        var rename = Task.Run(() =>
        {
            if (runtime.Log.GetHead(sessionId, cancellationToken) == 0 && !_created.ContainsKey(sessionId) && !runtime.TryGetSession(sessionId, out _))
                throw new KeyNotFoundException($"There is no session '{sessionId}' in Corvid's log.");
            runtime.Rename(sessionId, title);
        }, cancellationToken);
        // Waited for by IdleAsync, as a turn is.
        Track(rename);
        await rename.ConfigureAwait(false);
    }

    /// <summary>Pins or unpins in <c>ui.db</c> (G-7), where the CLI's <c>/pin</c> writes too; the rail follows the summary.</summary>
    public async Task SetPinnedAsync(string sessionId, bool pinned, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var pin = Task.Run(() => Ui.SetPinned(sessionId, pinned, _time.GetUtcNow()), cancellationToken);
        Track(pin);
        await pin.ConfigureAwait(false);
        Refresh(runtime, sessionId);
    }

    /// <summary>The draft <c>ui.db</c> keeps for the session (G-7): this window's from an earlier run, or the CLI's.</summary>
    public Task<SessionDraft?> LoadDraftAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.Run(() => Ui.GetDraft(sessionId) is { } kept ? new SessionDraft(kept.Text, kept.Stash) : null, cancellationToken);

    public Task SaveDraftAsync(string sessionId, SessionDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(draft);
        var save = Task.Run(() => Ui.SetDraft(sessionId, draft.Text, draft.Stash, _time.GetUtcNow()), cancellationToken);
        // Waited for by IdleAsync, so a closing window's last draft is written.
        Track(save);
        return save;
    }

    /// <summary>
    /// A recap of what happened since the last one (G-6), by the local generator; it arrives through the runtime's
    /// <see cref="CorvidRuntime.Recapped"/>. Refused when there is no local generator, with why.
    /// </summary>
    public async Task RecapAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        if (runtime.Titles.Unavailable is { } why)
            throw new ServiceUnavailableException($"No recap: {why}.");
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var writing = Task.Run(() => runtime.RecapAsync(sessionId, RecapSources.Requested, cancellationToken), cancellationToken);
        Track(writing);
        var recap = await writing.ConfigureAwait(false);
        if (recap is null)
            Publish(channel, _ => [new Notice(NoticeKind.Info, "Nothing happened since the last recap.") { At = _time.GetUtcNow() }]);
    }

    /// <summary>
    /// The person looks at a session, or stopped (G-6, G-7): when they come back after <c>Corvid:Sessions:RecapAfterMinutes</c>
    /// the local generator recaps what happened while they were away; either way the session is seen from now in
    /// <c>ui.db</c>, which the CLI's resume reads too.
    /// </summary>
    public async Task ViewedAsync(string sessionId, bool viewing, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!viewing)
        {
            _viewing.TryRemove(sessionId, out _);
            Seen(sessionId);
            return;
        }
        _viewing[sessionId] = 0;
        CorvidRuntime runtime;
        try
        {
            runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        }
        catch (ServiceUnavailableException)
        {
            // No runtime (models missing: First run says so): nothing to recap or record yet.
            return;
        }
        var recap = await Task.Run(() =>
        {
            if (IsJobRun(sessionId) || SessionCatalogue.Describe(runtime.Log, sessionId, cancellationToken) is not { } session)
                return false;
            var now = _time.GetUtcNow();
            var due = runtime.Titles.ShouldRecapOnReturn(session, Ui.LastSeen(sessionId)?.At, now);
            Ui.MarkSeen(sessionId, session.Head, now);
            return due;
        }, cancellationToken).ConfigureAwait(false);
        if (recap)
            Track(RecapOnReturnAsync(runtime, sessionId));
    }

    private static async Task RecapOnReturnAsync(CorvidRuntime runtime, string sessionId)
    {
        try
        {
            await runtime.RecapAsync(sessionId, RecapSources.Absence).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // A recap is a convenience: the session reads as it is.
            Log("corvid app: no recap on return: " + exception.Message);
        }
    }

    /// <summary>Records the session as seen through its head now, off the UI thread; a failure is logged.</summary>
    private void Seen(string sessionId)
    {
        if (!_engine.Runtime.IsCompletedSuccessfully)
            return;
        var runtime = _engine.Runtime.Result;
        Track(Task.Run(() => Ui.MarkSeen(sessionId, runtime.Log.GetHead(sessionId), _time.GetUtcNow())));
    }

    /// <summary>The pinned sessions in <c>ui.db</c>; none (and a line in the log) when it cannot be read.</summary>
    private IReadOnlySet<string> Pins()
    {
        try
        {
            return Ui.Pinned();
        }
        catch (Exception exception) when (exception is DbException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log("corvid app: pins could not be read from ui.db: " + exception.Message);
            return ImmutableHashSet<string>.Empty;
        }
    }

    /// <summary>
    /// A <c>#</c> line is staged at once through memory's gated write path (never queued: it starts no turn), and its
    /// outcome shows as a notice; a <c>!</c> line or a message returns null and goes on as a turn. A mark with nothing after
    /// it is refused with what to type, and the draft stays.
    /// </summary>
    private async Task<QueuedMessage?> ComposerLineAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        switch (ComposerLine.Parse(text))
        {
            case ComposerLine.Empty empty:
                throw new InvalidOperationException(empty.Message);
            case ComposerLine.Memory memory:
                await RememberAsync(sessionId, memory.Text, cancellationToken).ConfigureAwait(false);
                return new QueuedMessage("remembered", text.Trim());
            default:
                return null;
        }
    }

    private async Task RememberAsync(string sessionId, string fact, CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        var session = await SessionAsync(runtime, sessionId, cancellationToken).ConfigureAwait(false);
        // The extractor and the gate run on their devices; memory's store is SQLite: off the UI thread.
        var result = await Task.Run(() => session.RememberAsync(fact, cancellationToken), cancellationToken).ConfigureAwait(false);
        var (line, problem) = ComposerLine.Describe(result, fact, withId: false);
        var kind = problem ? NoticeKind.Warning : result.Status == MemoryWriteStatus.Staged ? NoticeKind.MemoryStaged : NoticeKind.Info;
        Publish(channel, _ => [new Notice(kind, line) { Target = result.Pending?.PendingId, At = _time.GetUtcNow() }]);
    }

    /// <summary>Posts a turn's line: a <c>!</c> line runs its command as the user's shell call, anything else is a message.</summary>
    private static Task<Task<Delivery>> PostLineAsync(CorvidSession session, string text, CancellationToken cancellationToken) =>
        ComposerLine.Parse(text) is ComposerLine.Shell shell
            ? session.PostShellAsync(text.Trim(), new ShellLine(shell.Command, shell.LocalOnly), cancellationToken)
            : session.PostAsync(text, EnvelopeOrigin.User, cancellationToken);

    /// <summary>The latest recap as the transcript's last row, when nothing has happened since it.</summary>
    private static Notice? RecapNotice(SessionDescription? session, DateTimeOffset at) =>
        session?.Recap is { } recap && session.Head <= recap.ThroughSequence + 2 ? Recap(recap, at) : null;

    private static Notice Recap(SessionRecapped recap, DateTimeOffset at) =>
        new(NoticeKind.Recap, recap.Source == RecapSources.Absence ? "While you were away" : "Recap") { Detail = recap.Text, At = at };

    /// <summary>
    /// Once the runtime is up, hears titles and recaps written in this process (a generated title after the first
    /// exchange, a rename, a recap): an open session shows them, and the rail and the Board get the new summary. Not
    /// tracked as work, as <see cref="WatchJobsAsync"/> is not.
    /// </summary>
    private async Task WatchRecordsAsync()
    {
        CorvidRuntime runtime;
        try
        {
            runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        }
        catch (ServiceUnavailableException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log("corvid app: titles and recaps will not reach the window: " + exception.Message);
            return;
        }

        void OnTitle(string id, SessionTitled titled)
        {
            if (_disposed)
                return;
            if (_channels.TryGetValue(id, out var channel))
                Publish(channel, _ => [new TitleChanged(titled.Title) { Generated = titled.Source == TitleSources.Generated, At = _time.GetUtcNow() }]);
            Refresh(runtime, id);
        }

        void OnRecap(string id, SessionRecapped recap)
        {
            if (!_disposed && _channels.TryGetValue(id, out var channel))
                Publish(channel, _ => [Recap(recap, _time.GetUtcNow())]);
        }

        runtime.TitleChanged += OnTitle;
        runtime.Recapped += OnRecap;
        _unwatchRecords = () =>
        {
            runtime.TitleChanged -= OnTitle;
            runtime.Recapped -= OnRecap;
        };
        if (_disposed)
            UnwatchRecords();
    }

    private void UnwatchRecords() => Interlocked.Exchange(ref _unwatchRecords, null)?.Invoke();
}
