using System.ComponentModel;
using Corvid.App.Core.Board;
using Corvid.App.Core.Safety;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private ISafetyService? _safetyService;
    private SafetyViewModel? _safety;
    private HubSafetySession? _safetySession;

    /// <summary>
    /// The Safety area's service: remembered rules, detections, the hardline list and taint. Demo mode (the only mode until
    /// Corvid.App.Engine exists) reads the rules the sessions' approval answers wrote from the sessions themselves.
    /// </summary>
    public ISafetyService SafetyService => _safetyService ??= new DemoSafetyService(Time, SessionService, new SessionServiceWatcher(SessionService));

    /// <summary>The Safety area: modes, rules and detections. Created on first use; disposed with the composition.</summary>
    public SafetyViewModel Safety => _safety ??= new SafetyViewModel(SafetyService, _safetySession ??= new HubSafetySession(SessionHub), Shell, StatusBar, Time);

    /// <summary>Stops the Safety area's feeds: the view model's subscriptions, the hub adapter and the session watches.</summary>
    private void DisposeSafety()
    {
        _safety?.Dispose();
        _safetySession?.Dispose();
        (_safetyService as IDisposable)?.Dispose();
    }

    /// <summary>The Safety page's mode control drives the session the rail has selected, through the hub.</summary>
    private sealed class HubSafetySession : ISafetySession, IDisposable
    {
        private readonly SessionHub _hub;
        private SessionViewModel? _current;

        public HubSafetySession(SessionHub hub)
        {
            _hub = hub;
            _hub.CurrentChanged += OnCurrentChanged;
            Follow(_hub.Current);
        }

        public event EventHandler? Changed;

        public string? SessionId => _current?.SessionId;

        public string Title => _current?.Title ?? "";

        public ApprovalMode? Mode => _current?.Mode;

        public bool ReadOnly => _current?.IsReadOnly ?? false;

        public IReadOnlyList<StatusTag> Tags => _current?.StatusTags ?? [];

        public void RequestMode(ApprovalMode mode)
        {
            if (_current is { IsReadOnly: false } session && session.SetModeCommand.CanExecute(mode))
                session.SetModeCommand.Execute(mode);
        }

        public void Dispose()
        {
            _hub.CurrentChanged -= OnCurrentChanged;
            Follow(null);
        }

        private void OnCurrentChanged(object? sender, EventArgs e)
        {
            Follow(_hub.Current);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Follow(SessionViewModel? session)
        {
            if (_current is not null)
                _current.PropertyChanged -= OnSessionChanged;
            _current = session;
            if (_current is not null)
                _current.PropertyChanged += OnSessionChanged;
        }

        private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null or "" or nameof(SessionViewModel.Mode) or nameof(SessionViewModel.StatusTags)
                or nameof(SessionViewModel.Title) or nameof(SessionViewModel.IsReadOnly))
                Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
