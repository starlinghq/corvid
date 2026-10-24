using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    /// <summary>The Main mockup's "draft saved · about 40 tokens": a draft put aside with Ctrl+S, kept for the session.</summary>
    internal const string MainSceneStash =
        "check whether the retry count in ci.yml hides other flaky tests, then list each test that needed a retry in the last ten CI runs on main, with how often it did";

    /// <summary>The Approval mockup's draft, typed while the card opened.</summary>
    internal const string ApprovalSceneDraft = "and keep the launcher untouched, the test is the only thing to change";

    private SessionsViewModel? _sessions;

    /// <summary>
    /// The engine's sessions as the views see them. Demo mode (the only mode until Corvid.App.Engine exists) serves the
    /// mockups' scenario, opening at the moment <c>--scene</c> names.
    /// </summary>
    public ISessionService SessionService { get; private set; } = null!;

    /// <summary>The window's open sessions and the feeds from them into the rail, the attention count and the status bar.</summary>
    public SessionHub SessionHub { get; private set; } = null!;

    /// <summary>The Sessions area: the selected session's conversation and the dock's tabs.</summary>
    public SessionsViewModel Sessions => _sessions ??= CreateSessions();

    private void StartSessions()
    {
        SessionService = new DemoSessionService(Dispatcher, Time, Options.Scene);
        SessionHub = new SessionHub(SessionService, Dispatcher, Time, Shell, StatusBar);
        // The demo answers at once, so the rail is full and the session open when this returns.
        _ = SessionHub.StartAsync(Options.Session, DemoShell.CurrentSessionId);
        if (Options.Demo)
            SetScene(SessionHub.Get(DemoSessionService.MainSessionId));
    }

    /// <summary>What the mockup shows beyond the session's own state: the composer's draft, the open rule preview.</summary>
    private void SetScene(SessionViewModel session)
    {
        if (Options.Scene == DemoScene.Approval)
        {
            session.Draft = ApprovalSceneDraft;
            if (session.CurrentApproval?.Options.FirstOrDefault(option => option.OptionKind == ApprovalOptionKind.Always) is { } always)
                always.Choose.Execute(null);
        }
        else
        {
            session.StashDraft(MainSceneStash);
        }
    }

    private SessionsViewModel CreateSessions()
    {
        var sessions = new SessionsViewModel(Shell, SessionHub);
        // Approval.png shows the Decisions tab beside the card.
        if (Options.Demo && Options.Scene == DemoScene.Approval)
            sessions.SelectPanel(SessionPanels.Decisions);
        return sessions;
    }
}
