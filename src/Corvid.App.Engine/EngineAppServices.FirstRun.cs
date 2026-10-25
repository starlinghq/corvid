using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;
using Corvid.App.Engine.FirstRun;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>FirstRun: the doctor's checks, the workspace, the Hermes home and the setup's choices (src/Corvid.App.Engine/FirstRun/).</summary>
    public override IFirstRunService CreateFirstRunService(AppComposition app) => new EngineFirstRunService(Engine, app.Dispatcher, Home, Environment);
}
