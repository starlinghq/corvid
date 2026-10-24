using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Engine.History;

/// <summary>
/// Tells Rewind and Review that a session's ledger may have moved: a turn the window ran ended (the session service's
/// <see cref="ISessionService.SessionChanged"/>), or one of them restored or reverted files. Raised on the UI thread with
/// the session's id. One per window; disposing it stops listening to the session service.
/// </summary>
internal sealed class HistorySignals : IDisposable
{
    private readonly ISessionService _sessions;
    private readonly IDispatcher _dispatcher;
    private volatile bool _disposed;

    public HistorySignals(ISessionService sessions, IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _sessions = sessions;
        _dispatcher = dispatcher;
        _sessions.SessionChanged += OnSessionChanged;
    }

    /// <summary>A session's ledger may have changed; on the UI thread.</summary>
    public event Action<string>? Changed;

    /// <summary>Says so from any thread: the listeners hear it on the UI thread.</summary>
    public void Raise(string sessionId) => _dispatcher.Post(() =>
    {
        if (!_disposed)
            Changed?.Invoke(sessionId);
    });

    public void Dispose()
    {
        _disposed = true;
        _sessions.SessionChanged -= OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, SessionSummary summary) => Raise(summary.Id);
}
