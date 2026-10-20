using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Kernel.Events;
using Corvid.Tools;

namespace Corvid.Kernel.Checkpoints;

/// <summary>
/// One tool call's entry in the write ledger: the workspace before the call changed files and after it had. A restore
/// reverts exactly the paths that changed between the two.
/// </summary>
/// <param name="CallSequence">Where the call's <c>ToolCallIssued</c> is in the log.</param>
/// <param name="After">Null when the call never finished recording (a crash): a restore across it is refused.</param>
public sealed record LedgerEntry(string CallId, string Tool, long CallSequence, RecordedCheckpoint Before, RecordedCheckpoint? After)
{
    public ImmutableArray<string> Paths => After?.Checkpoint.Paths ?? [];
}

/// <summary>What a restore will do: go back to <see cref="Position"/> and revert the ledger entries after it.</summary>
/// <param name="Position">The log position the transcript returns to.</param>
/// <param name="Reason"><c>undo</c> or <c>rollback</c>.</param>
/// <param name="Entries">Ledger entries after the position, oldest first.</param>
/// <param name="Unrecorded">
/// Calls after the position that changed files without a checkpoint (checkpoints were off or unavailable): their changes
/// cannot be reverted, so a restore across them is refused rather than leaving files and transcript out of step.
/// </param>
public sealed record RestorePlan(long Position, string Reason, ImmutableArray<LedgerEntry> Entries, ImmutableArray<ToolCallIssued> Unrecorded = default)
{
    public ImmutableArray<ToolCallIssued> Unrecorded { get; init; } = Unrecorded.IsDefault ? [] : Unrecorded;
}

/// <summary>A path the restore would not change, because doing so would lose something the user has.</summary>
public sealed record RestoreConflict(string Path, string Reason);

public enum RestoreStatus
{
    /// <summary>Files were put back and the transcript moved to the plan's position.</summary>
    Restored,
    /// <summary>Nothing changed: a conflict, or the plan cannot be applied.</summary>
    Refused,
}

/// <param name="Restored">Paths put back, relative to the workspace.</param>
/// <param name="Merged">Paths whose current content was merged with the revert rather than replaced (the user's edits kept).</param>
/// <param name="Detail">Why the restore was refused.</param>
public sealed record RestoreResult(RestoreStatus Status, RestorePlan Plan, ImmutableArray<string> Restored, ImmutableArray<string> Merged,
    ImmutableArray<RestoreConflict> Conflicts, string? Detail = null, string? CheckpointId = null);

/// <summary>
/// Checkpoints for the turn loop and the <c>/undo</c> and <c>/rollback</c> commands (plan §15, §3.2, §10.2). A checkpoint
/// is taken before a tool call that changes files and another after it, both recorded as <c>CheckpointTaken</c> events
/// linked to the call: the write ledger. Undo and rollback are one operation on log positions: choose a position, revert
/// every ledger entry after it, newest first, and append <c>SessionRewound</c> so the transcript shows the session as it
/// was there. A path is reverted only when it still holds what Corvid wrote; otherwise the revert is merged three ways
/// into the current content, and if that conflicts, or the file is binary, the whole restore is refused. The user's own
/// edits are never overwritten.
/// </summary>
public sealed class CheckpointService
{
    private const int MaxAppendAttempts = 8;

    // Effects a checkpoint cannot take back: they reach outside the workspace, or run code whose writes are unknown.
    private const ActionEffects Irreversible = ActionEffects.Network | ActionEffects.Signal | ActionEffects.Elevated | ActionEffects.System
        | ActionEffects.Configuration | ActionEffects.Messaging | ActionEffects.Schedule | ActionEffects.Memory | ActionEffects.Remote
        | ActionEffects.Execute;

