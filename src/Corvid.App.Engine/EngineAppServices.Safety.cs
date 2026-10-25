using Corvid.App.Core.Composition;
using Corvid.App.Core.Safety;
using Corvid.App.Engine.Safety;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Safety over the approval pipeline's rules (K-7), the detection pack and the log (src/Corvid.App.Engine/Safety/).</summary>
    public override ISafetyService CreateSafetyService(AppComposition app) => new EngineSafetyService(Engine, app.Dispatcher, app.Time);
}
