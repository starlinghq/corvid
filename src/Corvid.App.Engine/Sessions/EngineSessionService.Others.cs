using Corvid.App.Core.Composition;
using Corvid.Host.Composition;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// G-12 in the list: sessions another process writes (a terminal's <c>corvid chat</c>, an editor over ACP, the gateway's
/// conversations) reach the rail and the Board without a restart. Every <see cref="ListInterval"/> the log's sessions and
/// heads are read against the last read (the list's, at first): a new session, or one that grew while no view here has it
/// open, is summarised again and raised as <see cref="SessionChanged"/>. An open view follows its session itself.
/// </summary>
public sealed partial class EngineSessionService
{
    /// <summary>How often the log is read for sessions other processes started or wrote to.</summary>
    public static readonly TimeSpan ListInterval = TimeSpan.FromSeconds(3);

    private ITimer? _listTimer;
    // The heads of the conversations as last read: by the list, then by each look. Replaced whole, never changed.
    private IReadOnlyDictionary<string, long>? _listedHeads;
    private int _lookingAtLog;

    private async Task WatchLogAsync()
    {
        CorvidRuntime runtime;
        try
        {
            runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        }
        catch (ServiceUnavailableException)
        {
            // No runtime yet (models missing): the engine's start watches again.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log("corvid app: sessions other processes start will not reach the list until a restart: " + exception.Message);
            return;
        }
        if (_disposed)
            return;
        var timer = _time.CreateTimer(_ => LookAtLog(runtime), null, ListInterval, ListInterval);
        if (Interlocked.CompareExchange(ref _listTimer, timer, null) is not null)
            timer.Dispose();
        else if (_disposed)
            StopWatchingLog();
    }

    private void StopWatchingLog() => Interlocked.Exchange(ref _listTimer, null)?.Dispose();

    /// <summary>The heads the list just read: the next look raises only what changed since.</summary>
    private void Listed(IReadOnlyDictionary<string, long> heads) => Volatile.Write(ref _listedHeads, heads);

    private void LookAtLog(CorvidRuntime runtime)
    {
        if (_disposed || Interlocked.Exchange(ref _lookingAtLog, 1) != 0)
            return;
        Track(Task.Run(() =>
        {
            try
            {
                var heads = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var id in runtime.Log.ListSessions())
                    if (IsConversation(id))
                        heads[id] = runtime.Log.GetHead(id);
                var previous = Interlocked.Exchange(ref _listedHeads, heads);
                // The first look with no list read before it has nothing to compare with: it is the baseline.
                if (previous is null || _disposed)
                    return;
                foreach (var (id, head) in heads)
                    if ((!previous.TryGetValue(id, out var before) || before != head) && !_channels.ContainsKey(id))
                        Refresh(runtime, id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A busy or locked log: the next look reads it again.
                Log("corvid app: could not read the session list: " + exception.Message);
            }
            finally
            {
                Volatile.Write(ref _lookingAtLog, 0);
            }
        }));
    }
}