    public CheckpointService(ShadowStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ShadowStore Store { get; }

    /// <summary>Whether a call needs a checkpoint: a tool that changes files, running something other than a read-only command.</summary>
    public static bool Covers(ToolDescriptor tool, ActionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(analysis);
        return tool.Risk.SideEffects.HasFlag(SideEffects.Filesystem)
            && (analysis.Unsupported is not null || analysis.Commands.Any(command => !command.ReadOnly));
    }

    /// <summary>
    /// Whether a checkpoint could undo the action (the <c>reversible</c> field of the <c>risk</c> state, plan §10.2 step 4):
    /// the store works, the action only writes or deletes files at resolved paths inside the workspace, and none of those
    /// paths is excluded, oversized or (for a directory) ignored by the workspace's <c>.gitignore</c>.
    /// </summary>
    public async Task<bool> IsReversibleAsync(ActionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!Store.IsAvailable || analysis.Unsupported is not null || (analysis.Effects & Irreversible) != 0)
            return false;
        if ((analysis.Effects & (ActionEffects.Write | ActionEffects.Delete)) == 0)
            return true;
        if (analysis.Commands.Any(command => !command.Unresolved.IsEmpty))
            return false;
        foreach (var target in analysis.Targets.Where(target => target.Kind == TargetKind.Path && target.Access is TargetAccess.Write or TargetAccess.Delete))
        {
            if (target.Dynamic || target.Location is not (PathLocation.Inside or PathLocation.WorkspaceRoot))
                return false;
            var value = target.Pattern ? Path.GetDirectoryName(target.Value) ?? target.Value : target.Value;
            if (Store.Relative(value) is not { } relative)
            {
                // The workspace itself (a pattern or recursive target at its root): what it holds is checkpointed.
                if (Store.IsWorkspace(value))
                    continue;
                return false;
            }
            if (Store.Excluded(relative))
                return false;
            var full = Path.Combine(Store.Workspace, relative);
            if (target.Pattern || target.Recursive || Directory.Exists(full))
            {
                if (await Store.IsIgnoredAsync(relative).ConfigureAwait(false))
                    return false;
            }
            else if (new FileInfo(full) is { Exists: true, LinkTarget: null } file && file.Length > Store.MaxFileBytes)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The checkpoint before a call changes files. Its file targets are captured even when the workspace ignores them.</summary>
    /// <exception cref="CheckpointException">No checkpoint could be taken; the call must not run.</exception>
    public async Task<CheckpointTaken> BeforeAsync(string callId, ActionAnalysis analysis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        var targets = Include(analysis).ToList();
        var commit = await Store.SnapshotAsync($"before {callId}", targets).ConfigureAwait(false);
        // Target files that exist but are not in the checkpoint (oversized, excluded): their earlier content is gone once the
        // call runs, so a restore must refuse them rather than take "missing" for their earlier state.
        var captured = await Store.ReadAsync(commit, targets).ConfigureAwait(false);
        var uncaptured = targets.Select(Store.Relative).OfType<string>()
            .Where(path => !captured.ContainsKey(path) && new FileInfo(Path.Combine(Store.Workspace, path)) is { } file
                && (file.Exists || file.LinkTarget is not null))
            .Order(StringComparer.Ordinal).ToImmutableArray();
        return new CheckpointTaken(NewId(), commit, callId, CheckpointKinds.Before, uncaptured.IsEmpty ? null : uncaptured);
    }

    /// <summary>The ledger's record of what the call changed: the workspace after it, and the paths that differ from before.</summary>
    public async Task<CheckpointTaken> AfterAsync(string callId, CheckpointTaken before, ActionAnalysis analysis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(before);
        var commit = await Store.SnapshotAsync($"after {callId}", Include(analysis)).ConfigureAwait(false);
        var paths = await Store.ChangedAsync(before.Revision, commit).ConfigureAwait(false);
        return new CheckpointTaken(NewId(), commit, callId, CheckpointKinds.After, paths);
    }

    /// <summary>The visible write ledger, oldest first: calls still in the transcript that took a checkpoint before running.</summary>
    public static ImmutableArray<LedgerEntry> Ledger(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var calls = snapshot.Transcript.Where(entry => entry.Content is ToolCallIssued)
            .ToDictionary(entry => ((ToolCallIssued)entry.Content).CallId, entry => (entry.FromSequence, ((ToolCallIssued)entry.Content).ToolName),
                StringComparer.Ordinal);
        var after = snapshot.CheckpointEvents.Where(item => item.Checkpoint is { Kind: CheckpointKinds.After, CallId: not null })
            .GroupBy(item => item.Checkpoint.CallId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<LedgerEntry>();
        foreach (var before in snapshot.CheckpointEvents.Where(item => item.Checkpoint is { Kind: CheckpointKinds.Before, CallId: not null }))
        {
            var callId = before.Checkpoint.CallId!;
            if (!snapshot.IsVisible(before.Sequence) || !calls.TryGetValue(callId, out var call))
                continue;
            entries.Add(new LedgerEntry(callId, call.ToolName, call.FromSequence, before, after.GetValueOrDefault(callId)));
        }
        return [.. entries.OrderBy(entry => entry.CallSequence)];
    }

    /// <summary>Undo: back to just before the last user message still in the transcript. Null when there is none.</summary>
    /// <param name="changesFiles">Whether a call changes files (a tool with filesystem side effects, not a read-only command).</param>
    public static RestorePlan? PlanUndo(SessionSnapshot snapshot, Func<ToolCallIssued, bool>? changesFiles = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Transcript.LastOrDefault(entry => entry.Content is UserMessage) is not { } last)
            return null;
        return Plan(snapshot, last.FromSequence - 1, "undo", changesFiles);
    }

    /// <summary>
    /// Rollback: back to just before the call of the <paramref name="number"/>th most recent checkpoint (1 is the latest),
    /// reverting it and every later one. Null when there are fewer checkpoints.
    /// </summary>
    public static RestorePlan? PlanRollback(SessionSnapshot snapshot, int number, Func<ToolCallIssued, bool>? changesFiles = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ledger = Ledger(snapshot);
        if (number < 1 || number > ledger.Length)
            return null;
        return Plan(snapshot, ledger[^number].CallSequence - 1, "rollback", changesFiles);
    }

    private static RestorePlan Plan(SessionSnapshot snapshot, long position, string reason, Func<ToolCallIssued, bool>? changesFiles)
    {
        var entries = Ledger(snapshot).Where(entry => entry.CallSequence > position).ToImmutableArray();
        var covered = entries.Select(entry => entry.CallId).ToHashSet(StringComparer.Ordinal);
        var results = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolResult>()
            .ToDictionary(result => result.CallId, StringComparer.Ordinal);
        var unrecorded = changesFiles is null ? [] : snapshot.Transcript
            .Where(entry => entry.FromSequence > position && entry.Content is ToolCallIssued call && !covered.Contains(call.CallId)
                && Ran(results.GetValueOrDefault(call.CallId)) && changesFiles(call))
            .Select(entry => (ToolCallIssued)entry.Content).ToImmutableArray();
        return new RestorePlan(position, reason, entries, unrecorded);

        // A call refused before it ran (denied, invalid arguments) changed nothing.
        static bool Ran(ToolResult? result) => result is not { IsError: true } error
            || !(error.Result.ValueKind == System.Text.Json.JsonValueKind.Object && error.Result.TryGetProperty("error", out var kind)
                 && kind.ValueKind == System.Text.Json.JsonValueKind.String && kind.GetString() is "not run" or "invalid arguments");
    }

    /// <summary>
    /// Applies a plan: reverts its ledger entries in the workspace and appends <c>SessionRewound</c>. Nothing is written,
    /// and nothing appended, unless every path can be reverted without losing the user's content. Call it only while the
    /// session runs no turn (the session actor's exclusive work).
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(IEventLog log, string sessionId, RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Entries.IsEmpty || Refusal(log, sessionId, plan, cancellationToken) is not null)
            return Rewind(log, sessionId, plan, cancellationToken);
        if (Store.UnavailableReason is { } unavailable)
            return Refused(plan, $"checkpoints are unavailable ({unavailable}), so the files cannot be put back");
        if (plan.Entries.FirstOrDefault(entry => entry.After is null) is { } unfinished)
            return Refused(plan, $"the checkpoint for call '{unfinished.CallId}' has no record of what the call changed, so Corvid's " +
                "changes cannot be told from yours");
        foreach (var entry in plan.Entries)
            if (!await Store.ContainsAsync(entry.Before.Checkpoint.Revision).ConfigureAwait(false)
                || !await Store.ContainsAsync(entry.After!.Checkpoint.Revision).ConfigureAwait(false))
                return Refused(plan, $"the checkpoints for call '{entry.CallId}' are not in this workspace's store ({Store.Workspace})");

        var current = await Store.SnapshotAsync($"restore {plan.Reason}").ConfigureAwait(false);
        var now = await Store.ReadAsync(current).ConfigureAwait(false);
        var working = new Dictionary<string, StoredFile?>(StringComparer.Ordinal);
        StoredFile? Working(string path) => working.TryGetValue(path, out var file) ? file : now.TryGetValue(path, out var stored) ? stored : null;
        var trees = new Dictionary<string, ImmutableDictionary<string, StoredFile>>(StringComparer.Ordinal);
        async Task<ImmutableDictionary<string, StoredFile>> Tree(string revision) =>
            trees.TryGetValue(revision, out var tree) ? tree : trees[revision] = await Store.ReadAsync(revision).ConfigureAwait(false);

        var conflicts = new List<RestoreConflict>();
        var merged = new SortedSet<string>(StringComparer.Ordinal);
        // Newest first, as git revert would: each call's change is taken back from what is there now.
        foreach (var entry in plan.Entries.Reverse())
        {
            var before = await Tree(entry.Before.Checkpoint.Revision).ConfigureAwait(false);
            var after = await Tree(entry.After!.Checkpoint.Revision).ConfigureAwait(false);
            var uncaptured = entry.Before.Checkpoint.Paths ?? [];
            foreach (var path in entry.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (uncaptured.Contains(path))
                {
                    conflicts.Add(new(path, $"was not in the checkpoint before {entry.Tool} changed it (too large or protected), so it cannot be put back"));
                    continue;
                }
                StoredFile? pre = before.TryGetValue(path, out var p) ? p : null, post = after.TryGetValue(path, out var q) ? q : null;
                var present = Working(path);
                if (present == post)
                {
                    working[path] = pre;
                }
                else if (present == pre)
                {
                    // Already as it was (the user put it back): nothing to take back.
                }
                else if (present is { IsLink: false } mine && pre is { IsLink: false } old && post is { IsLink: false } corvid)
                {
                    var result = await Store.MergeAsync(await Store.ReadBlobAsync(mine.Blob).ConfigureAwait(false),
                        await Store.ReadBlobAsync(corvid.Blob).ConfigureAwait(false), await Store.ReadBlobAsync(old.Blob).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    if (result is null)
                    {
                        conflicts.Add(new(path, $"changed since {entry.Tool} wrote it, and the changes overlap or the file is binary"));
                        continue;
                    }
                    working[path] = new StoredFile(mine.Mode == corvid.Mode ? old.Mode : mine.Mode, await Store.WriteBlobAsync(result).ConfigureAwait(false));
                    merged.Add(path);
                }
                else
                {
                    conflicts.Add(new(path, present is null
                        ? $"deleted since {entry.Tool} wrote it"
                        : post is null ? $"recreated since {entry.Tool} deleted it" : pre is null ? $"changed since {entry.Tool} created it"
                        : $"changed since {entry.Tool} wrote it"));
                }
            }
        }

        var changes = working.Where(pair => pair.Value != (now.TryGetValue(pair.Key, out var stored) ? stored : null))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        foreach (var (path, file) in changes)
            if (Unsafe(path, now.ContainsKey(path), file) is { } reason)
                conflicts.Add(new(path, reason));
        if (conflicts.Count > 0)
            return new RestoreResult(RestoreStatus.Refused, plan, [], [], [.. conflicts.DistinctBy(conflict => conflict.Path)],
                "nothing was changed: putting these back would lose content that is not Corvid's");

        // Deletions first, deepest first, so a file can replace what was a directory of Corvid's files.
        foreach (var (path, _) in changes.Where(change => change.Value is null).OrderByDescending(change => change.Key.Length))
            File.Delete(Path.Combine(Store.Workspace, path));
        foreach (var (path, file) in changes.Where(change => change.Value is not null))
            await WriteAsync(path, file!.Value).ConfigureAwait(false);

        var restoreId = NewId();
        var restored = changes.Select(change => change.Key).ToImmutableArray();
        Append(log, sessionId, new CheckpointTaken(restoreId, current, Kind: CheckpointKinds.Restore, Paths: restored),
            new SessionRewound(plan.Position, plan.Reason, restored, restoreId));
        return new RestoreResult(RestoreStatus.Restored, plan, restored, [.. merged.Where(restored.Contains)], [], CheckpointId: restoreId);
    }

    /// <summary>
    /// A restore with no files to put back: only the transcript moves. Refused when the position would split a call from
    /// its result, or when calls after it changed files that no checkpoint recorded. Needs no store, so it works with
    /// checkpoints off.
    /// </summary>
    public static RestoreResult Rewind(IEventLog log, string sessionId, RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(plan);
        if (Refusal(log, sessionId, plan, cancellationToken) is { } refusal)
            return Refused(plan, refusal);
        if (!plan.Entries.IsEmpty)
            return Refused(plan, "checkpoints are off, so the files these calls changed cannot be put back");
        Append(log, sessionId, new SessionRewound(plan.Position, plan.Reason, []));
        return new RestoreResult(RestoreStatus.Restored, plan, [], [], []);
    }

    private static string? Refusal(IEventLog log, string sessionId, RestorePlan plan, CancellationToken cancellationToken)
    {
        if (SessionProjection.CheckRewind(log, sessionId, plan.Position, cancellationToken) is { } invalid)
            return invalid;
        if (!plan.Unrecorded.IsEmpty)
            return $"{string.Join(", ", plan.Unrecorded.Select(call => $"{call.ToolName} ({call.CallId})"))} changed files without a " +
                "checkpoint, so those changes cannot be put back";
        return null;
    }

    /// <summary>
    /// Why a path may not be written or deleted now: it is excluded, a component on the way is a link or a file, or the disk
    /// holds something the restore snapshot did not capture (a directory, an oversized or ignored file) that it would replace.
    /// </summary>
    private string? Unsafe(string path, bool captured, StoredFile? file)
    {
        if (Store.Excluded(path))
            return "a protected path";
        var current = Store.Workspace;
        var parts = path.Split('/');
        foreach (var part in parts[..^1])
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null)
                return "reached through a symbolic link";
            if (info.Exists)
                return "a file stands where its directory should be";
        }
        var full = Path.Combine(Store.Workspace, path);
        var target = new FileInfo(full);
        if (Directory.Exists(full) && target.LinkTarget is null)
            return file is null || Directory.EnumerateFileSystemEntries(full).Any() ? "a directory is there now" : null;
        if (!captured && (target.Exists || target.LinkTarget is not null))
            return "the file there now is not in the checkpoint (oversized or ignored), so it would be lost";
        return null;
    }

