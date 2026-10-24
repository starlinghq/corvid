using Corvid.App.Core.Audit;
using Corvid.App.Core.Board;
using Corvid.App.Core.FirstRun;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mcp;
using Corvid.App.Core.Memory;
using Corvid.App.Core.Models;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Review;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Safety;
using Corvid.App.Core.Secrets;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Settings;
using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Composition;

/// <summary>
/// Demo mode's services (<c>--demo</c>, the default): the mockups' scenario on the composition's frozen clock, in memory.
/// Review and Rewind work over the demo sessions themselves, so this keeps the <see cref="DemoSessionService"/> it made
/// for them rather than the composition casting its session service.
/// </summary>
public sealed class DemoAppServices : IAppServices
{
    private DemoSessionService? _sessions;

    public ISessionService CreateSessionService(AppComposition app) => _sessions = new DemoSessionService(app.Dispatcher, app.Time, app.Options.Scene);

    public IBoardService CreateBoardService(AppComposition app) => new DemoBoardService(app.Time);

    public IReviewService CreateReviewService(AppComposition app) => new DemoReviewService(app.SessionHub, DemoSessions(), app.Time);

    public IRewindService CreateRewindService(AppComposition app) => new DemoRewindService(app.SessionHub, DemoSessions(), app.Time);

    public IAuditService CreateAuditService(AppComposition app) => new DemoAuditService(app.Time);

    public IMemoryService CreateMemoryService(AppComposition app) => new DemoMemoryService(app.Time);

    public MemoryEngineStatus? CreateMemoryStatus(AppComposition app) => MemoryEngineStatus.Demo;

    public ISkillsService CreateSkillsService(AppComposition app) => new DemoSkillsService(app.Time);

    public ISafetyService CreateSafetyService(AppComposition app) =>
        new DemoSafetyService(app.Time, app.SessionService, new SessionServiceWatcher(app.SessionService));

    public IJobsService CreateJobsService(AppComposition app) => new DemoJobsService(app.Time);

    public IMcpService CreateMcpService(AppComposition app) => new DemoMcpService(app.Time, app.BoardService);

    public IGatewayService CreateGatewayService(AppComposition app) => new DemoGatewayService(app.Time, app.BoardService);

    public ISecretsService CreateSecretsService(AppComposition app) => new DemoSecretsService(app.Time);

    public IModelService CreateModelService(AppComposition app) => new DemoModelService(app.Dispatcher, app.Time,
        app.Options.Page == Area.FirstRun ? DemoModelScene.FirstRun : DemoModelScene.Main);

    public IFirstRunService CreateFirstRunService(AppComposition app) => new DemoFirstRunService(freshMachine: app.Options.Page == Area.FirstRun);

    public ISettingsService CreateSettingsService(AppComposition app) => new DemoSettingsService();

    public IPromptHistory CreatePromptHistory(AppComposition app) => new DemoPromptHistory(app.Time);

    private DemoSessionService DemoSessions() =>
        _sessions ?? throw new InvalidOperationException("The demo's Review and Rewind work over the demo sessions, which the composition makes first.");
}
