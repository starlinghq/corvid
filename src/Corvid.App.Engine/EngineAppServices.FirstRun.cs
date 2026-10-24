using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>FirstRun: refused until A3 setup builds it (src/Corvid.App.Engine/FirstRun/).</summary>
    public override IFirstRunService CreateFirstRunService(AppComposition app) =>
        new Unavailable.FirstRun(Unavailable.ComesWith("setup", "the doctor's checks and the setup"));
}
