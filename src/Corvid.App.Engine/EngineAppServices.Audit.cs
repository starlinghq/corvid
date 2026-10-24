using Corvid.App.Core.Audit;
using Corvid.App.Core.Composition;
using Corvid.App.Engine.Audit;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Audit over the event log the configuration names (A3 history). It does not wait for the runtime, so it answers while
    /// the models load or when they are missing. The session status stays the hub's: the engine knows no more of it.
    /// </summary>
    public override IAuditService CreateAuditService(AppComposition app) => new EngineAuditService(Engine, app.Time);
}
