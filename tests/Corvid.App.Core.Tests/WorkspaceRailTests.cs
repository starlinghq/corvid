using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// A6 (G-4): several workspaces in one window. With a service that keeps a workspace per session, the rail groups the
/// sessions by workspace, a new session starts in the workspace of the one in view, and "Open a workspace…" starts one in a
/// typed folder, a listed workspace, or a new git worktree asked for on the session in view. The demo keeps Pinned and Recent.
/// </summary>
public sealed class WorkspaceRailTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The demo's sessions with absolute workspaces and a workspace per session; new sessions are recorded, not made.</summary>
    private sealed class ChoosingService(DemoSessionService inner, Func<string, string>? workspaceOf = null) : ISessionService
    {
        public List<NewSessionRequest?> Created { get; } = [];

        public Exception? Refuse { get; set; }

        public bool CanChooseWorkspace => true;

        public bool CanCreate => true;

        public Task<string> CreateAsync(CancellationToken cancellationToken = default) => CreateAsync(null!, cancellationToken);

        public Task<string> CreateAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Refuse is { } refused ? Task.FromException<string>(refused) : Task.FromResult("app-" + Created.Count);
        }

        private SessionSummary Absolute(SessionSummary summary) =>
            summary with { Workspace = workspaceOf?.Invoke(summary.Id) ?? "/home/me/src/" + summary.Workspace };

        public event EventHandler<SessionSummary>? SessionChanged;

        public void Raise(SessionSummary summary) => SessionChanged?.Invoke(this, summary);

        public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            [.. (await inner.ListAsync(cancellationToken)).Select(Absolute)];

        public Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default) =>
            inner.OpenAsync(sessionId, onUpdate, cancellationToken);
        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(sessionId, text, cancellationToken);
        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.QueueAsync(sessionId, text, cancellationToken);
        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.RemoveQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => inner.MoveQueuedAsync(sessionId, queuedId, index, cancellationToken);
        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.TakeBackQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SteerAsync(sessionId, text, cancellationToken);
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InterruptAsync(sessionId, cancellationToken);
        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) => inner.AnswerApprovalAsync(sessionId, approvalId, answer, cancellationToken);
        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) => inner.AnswerQuestionAsync(sessionId, questionId, answer, cancellationToken);
        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) => inner.RunCommandAsync(sessionId, line, cancellationToken);
        public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default) => inner.SetApprovalModeAsync(sessionId, mode, cancellationToken);
        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => inner.SetGoalAsync(sessionId, goal, cancellationToken);
        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => inner.CompactAsync(sessionId, focus, cancellationToken);
        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => inner.ForkAsync(sessionId, throughPosition, cancellationToken);
        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => inner.RenameAsync(sessionId, title, cancellationToken);
    }

    private sealed record Rig(SessionHub Hub, ShellViewModel Shell, ChoosingService Service) : IDisposable
    {
        public void Dispose() => Hub.Dispose();
    }

    private static async Task<Rig> StartAsync(Func<string, string>? workspaceOf = null, string? select = DemoSessionService.ReleaseNotesSessionId)
    {
        var time = ManualTimeProvider.Demo();
        var service = new ChoosingService(new DemoSessionService(new InlineDispatcher(), time), workspaceOf);
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        var hub = new SessionHub(service, new InlineDispatcher(), time, shell, bar);
        await hub.StartAsync(select, null, Ct);
        return new Rig(hub, shell, service);
    }

    [Fact]
    public async Task TheRailGroupsSessionsByWorkspaceWithTheWorktreesBranch()
    {
        using var rig = await StartAsync();

        var titles = rig.Shell.SessionGroups.Select(group => group.Title).ToList();
        Assert.Equal("Pinned", titles[0]);
        Assert.DoesNotContain("Recent", titles);
        Assert.Contains("corvid-vault · ntfy", titles);
        Assert.Contains(titles, title => title.StartsWith("corvid", StringComparison.Ordinal) && !title.StartsWith("corvid-vault", StringComparison.Ordinal));
        var vault = rig.Shell.SessionGroups.Single(group => group.Title == "corvid-vault · ntfy");
        Assert.Equal([DemoSessionService.VaultSessionId], vault.Sessions.Select(session => session.Id));
    }

    [Fact]
    public async Task TheDemoKeepsPinnedAndRecent()
    {
        var time = ManualTimeProvider.Demo();
        var service = new DemoSessionService(new InlineDispatcher(), time);
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        using var hub = new SessionHub(service, new InlineDispatcher(), time, shell, bar);
        await hub.StartAsync(null, null, Ct);

        Assert.False(hub.CanChooseWorkspace);
        Assert.Equal(["Pinned", "Recent"], shell.SessionGroups.Select(group => group.Title));
    }

    [Fact]
    public async Task TwoWorkspacesOfOneNameAreTitledByPathAndHiddenCharactersAreCleaned()
    {
        using var rig = await StartAsync(id => id switch
        {
            DemoSessionService.VaultSessionId => "/home/me/b/corvid‮",
            _ => "/home/me/a/corvid‮",
        });

        var titles = rig.Shell.SessionGroups.Select(group => group.Title).Skip(1).ToList();
        Assert.Equal(2, titles.Count);
        Assert.All(titles, title => Assert.DoesNotContain("‮", title, StringComparison.Ordinal));
        Assert.Contains(titles, title => title.StartsWith("/home/me/a/corvid", StringComparison.Ordinal));
        Assert.Contains(titles, title => title.StartsWith("/home/me/b/corvid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewSessionStartsInTheWorkspaceOfTheSessionInView()
    {
        using var rig = await StartAsync(select: DemoSessionService.VaultSessionId);

        rig.Hub.StartNew();

        var request = Assert.Single(rig.Service.Created);
        Assert.Equal("/home/me/src/corvid-vault", request!.Workspace);
        Assert.False(request.Worktree);
    }

    [Fact]
    public async Task OpenAWorkspaceStartsOneInATypedOrListedFolderAndSaysWhyItWasRefused()
    {
        using var rig = await StartAsync();
        var chooser = new WorkspaceChooserViewModel(rig.Hub, rig.Shell);
        Assert.True(chooser.OpenCommand.CanExecute(null));
        Assert.False(chooser.StartCommand.CanExecute(null));

        chooser.OpenCommand.Execute(null);
        Assert.True(chooser.IsOpen);
        Assert.Equal(["/home/me/src/corvid", "/home/me/src/corvid-vault"], chooser.Known.Select(choice => choice.Description).Order(StringComparer.Ordinal));

        chooser.Path = " /srv/project ";
        await chooser.StartCommand.ExecuteAsync();
        Assert.Equal("/srv/project", rig.Service.Created.Single()!.Workspace);
        Assert.False(chooser.IsOpen);

        chooser.OpenCommand.Execute(null);
        rig.Service.Refuse = new InvalidOperationException("The workspace /nowhere‮ is not a directory that exists.");
        chooser.Path = "/nowhere";
        await chooser.StartCommand.ExecuteAsync();
        Assert.True(chooser.IsOpen);
        Assert.True(chooser.HasError);
        Assert.Contains("not a directory that exists", chooser.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("‮", chooser.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewWorktreeIsAskedForOnTheSessionInViewWhichSessionsShows()
    {
        using var rig = await StartAsync();
        rig.Shell.Navigate(Area.Board);
        var chooser = new WorkspaceChooserViewModel(rig.Hub, rig.Shell);
        chooser.OpenCommand.Execute(null);
        Assert.Contains("corvid", chooser.WorktreeText, StringComparison.Ordinal);

        chooser.WorktreeName = "feature";
        await chooser.WorktreeCommand.ExecuteAsync();

        var request = Assert.Single(rig.Service.Created);
        Assert.True(request!.Worktree);
        Assert.Equal((DemoSessionService.ReleaseNotesSessionId, "feature"), (request.AskIn, request.WorktreeName));
        Assert.Equal(Area.Sessions, rig.Shell.CurrentArea);
    }

    [Fact]
    public async Task TheBoardOpensTheChooserOnTheEngineOnly()
    {
        using var rig = await StartAsync();
        var time = ManualTimeProvider.Demo();
        using var board = new BoardViewModel(rig.Shell, rig.Hub, new DemoBoardService(time), new SessionServiceWatcher(rig.Service), time, new InlineDispatcher());
        Assert.True(board.OpenWorkspaceCommand.CanExecute(null));
        board.OpenWorkspaceCommand.Execute(null);
        Assert.True(board.Chooser.IsOpen);
    }
}
