using Corvid.App.Core.Composition;
using Corvid.App.Core.Rewind;
using Corvid.App.Engine.Rewind;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Rewind over the runtime's write ledger and the kernel's restore (A3 history).</summary>
    public override IRewindService CreateRewindService(AppComposition app) => new EngineRewindService(Engine, app.Time, History(app));
}
