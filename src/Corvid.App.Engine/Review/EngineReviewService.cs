using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Review;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.History;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.State;
using Corvid.Kernel.Checkpoints;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using KernelEntry = Corvid.Kernel.Checkpoints.LedgerEntry;

namespace Corvid.App.Engine.Review;

/// <summary>
/// Review over the runtime's write ledger (docs/plan/ui.md §4.7, F4). The files a scope lists are those its finished calls
/// changed; each file's diff runs from the file as it was before the first of them (its before checkpoint, which the whole
/// file can be put back to) to the file in the workspace now, so the person's own edits show and a file already back as it
/// was drops out. Texts are read through the file tools' rules; a path the tools may not open is listed by name only.
/// Comments go to the session as one message through the window's session service. One file goes back with
/// <see cref="CorvidSession.RollbackToAsync"/>, files only and that path only (G-3), merging three ways as a restore does.
/// <para>
/// Review points are kept in <c>ui.db</c> (G-7), beside the log, so "since last review" lasts across restarts. Open in
/// editor runs <c>$VISUAL</c> or <c>$EDITOR</c> when one is set and is not a terminal editor, with an argument list and no
/// shell; otherwise it is refused, saying how to set one.
/// </para>
/// </summary>
public sealed class EngineReviewService : IReviewService, IDisposable
{
    /// <summary>The most files one list holds; a session that changed more lists its latest.</summary>
    public const int MaxFiles = 200;

    /// <summary>Editors that need a terminal, which a window cannot give them.</summary>
    private static readonly FrozenNames TerminalEditors = new("vi", "vim", "nvim", "nano", "pico", "emacs", "emacsclient", "micro", "hx", "helix",
        "kak", "joe", "mg", "ed", "ne", "mcedit", "jed", "zile");

    private static readonly FrozenNames GotoEditors = new("code", "code-insiders", "codium", "vscodium", "cursor", "windsurf");

    private static readonly FrozenNames PlusLineEditors = new("gvim", "mvim", "gedit", "kate", "kwrite", "geany", "pluma", "mousepad", "xed",
        "subl", "sublime_text", "gnome-text-editor", "zed");

    private readonly AppEngine _engine;
    private readonly ISessionService _sessions;
    private readonly TimeProvider _time;
    private readonly HistorySignals _signals;
    private readonly Func<string, string?> _environment;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly Lazy<Task<LedgerTexts>> _texts;
    /// <summary>Files put back from here, by session: the checkpoint each went back to.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _reverted = new(StringComparer.Ordinal);

    internal EngineReviewService(AppEngine engine, ISessionService sessions, TimeProvider time, HistorySignals signals,
        Func<string, string?>? environment = null, Func<ProcessStartInfo, Process?>? start = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(signals);
        _engine = engine;
        _sessions = sessions;
        _time = time;
        _signals = signals;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _start = start ?? Process.Start;
        _signals.Changed += OnChanged;
        _texts = new Lazy<Task<LedgerTexts>>(async () => new LedgerTexts(await engine.RuntimeAsync().ConfigureAwait(false)));
    }

    public event EventHandler<string>? ChangesChanged;

