using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Corvid.Abstractions.Events;
using FileChange = Corvid.App.Core.Sessions.FileChange;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Events;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using AppDiff = Corvid.App.Core.Diffs.FileDiff;
using KernelEntry = Corvid.Kernel.Checkpoints.LedgerEntry;

namespace Corvid.App.Engine.History;

/// <summary>
/// One session's write ledger as Rewind and Review read it, from one pass over the log: the ledger exactly as
/// <c>/rollback</c> lists it (<see cref="CheckpointService.Ledger"/>), the turn each position belongs to (counted over the
/// expanded transcript, as the Sessions page numbers turns), each event's time, which calls finished, and the restores.
/// </summary>
internal sealed class SessionLedger
{
    private readonly ImmutableArray<long> _prompts;

    private SessionLedger(string sessionId, long head, SessionSnapshot snapshot, ImmutableArray<long> prompts,
        IReadOnlyDictionary<long, StoredEvent> events)
    {
        SessionId = sessionId;
        Head = head;
        Snapshot = snapshot;
        _prompts = prompts;
        Events = events;
        Entries = CheckpointService.Ledger(snapshot);
        Finished = [.. snapshot.Transcript.Select(entry => entry.Content).OfType<ToolResult>().Select(result => result.CallId)];
        Calls = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolCallIssued>()
            .GroupBy(call => call.CallId, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    public string SessionId { get; }

    /// <summary>The log head the ledger was read through.</summary>
    public long Head { get; }

    /// <summary>The session as <c>/rollback</c> projects it (compactions not expanded).</summary>
    public SessionSnapshot Snapshot { get; }

    /// <summary>The visible ledger, oldest first.</summary>
    public ImmutableArray<KernelEntry> Entries { get; }

    /// <summary>Every event read, by its sequence.</summary>
    public IReadOnlyDictionary<long, StoredEvent> Events { get; }

    /// <summary>Calls the transcript has a result for.</summary>
    public ImmutableHashSet<string> Finished { get; }

    public ImmutableDictionary<string, ToolCallIssued> Calls { get; }

    /// <summary>The session's turns as the transcript stands.</summary>
    public int Turn => _prompts.Length;

    /// <summary>The turn a position is in: the number of the transcript's user messages at or before it.</summary>
    public int TurnAt(long sequence)
    {
        var index = _prompts.BinarySearch(sequence);
        return index >= 0 ? index + 1 : ~index;
    }

    public DateTimeOffset TimeOf(long sequence) => Events.TryGetValue(sequence, out var stored) ? stored.RecordedAt : default;

    /// <summary>Every restore checkpoint the log has (a restore's own checkpoint is inside the range it hid), oldest first.</summary>
    public IEnumerable<RecordedCheckpoint> Restores =>
        Snapshot.CheckpointEvents.Where(item => item.Checkpoint.Kind == CheckpointKinds.Restore);

    /// <summary>Every rewind of the transcript, oldest first, with its sequence.</summary>
    public IEnumerable<(long Sequence, SessionRewound Rewound)> Rewinds =>
        Events.Values.Where(item => item.Payload is SessionRewound).OrderBy(item => item.Sequence)
            .Select(item => (item.Sequence, (SessionRewound)item.Payload));

    /// <summary>Reads the session's ledger; synchronous SQLite, so call it off the UI thread.</summary>
    /// <exception cref="KeyNotFoundException">The log has no such session.</exception>
    public static SessionLedger Load(CorvidRuntime runtime, string sessionId, CancellationToken cancellationToken)
    {
        var head = runtime.Log.GetHead(sessionId, cancellationToken);
        if (head == 0 && !runtime.TryGetSession(sessionId, out _))
            throw new KeyNotFoundException($"There is no session '{sessionId}' in Corvid's log.");
        var events = SessionProjection.ReadRange(runtime.Log, sessionId, 0, head, cancellationToken).ToDictionary(item => item.Sequence);
        var snapshot = SessionProjection.Load(runtime.Log, sessionId, head, cancellationToken: cancellationToken);
        var expanded = SessionProjection.Load(runtime.Log, sessionId, head, expandCompactions: true, cancellationToken);
        var prompts = expanded.Transcript.Where(entry => entry.Content is UserMessage).Select(entry => entry.FromSequence).Order().ToImmutableArray();
        return new SessionLedger(sessionId, head, snapshot, prompts, events);
    }
}

/// <summary>A file's text at a checkpoint or on disk: absent, text, or present without text to show and why.</summary>
internal sealed record FileText(bool Exists, string? Text, string? Note = null)
{
    public static FileText Absent { get; } = new(false, null);
}

/// <summary>
/// Reads files for Rewind and Review: from the checkpoint store at a revision, or from the workspace now, through the file
/// tools' own <see cref="Workspace"/> rules first, so a path the tools may not open (a protected location, a credential
/// file, outside the workspace) is never read or shown. A binary, oversized or linked file is reported, not decoded.
/// Counts of what each finished call changed are kept by its after checkpoint, which never changes.
/// </summary>
/// <param name="runtime">The runtime whose log and checkpoints are read.</param>
/// <param name="scope">A6 (G-4): the workspace the session works in; the runtime's configured one when null.</param>
internal sealed class LedgerTexts(CorvidRuntime runtime, WorkspaceServices? scope = null)
{
    private readonly WorkspaceServices _scope = scope ?? runtime.DefaultWorkspace;
    private readonly Workspace _workspace = Workspace.For((scope ?? runtime.DefaultWorkspace).Environment);
    private readonly ConcurrentDictionary<string, ImmutableArray<FileChange>> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LedgerTexts> _others = new(StringComparer.Ordinal);

    /// <summary>The workspace the paths are relative to.</summary>
    public string Root => _workspace.Root;

    public ShadowStore? Store => _scope.Checkpoints is { Store: { IsAvailable: true } store } ? store : null;

    /// <summary>
    /// A6 (G-4): the texts of a session's workspace (this one when it works here): its files now and its checkpoints, never
    /// another directory's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session's workspace cannot be used now (a removed worktree), said why.</exception>
    public LedgerTexts For(string sessionId)
    {
        WorkspaceServices workspace;
        try
        {
            workspace = runtime.WorkspaceServicesOf(sessionId);
        }
        catch (WorkspaceUnavailableException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        return workspace.Root == _scope.Root ? this : _others.GetOrAdd(workspace.Root, _ => new LedgerTexts(runtime, workspace));
    }

    /// <summary>Whether the file tools may read the workspace-relative path.</summary>
    public bool Readable(string relative)
    {
        try
        {
            _workspace.Resolve(relative);
            return true;
        }
        catch (ToolArgumentException)
        {
            return false;
        }
    }

    /// <summary>The file as a checkpoint holds it.</summary>
    public async Task<FileText> AtAsync(string revision, string relative)
    {
        if (Store is not { } store)
            return new FileText(true, null, "checkpoints are unavailable");
        var files = await store.ReadAsync(revision, [relative]).ConfigureAwait(false);
        return files.TryGetValue(relative, out var file) ? await BlobAsync(file).ConfigureAwait(false) : FileText.Absent;
    }

    /// <summary>A stored file's text; null for an absent file.</summary>
    public async Task<FileText> BlobAsync(StoredFile? file)
    {
        if (file is not { } stored)
            return FileText.Absent;
        if (stored.IsLink)
            return new FileText(true, null, "a symbolic link");
        if (Store is not { } store)
            return new FileText(true, null, "checkpoints are unavailable");
        return Decode(await store.ReadBlobAsync(stored.Blob).ConfigureAwait(false));
    }

    /// <summary>The file in the workspace now.</summary>
    public FileText Now(string relative)
    {
        string path;
        try
        {
            path = _workspace.Resolve(relative);
        }
        catch (ToolArgumentException)
        {
            return new FileText(true, null, "not readable by Corvid");
        }
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            return new FileText(true, null, "a symbolic link");
        if (!info.Exists)
            return FileText.Absent;
        if (info.Length > ToolPresentation.MaxDiffBytes)
            return new FileText(true, null, TooLarge(info.Length));
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FileText(true, null, "could not be read: " + exception.Message);
        }
    }

    /// <summary>When the person (or anything else) last wrote the file in the workspace; null when it is not there.</summary>
    public DateTimeOffset? Modified(string relative)
    {
        try
        {
            var info = new FileInfo(_workspace.Resolve(relative));
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (ToolArgumentException)
        {
            return null;
        }
    }

    /// <summary>What a finished call changed in each path, with line counts (zero for a file with no text to count).</summary>
    public async Task<ImmutableArray<FileChange>> ChangesAsync(KernelEntry entry)
    {
        if (entry.After is not { } after)
            return [];
        if (_counts.TryGetValue(after.Checkpoint.CheckpointId, out var known))
            return known;
        var changes = ImmutableArray.CreateBuilder<FileChange>();
        foreach (var path in entry.Paths)
        {
            if (!Readable(path))
            {
                changes.Add(new FileChange(path, 0, 0) { Readable = false });
                continue;
            }
            var (added, removed) = Count(await AtAsync(entry.Before.Checkpoint.Revision, path).ConfigureAwait(false),
                await AtAsync(after.Checkpoint.Revision, path).ConfigureAwait(false), path);
            changes.Add(new FileChange(path, added, removed));
        }
        var result = changes.ToImmutable();
        // Unavailable checkpoints give no counts this time; only real ones are kept.
        if (Store is not null)
            _counts[after.Checkpoint.CheckpointId] = result;
        return result;
    }

    /// <summary>Lines added and removed from one text to the other; zero when either side has no text to show.</summary>
    public static (int Added, int Removed) Count(FileText from, FileText to, string path)
    {
        if (from.Exists && from.Text is null || to.Exists && to.Text is null)
            return (0, 0);
        var diff = AppDiff.Compute(path, from.Exists ? from.Text : null, to.Text ?? "");
        return (diff.Added, diff.Removed);
    }

    private static FileText Decode(byte[] bytes)
    {
        if (bytes.Length > ToolPresentation.MaxDiffBytes)
            return new FileText(true, null, TooLarge(bytes.Length));
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0)
            return new FileText(true, null, "binary file");
        try
        {
            return new FileText(true, new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return new FileText(true, null, "binary file");
        }
    }

    private static string TooLarge(long bytes) => string.Create(CultureInfo.InvariantCulture,
        $"too large to show ({(bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024.0:0} KB")})");
}
