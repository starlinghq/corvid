using System.Collections.Concurrent;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Flocks;
using Corvid.Host.Commands;
using Corvid.Host.Sessions;
using Corvid.Kernel.Sessions;

namespace Corvid.Host.Composition;

/// <summary>
/// C-042: a message from a flock for one member this process drives, as the relay hands it to the member's surface: the
/// text, who it is from, and how the turn takes it.
/// </summary>
/// <param name="Origin">
/// <see cref="EnvelopeOrigin.User"/> for the user's own message to the flock, <see cref="EnvelopeOrigin.Peer"/> for an
/// untainted member's, <see cref="EnvelopeOrigin.External"/> for a tainted member's (outside content, plan §10.3).
/// </param>
/// <param name="Peer">The member it is from, or null for the user's.</param>
public sealed record FlockDelivery(string FlockId, string FlockName, long Sequence, string Text, EnvelopeOrigin Origin, PeerSender? Peer)
{
    /// <summary>One line for a surface's notice: "flock auth · api → you: …".</summary>
    public string Notice => $"flock {FlockName} · {Peer?.Name ?? "you"}: {Text}";
}

/// <summary>
/// C-042: a surface that runs a member session's turns itself (<c>corvid chat</c>, the app's window) takes the flock's
/// messages for it here instead of the relay posting them. Return true once the surface has the message: it then posts it
/// with <see cref="CorvidRuntime.PostFlockAsync"/>, which records it delivered, or gives it back with
/// <see cref="CorvidRuntime.ReleaseFlock"/>; until then the relay holds it. False leaves it for the next pass.
/// </summary>
public delegate bool FlockInbox(FlockDelivery delivery);

public sealed partial class CorvidRuntime
{
    /// <summary>How often the relay reads the flocks for messages to deliver.</summary>
    internal static TimeSpan FlockPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    private FlockStore? _flocks;
    private readonly ConcurrentDictionary<string, FlockInbox> _flockInboxes = new(StringComparer.Ordinal);
    // A member's turn the relay posted and that has not ended: the next message waits for it.
    private readonly ConcurrentDictionary<string, Task> _flockTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _flockHeads = new(StringComparer.Ordinal);
    // The user's messages steered into a running turn and not yet taken, by flock, message and member.
    private readonly ConcurrentDictionary<(string Flock, long Sequence, string Session), bool> _flockSteers = new();
    // Messages a surface's inbox took and has neither posted (PostFlockAsync) nor given back (ReleaseFlock): not offered
    // again meanwhile. Held in memory only: a process that exits before posting one leaves it undelivered in the log.
    private readonly ConcurrentDictionary<(string Flock, long Sequence, string Session), bool> _flockOffered = new();
    private int _relayStarted;

    /// <summary>C-042: flocks in this runtime's event log.</summary>
    public FlockStore Flocks => LazyInitializer.EnsureInitialized(ref _flocks, () => new FlockStore(Log));

    /// <summary>Turns members may start on each other before a flock pauses for the user.</summary>
    public int MaxPeerTurns { get; internal set; } = FlockStore.DefaultMaxPeerTurns;

    /// <summary>
    /// C-042: a flock this process's relay sees appeared, moved (a message, a member, a delivery, a pause) or closed: for a
    /// surface that shows flocks. Raised on the relay's thread.
    /// </summary>
    public event Action<FlockState>? FlockChanged;

    /// <summary>
    /// C-042: <paramref name="inbox"/> takes the flock's messages for the session while attached. Disposing it gives back
    /// every message it took and did not post, and the relay posts them itself from then on.
    /// </summary>
    public IDisposable AttachFlockInbox(string sessionId, FlockInbox inbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(inbox);
        _flockInboxes[sessionId] = inbox;
        StartFlockRelay();
        return new Detach(() =>
        {
            _flockInboxes.TryRemove(new KeyValuePair<string, FlockInbox>(sessionId, inbox));
            foreach (var key in _flockOffered.Keys.Where(key => key.Session == sessionId))
                _flockOffered.TryRemove(key, out _);
        });
    }

    // The hard filter withholds flock_send from ranking for every session; AlwaysOfferedTools adds it back for members.
    private static Func<Corvid.Tools.ToolDescriptor, bool> WithoutFlockSend(Func<Corvid.Tools.ToolDescriptor, bool> filter) =>
        tool => tool.Name != FlockSendTool.ToolName && filter(tool);