    public async Task<ReviewChanges> GetChangesAsync(string sessionId, ReviewScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var texts = await _texts.Value.ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var ledger = SessionLedger.Load(runtime, sessionId, cancellationToken);
            // G-7: the point in ui.db, with the log head it was sent at.
            var point = runtime.UiState.GetReviewPoint(sessionId) is { } kept
                ? (new ReviewPoint(kept.Turn, kept.CommentsSent, kept.At), kept.Head)
                : ((ReviewPoint, long)?)null;
            Func<KernelEntry, bool> inScope = scope switch
            {
                ReviewScope.ThisTurn => entry => ledger.TurnAt(entry.CallSequence) == ledger.Turn,
                // Before the first send from here the point is the session's start.
                ReviewScope.SinceLastReview when point is { } since => entry => entry.CallSequence > since.Item2,
                _ => _ => true,
            };
            var calls = ledger.Entries.Where(entry => entry.After is not null && inScope(entry)).ToList();
            // Each path in the order the calls first changed it, with the first call (what it goes back to) and the last.
            var paths = new List<(string Path, KernelEntry First, KernelEntry Last)>();
            foreach (var call in calls)
            {
                foreach (var path in call.Paths)
                {
                    var index = paths.FindIndex(item => item.Path == path);
                    if (index < 0)
                        paths.Add((path, call, call));
                    else
                        paths[index] = (path, paths[index].First, call);
                }
            }
            if (paths.Count > MaxFiles)
                paths = paths[^MaxFiles..];

            var reverted = _reverted.TryGetValue(sessionId, out var mine) ? mine : null;
            var files = ImmutableArray.CreateBuilder<ReviewFile>();
            foreach (var (path, first, last) in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await FileAsync(texts, path, first, last, ledger).ConfigureAwait(false);
                if (file is not null)
                    files.Add(file);
                else if (reverted is not null && reverted.TryGetValue(path, out var checkpoint))
                    // Back as it was because it was put back from here: listed as reverted until a call changes it again.
                    files.Add(new ReviewFile(path, 0, 0) { Reverted = true, CheckpointId = checkpoint });
            }

            var covering = calls.Where(call => files.Any(file => !file.Reverted && call.Paths.Contains(file.Path))).ToList();
            return new ReviewChanges(sessionId, scope)
            {
                Files = files.ToImmutable(),
                Turn = ledger.Turn,
                ReviewPoint = point?.Item1,
                FirstCheckpoint = covering.FirstOrDefault()?.Before.Checkpoint.CheckpointId,
                LastCheckpoint = covering.LastOrDefault()?.After?.Checkpoint.CheckpointId,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewPoint> SendCommentsAsync(string sessionId, IReadOnlyList<ReviewComment> comments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count == 0)
            throw new ArgumentException("There are no comments to send.", nameof(comments));
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var ledger = await Task.Run(() => SessionLedger.Load(runtime, sessionId, cancellationToken), cancellationToken).ConfigureAwait(false);
        // The session service refuses while a turn runs (the queue comes with A3 sessions); the page shows its reason.
        await _sessions.SendAsync(sessionId, ReviewMessage.Compose(comments), cancellationToken).ConfigureAwait(true);
        var point = new ReviewPoint(ledger.Turn, comments.Count, _time.GetUtcNow());
        // What the ledger held when the comments went is reviewed; "since last review" is every call after it.
        await Task.Run(() => runtime.UiState.SetReviewPoint(new UiReviewPoint(sessionId, point.Turn, point.CommentsSent, point.At, ledger.Head)),
            cancellationToken).ConfigureAwait(true);
        return point;
    }

    public async Task<FileRevertResult> RevertFileAsync(string sessionId, string path, string checkpointId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var texts = await _texts.Value.ConfigureAwait(false);
        if (!texts.Readable(path))
            return new FileRevertResult(path, checkpointId, false, true, "Corvid may not open it, so it cannot be put back from here");
        CorvidSession session;
        // M2: a session this revert opens is let go of once it is done, so a terminal can drive it again.
        var opened = false;
        try
        {
            opened = !runtime.TryGetSession(sessionId, out var open);
            session = opened ? runtime.OpenSession(sessionId, EngineSessionService.Surface) : open;
        }
        catch (SessionHeldException exception)
        {
            // G-12: the session's driver in another process is the one to put files back; nothing is changed from here.
            return new FileRevertResult(path, checkpointId, false, true, exception.Message.TrimEnd('.'));
        }
        RestoreResult? result;
        try
        {
            result = await session.RollbackToAsync(checkpointId, RestoreScope.FilesOnly, [path], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (opened)
                await runtime.ReleaseDriverAsync(sessionId).ConfigureAwait(false);
        }
        if (result is null)
            return new FileRevertResult(path, checkpointId, false, true, $"{checkpointId} is no longer in the ledger");
        if (result.Status == RestoreStatus.Refused)
            return new FileRevertResult(path, checkpointId, false, true,
                result.Conflicts.FirstOrDefault(conflict => conflict.Path == path)?.Reason ?? result.Detail ?? "the restore was refused");
        _reverted.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal))[path] = checkpointId;
        _signals.Raise(sessionId);
        return new FileRevertResult(path, checkpointId, result.Merged.Contains(path), false);
    }

