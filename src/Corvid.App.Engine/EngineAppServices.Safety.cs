using Corvid.App.Core.Composition;
using Corvid.App.Core.Safety;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Safety: refused until A3 knowledge builds it (src/Corvid.App.Engine/Safety/).</summary>
    public override ISafetyService CreateSafetyService(AppComposition app) =>
        new Unavailable.Safety(Unavailable.ComesWith("knowledge", "the remembered rules and detections"));
}
