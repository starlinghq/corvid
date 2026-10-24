using Corvid.App.Core.Composition;
using Corvid.App.Core.Audit;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Audit: refused until A3 history builds it (src/Corvid.App.Engine/Audit/).</summary>
    public override IAuditService CreateAuditService(AppComposition app) =>
        new Unavailable.Audit(Unavailable.ComesWith("history", "Audit over the event log"));
}
