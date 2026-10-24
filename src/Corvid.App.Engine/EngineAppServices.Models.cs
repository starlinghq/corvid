using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Models: refused until A3 setup builds it (src/Corvid.App.Engine/Models/).</summary>
    public override IModelService CreateModelService(AppComposition app) =>
        new Unavailable.Models(Unavailable.ComesWith("setup", "the model slots, catalogue and downloads"));
}