    private async Task WriteAsync(string path, StoredFile file)
    {
        var full = Path.Combine(Store.Workspace, path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        if (Directory.Exists(full) && new FileInfo(full).LinkTarget is null)
            Directory.Delete(full);
        var content = await Store.ReadBlobAsync(file.Blob).ConfigureAwait(false);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.corvid-{Guid.NewGuid():N}.tmp");
        try
        {
            if (file.IsLink)
            {
                File.CreateSymbolicLink(temp, System.Text.Encoding.UTF8.GetString(content));
            }
            else
            {
                await File.WriteAllBytesAsync(temp, content).ConfigureAwait(false);
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                if (file.IsExecutable)
                    mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, mode);
            }
            // A rename replaces a file or a link without following it.
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp) || new FileInfo(temp).LinkTarget is not null)
                File.Delete(temp);
        }
    }

    private static IEnumerable<string> Include(ActionAnalysis analysis) =>
        analysis.Targets.Where(target => target is { Kind: TargetKind.Path, Dynamic: false, Pattern: false, Location: PathLocation.Inside }
            && target.Access is TargetAccess.Write or TargetAccess.Delete).Select(target => target.Value);

    private static RestoreResult Refused(RestorePlan plan, string detail) => new(RestoreStatus.Refused, plan, [], [], [], detail);

    private static string NewId() => "cp-" + Guid.NewGuid().ToString("N")[..12];

    private static void Append(IEventLog log, string sessionId, params SessionEvent[] payloads)
    {
        var events = payloads.Select(EventData.Create).ToArray();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                log.Append(sessionId, log.GetHead(sessionId), events);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }

    /// <summary>A short description of an entry, for listings: when, the tool and what it changed.</summary>
    public static string Describe(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var paths = entry.After is null ? "not finished" : entry.Paths.IsEmpty ? "no changes" : string.Join(", ", entry.Paths.Take(4))
            + (entry.Paths.Length > 4 ? string.Create(CultureInfo.InvariantCulture, $" and {entry.Paths.Length - 4} more") : "");
        return $"{entry.Tool}: {paths}";
    }
}
