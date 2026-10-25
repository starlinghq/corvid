using System.Collections.Concurrent;
using Corvid.App.Core.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// G-12 in the window: one driver per session. A session another process drives (a terminal's <c>corvid chat --session</c>,
/// an editor over ACP, the gateway) opens read-only with that process named, and the window follows it: every
/// <see cref="TailInterval"/> it reads what the log gained and tries the session's driver lock. When the other process lets
/// go (it exits), the view turns writable again, and the window's next message makes it this process's. A session this
/// process does not drive yet is watched the same way, so one another process takes while it is open turns read-only.
/// Requests that write are refused while another process drives (the runtime's own refusal, <see cref="SessionHeldException"/>,
/// names it); reading the log needs no lock.
/// </summary>
public sealed partial class EngineSessionService
{
    /// <summary>How often a session this process does not drive is read for what was appended and its lock tried.</summary>
    public static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<Channel, Tail> _tails = new();
    // Sessions listed as driven elsewhere, watched for their driver letting go, with the one timer that looks at them.
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);
    private ITimer? _heldTimer;
    private int _lookingAtHeld;

    /// <summary>What a tail reads past <paramref name="after"/>: the open calls are the other process's, still running.</summary>
    private static ReplayOptions TailOptions(long after) =>
        ReplayOptions.Full with { UnfinishedCalls = false, UnfinishedAsRunning = true, After = after };

    /// <summary>The read-only note for a session another process drives.</summary>
    internal static string DriverHolder(LockHolder? holder) =>
        $"driven by {SessionHeldException.HolderText(holder)}; this window follows it and can send once that process exits";

    /// <summary>
    /// Whether these updates end the other process's turn: since the latest prompt or call they read, the log holds the
    /// answer that ends a turn. A read with none (only events a replay does not show, such as decisions) ends nothing.
    /// </summary>
    internal static bool Answered(IReadOnlyList<SessionStreamUpdate> updates)
    {
        for (var index = updates.Count - 1; index >= 0; index--)
        {
            switch (updates[index])
            {
                case AnswerChunk:
                    return true;
                case PromptSent or ToolCallStarted or ToolCallChanged:
                    return false;
            }
        }
        return false;
    }

    /// <summary>Starts following a loaded session this process does not drive; once per channel.</summary>
    private void StartTail(CorvidRuntime runtime, ToolPresentation tools, Channel channel, long head)
    {
        if (_disposed || IsJobRun(channel.SessionId))
            return;
        var tail = new Tail(head);
        if (!_tails.TryAdd(channel, tail))
            return;
        tail.Timer = _time.CreateTimer(_ => Tick(runtime, tools, channel, tail), null, TailInterval, TailInterval);
        if (_disposed)
            StopTail(channel);
    }

    /// <summary>One look at the session, unless the last one is still reading.</summary>
    private void Tick(CorvidRuntime runtime, ToolPresentation tools, Channel channel, Tail tail)
    {
        if (_disposed || Interlocked.Exchange(ref tail.Busy, 1) != 0)
            return;
        Track(Task.Run(async () =>
        {
            try
            {
                await FollowAsync(runtime, tools, channel, tail).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref tail.Busy, 0);
            }
        }));
    }

    /// <summary>
    /// Who drives the session now, then what the log gained since the last look, published as the live updates of a turn
    /// would be; then, when the driver changed, the view's read-only state. The lock is tried before the log is read, so
    /// whatever a process wrote before letting go is in the read.
    /// </summary>
    private async Task FollowAsync(CorvidRuntime runtime, ToolPresentation tools, Channel channel, Tail tail)
    {
        var id = channel.SessionId;
        lock (channel.Gate)
        {
            if (channel.Closed || _disposed)
            {
                StopTail(channel);
                return;
            }
        }
        var driver = runtime.Driver(id);
        if (driver.State == LockState.HeldHere)
        {
            // This process took it (the window's own message): its turns report live from here on.
            StopTail(channel);
            return;
        }
        var changed = false;
        if (runtime.Log.GetHead(id) > tail.Head)
        {
            var history = await SessionReplay.ReadAsync(runtime, id, tools, TailOptions(tail.Head)).ConfigureAwait(false);
            tail.Head = history.Head;
            var answered = Answered(history.Updates);
            Publish(channel, translator =>
            {
                // The window started a turn since the read: the session is becoming this process's, and its reporter
                // reports from the head it read, so this read is dropped rather than shown twice.
                if (channel.Running)
                    return [];
                var output = new List<SessionUpdate>();
                foreach (var update in history.Updates)
                    output.AddRange(translator.Translate(update));
                if (answered)
                    output.AddRange(translator.EndReplay());
                return output;
            });
            changed = true;
        }
        var elsewhere = driver.State == LockState.HeldElsewhere;
        var holder = elsewhere ? DriverHolder(driver.Holder) : null;
        var flipped = false;
        Publish(channel, translator =>
        {
            if (channel.Running || IsJobRun(id) || (channel.ReadOnly == elsewhere && channel.Holder == holder))
                return [];
            flipped = true;
            channel.ReadOnly = elsewhere;
            channel.Holder = holder;
            // Let go: whatever turn of its the log left open is closed as the log has it.
            var output = new List<SessionUpdate>(elsewhere ? [] : translator.EndReplay());
            output.Add(new DriverChanged(elsewhere) { Holder = holder, At = _time.GetUtcNow() });
            return output;
        });
        if (changed || flipped)
            Refresh(runtime, id);
    }

    /// <summary>
    /// A request found the session driven elsewhere (the runtime refused to open it): the view turns read-only at once,
    /// naming the holder, rather than at the next look. No turn of the window's can be running: it could not open the session.
    /// </summary>
    private void Held(string sessionId, SessionHeldException exception)
    {
        if (!_channels.TryGetValue(sessionId, out var channel))
            return;
        var holder = DriverHolder(exception.Holder);
        var flipped = false;
        Publish(channel, _ =>
        {
            if (channel.ReadOnly && channel.Holder == holder)
                return [];
            flipped = true;
            channel.ReadOnly = true;
            channel.Holder = holder;
            return [new DriverChanged(true) { Holder = holder, At = _time.GetUtcNow() }];
        });
        if (flipped && _engine.Runtime.IsCompletedSuccessfully)
            Refresh(_engine.Runtime.Result, sessionId);
    }

    private void StopTail(Channel channel)
    {
        if (_tails.TryRemove(channel, out var tail))
            tail.Timer?.Dispose();
    }

    private void StopTails()
    {
        foreach (var channel in _tails.Keys)
            StopTail(channel);
        Interlocked.Exchange(ref _heldTimer, null)?.Dispose();
    }

    /// <summary>
    /// A session listed as driven elsewhere, open in no view here (the Board's card, the rail): every
    /// <see cref="TailInterval"/> its lock is tried, and its summary raised again once that process lets go, so the card
    /// stops saying "held" without a reload. An open view follows it through its own tail.
    /// </summary>
    private void WatchHeld(CorvidRuntime runtime, string sessionId)
    {
        if (_disposed || !_held.TryAdd(sessionId, 0) || Volatile.Read(ref _heldTimer) is not null)
            return;
        var timer = _time.CreateTimer(_ => LookAtHeld(runtime), null, TailInterval, TailInterval);
        if (Interlocked.CompareExchange(ref _heldTimer, timer, null) is not null || _disposed)
            timer.Dispose();
    }

    private void LookAtHeld(CorvidRuntime runtime)
    {
        if (_disposed || _held.IsEmpty || Interlocked.Exchange(ref _lookingAtHeld, 1) != 0)
            return;
        Track(Task.Run(() =>
        {
            try
            {
                foreach (var id in _held.Keys)
                    if (runtime.Driver(id).State != LockState.HeldElsewhere && _held.TryRemove(id, out _))
                        Refresh(runtime, id);
            }
            finally
            {
                Volatile.Write(ref _lookingAtHeld, 0);
            }
        }));
    }

    /// <summary>How far a followed session has been read, and its timer.</summary>
    private sealed class Tail(long head)
    {
        public long Head = head;

        public int Busy;

        public ITimer? Timer;
    }
}