    /// <summary>The tools a session's turns are always offered: <c>flock_send</c> while it is in a flock (C-042).</summary>
    private IReadOnlyCollection<string> AlwaysOfferedTools(string sessionId) =>
        Flocks.FlockOf(sessionId) is null ? [] : [FlockSendTool.ToolName];

    /// <summary>
    /// C-042: joins a session to a flock under <paramref name="name"/> (<see cref="MemberName"/> when null). A child session
    /// never joins: its parent speaks for it.
    /// </summary>
    /// <exception cref="FlockException">Refused, with why.</exception>
    public FlockState JoinFlock(string flockId, string sessionId, string? name = null)
    {
        var flock = Flocks.Join(flockId, sessionId, name ?? MemberName(Log, sessionId));
        StartFlockRelay();
        return flock;
    }

    /// <summary>C-042: the name a session joins a flock under when none is given: its title, else its id's last part.</summary>
    public static string MemberName(IEventLog log, string sessionId) =>
        SessionCatalogue.Describe(log, sessionId)?.Titled?.Title is { Length: > 0 } title ? title : sessionId[(sessionId.LastIndexOf('-') + 1)..];

    /// <summary>Starts the relay once; a session opened, an inbox attached or a flock joined starts it.</summary>
    internal void StartFlockRelay()
    {
        if (Interlocked.Exchange(ref _relayStarted, 1) == 1)
            return;
        _ = Task.Run(() => RelayFlocksAsync(_lifetime.Token));
    }

