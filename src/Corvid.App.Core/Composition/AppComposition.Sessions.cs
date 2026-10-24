using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SessionsViewModel? _sessions;

    /// <summary>The Sessions area: the selected session's conversation.</summary>
    public SessionsViewModel Sessions => _sessions ??= CreateSessions();

    private SessionsViewModel CreateSessions()
    {
        var sessions = new SessionsViewModel(Shell);
        if (Options.Demo)
        {
            // The Main mockup's dock: three changed files, two open tasks.
            sessions.SetPanelBadge("Changes", 3);
            sessions.SetPanelBadge("Tasks", 2);
        }

        return sessions;
    }
}
