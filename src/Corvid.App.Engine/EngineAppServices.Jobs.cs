using Corvid.App.Core.Composition;
using Corvid.App.Core.Jobs;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Jobs: refused until A3 ops builds it (src/Corvid.App.Engine/Jobs/).</summary>
    public override IJobsService CreateJobsService(AppComposition app) =>
        new Unavailable.Jobs(Unavailable.ComesWith("ops", "the scheduler and its jobs"));
}