    private async Task RelayFlocksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FlockPollInterval);
        try
        {
            do
            {
                try
                {
                    await RelayOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // A store that cannot be read now (a busy database) is read again on the next pass.
                    Announce("flocks: " + TerminalText.Visible(exception.Message));
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass of the relay: every open flock's pending messages for the members this process drives.</summary>
    internal async Task RelayOnceAsync(CancellationToken cancellationToken = default)
    {
        var open = Flocks.List();
        foreach (var listed in open)
        {
            var flock = listed;
            if (!_flockHeads.TryGetValue(flock.Id, out var seen) || seen != flock.Head)
                FlockChanged?.Invoke(flock);
            _flockHeads[flock.Id] = flock.Head;
            foreach (var member in flock.Members)
            {
                if (!TryGetSession(member.SessionId, out var session) || session.Child is not null
                    || Driver(member.SessionId).State != LockState.HeldHere || !TakesFlockTurns(session))
                    continue;
                foreach (var entry in flock.Pending(member.SessionId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (handed, latest) = await DeliverAsync(flock, session, entry).ConfigureAwait(false);
                    flock = latest;
                    // Messages to a member go in order: one not handed over now holds the ones after it.
                    if (!handed)
                        break;
                }
            }
            if (flock.Head != listed.Head)
            {
                _flockHeads[flock.Id] = flock.Head;
                FlockChanged?.Invoke(flock);
            }
        }
        // A flock closed since the last pass is said once more, closed, so a surface lets go of it.
        foreach (var id in _flockHeads.Keys.Where(id => !open.Any(flock => flock.Id == id)).ToList())
        {
            _flockHeads.TryRemove(id, out _);
            if (Flocks.Get(id) is { } gone)
                FlockChanged?.Invoke(gone);
        }
    }

    // Surfaces that show a turn they did not start: the app and the chat (which attaches an inbox). An editor's ACP session,
    // an HTTP client's and a gateway chat answer only what they asked, so a member opened there gets its messages once it is
    // opened in one of these.
    private bool TakesFlockTurns(CorvidSession session) =>
        session.Surface is "app" or "cli" || _flockInboxes.ContainsKey(session.Id);

    // Hands one message to a member, true when it is handed (or with the surface already). The user's message steers a
    // running turn; anything else, and a steer that would pass an earlier message still on its way, is a turn of its own,
    // through the surface's inbox when one is attached. A delivery is recorded once the message is posted, never on the
    // inbox's word alone. The loop guard holds members' messages that would start a turn once they have started
    // MaxPeerTurns since the user last took part.
    private async Task<(bool Handed, FlockState Flock)> DeliverAsync(FlockState flock, CorvidSession session, FlockEntry entry)
    {
        var message = entry.Message;
        var fromUser = message.From == FlockSessions.User;
        if (flock.Paused && !fromUser)
            return (false, flock);
        var key = (flock.Id, entry.Sequence, session.Id);
        // The surface has it and runs its messages in order: the next may go to it too.
        if (_flockOffered.ContainsKey(key))
            return (true, flock);
        if (_flockSteers.ContainsKey(key))
            return (false, flock);
        var peer = fromUser ? null : new PeerSender(flock.SenderName(message), flock.Name);
        var origin = fromUser ? EnvelopeOrigin.User : message.Tainted ? EnvelopeOrigin.External : EnvelopeOrigin.Peer;
        var text = !fromUser ? message.Text
            : message.To.IsDefaultOrEmpty ? $"(To everyone in the flock {flock.Name}) {message.Text}"
            : $"(To you in the flock {flock.Name}) {message.Text}";
        var earlier = _flockTurns.TryGetValue(session.Id, out var running) && !running.IsCompleted
            || _flockOffered.Keys.Any(other => other.Session == session.Id);
        // A steer is read as the user's own words in the running turn, so only the user's message is one: a member's, even an
        // untainted one, keeps its colleague's quoting in a turn of its own. Taken at the turn's next step, which may be
        // minutes off: the relay goes on meanwhile, holding this member's later messages, and records it once taken.
        if (fromUser && session.Actor.TurnRunning && !earlier)
        {
            _flockSteers[key] = true;
            _ = session.SteerAsync(text).ContinueWith(taken =>
            {
                try
                {
                    if (taken.IsCompletedSuccessfully && taken.Result)
                        Flocks.Delivered(flock.Id, entry.Sequence, session.Id, started: false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Announce("flocks: " + TerminalText.Visible(exception.Message));
                }
                finally
                {
                    _flockSteers.TryRemove(key, out _);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return (false, flock);
        }
        if (_flockTurns.TryGetValue(session.Id, out running) && !running.IsCompleted)
            return (false, flock);
        if (!fromUser && flock.PeerTurns >= MaxPeerTurns)
        {
            var paused = Flocks.Pause(flock.Id, MaxPeerTurns);
            if (paused.Paused && !flock.Paused)
                Announce($"The flock {TerminalText.Visible(flock.Name)} is paused: its members started {paused.PeerTurns} turns on each other "
                    + "without you. /flock resume lets them go on; a message to the flock does too.");
            return (false, paused);
        }
        var delivery = new FlockDelivery(flock.Id, flock.Name, entry.Sequence, text, origin, peer);
        if (_flockInboxes.TryGetValue(session.Id, out var inbox))
        {
            _flockOffered[key] = true;
            if (inbox(delivery))
                return (true, flock);
            _flockOffered.TryRemove(key, out _);
            return (false, flock);
        }
        var turn = await PostAsync(session, text, origin, null, _lifetime.Token, peer: peer).ConfigureAwait(false);
        _flockTurns[session.Id] = turn;
        return (true, Flocks.Delivered(flock.Id, entry.Sequence, session.Id, started: true));
    }

    /// <summary>
    /// C-042: posts a flock's message an inbox took into the member session (as
    /// <see cref="CorvidSession.PostAsync(string, EnvelopeOrigin, CancellationToken)"/> does, with its sender), then records it
    /// delivered. A post that fails leaves it undelivered; give it back with <see cref="ReleaseFlock"/>.
    /// </summary>
    public async Task<Task<Delivery>> PostFlockAsync(CorvidSession session, FlockDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(delivery);
        var turn = await PostAsync(session, delivery.Text, delivery.Origin, null, cancellationToken, peer: delivery.Peer).ConfigureAwait(false);
        try
        {
            Flocks.Delivered(delivery.FlockId, delivery.Sequence, session.Id, started: true);
        }
        catch (FlockException)
        {
            // Closed meanwhile: the turn runs, and there is nothing left to record it in.
        }
        _flockOffered.TryRemove((delivery.FlockId, delivery.Sequence, session.Id), out _);
        return turn;
    }

    /// <summary>C-042: an inbox gives back a message it took and will not post; the relay offers it again.</summary>
    public void ReleaseFlock(string sessionId, FlockDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        _flockOffered.TryRemove((delivery.FlockId, delivery.Sequence, sessionId), out _);
    }

    private sealed class Detach(Action detach) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                detach();
        }
    }
}