    public async Task<string> OpenInEditorAsync(string path, int line, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var editor = (_environment("VISUAL") is { Length: > 0 } visual ? visual : _environment("EDITOR"))?.Trim();
        if (string.IsNullOrEmpty(editor))
            throw new ServiceUnavailableException("No editor is configured: set $VISUAL (or $EDITOR) to a graphical editor such as code, gedit or kate, then start Corvid again.");
        var words = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = Path.GetFileName(words[0]);
        if (TerminalEditors.Contains(name))
            throw new ServiceUnavailableException($"The editor is {name}, which needs a terminal the window cannot give it: set $VISUAL to a graphical editor such as code, gedit or kate.");
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        string full;
        try
        {
            full = Workspace.For(runtime.Builtin.Environment).Resolve(path);
        }
        catch (ToolArgumentException exception)
        {
            throw new InvalidOperationException(exception.Message);
        }

        line = Math.Max(1, line);
        var start = new ProcessStartInfo(words[0]) { UseShellExecute = false };
        foreach (var word in words.Skip(1))
            start.ArgumentList.Add(word);
        if (GotoEditors.Contains(name))
        {
            start.ArgumentList.Add("--goto");
            start.ArgumentList.Add(FormattableString.Invariant($"{full}:{line}"));
        }
        else
        {
            if (PlusLineEditors.Contains(name))
                start.ArgumentList.Add(FormattableString.Invariant($"+{line}"));
            start.ArgumentList.Add(full);
        }

        try
        {
            using var process = _start(start);
            if (process is null)
                throw new InvalidOperationException($"{words[0]} did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"{words[0]} could not be started: {exception.Message}");
        }

        return string.Join(' ', start.ArgumentList.Prepend(words[0]));
    }

    public void Dispose() => _signals.Changed -= OnChanged;

    private void OnChanged(string sessionId) => ChangesChanged?.Invoke(this, sessionId);

    /// <summary>A changed file from the first call's before checkpoint to the workspace now; null when it is back as it was.</summary>
    private static async Task<ReviewFile?> FileAsync(LedgerTexts texts, string path, KernelEntry first, KernelEntry last, SessionLedger ledger)
    {
        var named = new ReviewFile(path, 0, 0)
        {
            Tool = last.Tool,
            Turn = ledger.TurnAt(last.CallSequence),
            CheckpointId = first.Before.Checkpoint.CheckpointId,
        };
        if (!texts.Readable(path))
            return named with { Readable = false, Note = "not readable by Corvid" };
        var old = await texts.AtAsync(first.Before.Checkpoint.Revision, path).ConfigureAwait(false);
        var now = texts.Now(path);
        if ((old.Note ?? now.Note) is { } note)
            return named with { Note = note };
        if (!old.Exists && !now.Exists || old.Exists && now.Exists && old.Text == now.Text)
            return null;
        var (added, removed) = LedgerTexts.Count(old, now, path);
        return named with { Added = added, Removed = removed, OldText = old.Exists ? old.Text : null, NewText = now.Text ?? "" };
    }

    /// <summary>A small set of program names, compared exactly.</summary>
    private sealed class FrozenNames(params string[] names)
    {
        private readonly HashSet<string> _names = new(names, StringComparer.Ordinal);

        public bool Contains(string name) => _names.Contains(name);
    }
}
