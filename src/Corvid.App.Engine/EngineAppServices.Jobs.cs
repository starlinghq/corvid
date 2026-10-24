using Corvid.App.Core.Composition;
using Corvid.App.Core.Jobs;
using Corvid.App.Engine.Jobs;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Jobs over the runtime's job store and scheduler (src/Corvid.App.Engine/Jobs/).</summary>
    public override IJobsService CreateJobsService(AppComposition app) => new EngineJobsService(Engine, app.Dispatcher, app.Time);
}
