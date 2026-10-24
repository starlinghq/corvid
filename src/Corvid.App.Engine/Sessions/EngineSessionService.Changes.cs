using System.Collections.Immutable;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.History;
using Corvid.Host.Composition;
using KernelEntry = Corvid.Kernel.Checkpoints.LedgerEntry;

namespace Corvid.App.Engine.Sessions;

public sealed partial class EngineSessionService
{
    /// <summary>The most files one change list holds; a session that changed more lists its latest (as Review does).</summary>
    public const int MaxChangedFiles = 200;

    private LedgerTexts? _texts;

    /// <summary>
    /// The dock's Changes and the Review badge (<see cref="ChangesUpdate"/>) from the write ledger: the paths the session's
    /// finished calls changed, this turn's and the whole session's, each counted from the checkpoint before the first call
    /// that changed it to the checkpoint after the last. A path the file tools may not open is listed without counts.
    /// Synchronous SQLite and the checkpoint store: call it off the UI thread.
    /// </summary>
    private async Task<ChangesUpdate> ChangesAsync(CorvidRuntime runtime, string sessionId, CancellationToken cancellationToken)
    {
        var texts = _texts ??= new LedgerTexts(runtime);
        var ledger = SessionLedger.Load(runtime, sessionId, cancellationToken);
        var finished = ledger.Entries.Where(entry => entry.After is not null).ToList();
        var session = await CountAsync(texts, finished, cancellationToken).ConfigureAwait(false);
        var turn = await CountAsync(texts, finished.Where(entry => ledger.TurnAt(entry.CallSequence) == ledger.Turn).ToList(), cancellationToken)
            .ConfigureAwait(false);
        return new ChangesUpdate { Turn = turn, Session = session, SinceTurn = 1, At = _time.GetUtcNow() };
    }

    private static async Task<ImmutableArray<FileChange>> CountAsync(LedgerTexts texts, List<KernelEntry> calls, CancellationToken cancellationToken)
    {
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
        if (paths.Count > MaxChangedFiles)
            paths = paths[^MaxChangedFiles..];
        var changes = ImmutableArray.CreateBuilder<FileChange>(paths.Count);
        foreach (var (path, first, last) in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!texts.Readable(path))
            {
                changes.Add(new FileChange(path, 0, 0) { Readable = false });
                continue;
            }
            var (added, removed) = LedgerTexts.Count(await texts.AtAsync(first.Before.Checkpoint.Revision, path).ConfigureAwait(false),
                await texts.AtAsync(last.After!.Checkpoint.Revision, path).ConfigureAwait(false), path);
            changes.Add(new FileChange(path, added, removed));
        }
        return changes.ToImmutable();
    }

    /// <summary>Reads the change lists again and publishes them to the session's views; a failure is logged, not lost.</summary>
    private void PublishChanges(CorvidRuntime runtime, Channel channel) => Track(Task.Run(async () =>
    {
        if (_disposed)
            return;
        var changes = await ChangesAsync(runtime, channel.SessionId, CancellationToken.None).ConfigureAwait(false);
        Publish(channel, _ => [changes]);
    }));
}
