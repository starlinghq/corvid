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
using Corvid.App.Core.Shell;
using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Composition;

/// <summary>
/// Where the composition gets each area's service: the demo's (<see cref="DemoAppServices"/>), the in-process engine's
/// (Corvid.App.Engine's <c>EngineAppServices</c>), or later a remote engine's in attached mode (G-1). One member per
/// service interface; the composition calls each once, on first use, from its <c>AppComposition.&lt;Area&gt;.cs</c> file,
/// and disposes what it returns (when it is <see cref="IDisposable"/>) with itself.
/// <para>
/// Each member takes the composition, so a factory reaches what it needs from it: <see cref="AppComposition.Dispatcher"/>
/// (the UI thread, for callbacks), <see cref="AppComposition.Time"/>, <see cref="AppComposition.Options"/> and the services
/// other areas already made (<see cref="AppComposition.SessionService"/>, <see cref="AppComposition.SessionHub"/>,
/// <see cref="AppComposition.BoardService"/>). An area whose engine service is not built yet returns one of
/// <see cref="Unavailable"/>'s services, whose requests fail with the reason the page shows; never demo data.
/// </para>
/// </summary>
public interface IAppServices
{
    ISessionService CreateSessionService(AppComposition app);

    IBoardService CreateBoardService(AppComposition app);

    IReviewService CreateReviewService(AppComposition app);

    IRewindService CreateRewindService(AppComposition app);

    IAuditService CreateAuditService(AppComposition app);

    IMemoryService CreateMemoryService(AppComposition app);

    /// <summary>What the Memory page's status bar shows besides the store; null leaves the status bar to the session.</summary>
    MemoryEngineStatus? CreateMemoryStatus(AppComposition app);

    ISkillsService CreateSkillsService(AppComposition app);

    ISafetyService CreateSafetyService(AppComposition app);

    IJobsService CreateJobsService(AppComposition app);

    IMcpService CreateMcpService(AppComposition app);

    IGatewayService CreateGatewayService(AppComposition app);

    ISecretsService CreateSecretsService(AppComposition app);

    IModelService CreateModelService(AppComposition app);

    IFirstRunService CreateFirstRunService(AppComposition app);

    ISettingsService CreateSettingsService(AppComposition app);

    IPromptHistory CreatePromptHistory(AppComposition app);

    /// <summary>
    /// G-5: search over what was said across sessions (the episodic index); null where there is none, and the palette's
    /// Ctrl+Shift+F then searches titles, previews and ids.
    /// </summary>
    ISessionSearch? CreateSessionSearch(AppComposition app) => null;

    /// <summary>The app's hosting of the gateway and the scheduler (U-8); a window that hosts nothing by default.</summary>
    IHostingService CreateHostingService(AppComposition app) => new NoHostingService("This window does not host the gateway or the scheduler.");
}
