using Corvid.App.Core.Composition;
using Corvid.App.Core.Rewind;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Rewind: refused until A3 history builds it (src/Corvid.App.Engine/Rewind/).</summary>
    public override IRewindService CreateRewindService(AppComposition app) =>
        new Unavailable.Rewind(Unavailable.ComesWith("history", "the checkpoint timeline and its restores"));
}
