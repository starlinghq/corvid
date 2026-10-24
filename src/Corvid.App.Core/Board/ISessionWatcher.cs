using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Board;

/// <summary>
/// A session's folded state, live, read-only: what a card needs beyond its summary (tool calls in the run, staged memory,
/// the open question's id and options) and what a peek shows. The Board reads sessions only through this and
/// <see cref="SessionHub"/>, never by opening a <see cref="SessionViewModel"/>, so peeking attaches nothing.
/// </summary>
public interface ISessionWatcher
{
    /// <summary>
    /// Calls <paramref name="onChange"/> with the session's state now, then after every update on the UI thread, until the
    /// returned handle is disposed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No session has that id.</exception>
    Task<IDisposable> WatchAsync(string sessionId, Action<SessionView> onChange, CancellationToken cancellationToken = default);
}

/// <summary>
/// Watches through <see cref="ISessionService.OpenAsync"/>: the history replayed through <see cref="SessionFold"/>, then
/// each live update folded in, the same code path a loaded and a live session share (docs/plan/ui.md §8).
/// </summary>
public sealed class SessionServiceWatcher(ISessionService service) : ISessionWatcher
{
    private readonly ISessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task<IDisposable> WatchAsync(string sessionId, Action<SessionView> onChange, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onChange);
        var watch = new Watch(sessionId, onChange);
        watch.Subscription = await _service.OpenAsync(sessionId, watch.Apply, cancellationToken).ConfigureAwait(true);
        watch.Start();
        return watch;
    }

    private sealed class Watch(string sessionId, Action<SessionView> onChange) : IDisposable
    {
        private readonly List<SessionUpdate> _early = [];
        private SessionView _state = SessionFold.Start(sessionId);
        private bool _started;
        private bool _disposed;

        public ISessionSubscription? Subscription { get; set; }

        public void Start()
        {
            // Updates that arrive while the subscription is being made come after its history.
            _state = SessionFold.Replay(sessionId, Subscription!.History);
            foreach (var update in _early)
                _state = SessionFold.Apply(_state, update);
            _early.Clear();
            _started = true;
            onChange(_state);
        }

        public void Apply(SessionUpdate update)
        {
            if (_disposed)
                return;
            if (!_started)
            {
                _early.Add(update);
                return;
            }

            _state = SessionFold.Apply(_state, update);
            onChange(_state);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Subscription?.Dispose();
        }
    }
}
