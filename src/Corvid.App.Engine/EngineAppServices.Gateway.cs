using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Gateway: refused until A3 ops builds it (src/Corvid.App.Engine/Gateway/).</summary>
    public override IGatewayService CreateGatewayService(AppComposition app) =>
        new Unavailable.Gateway(Unavailable.ComesWith("ops", "the gateway's adapters and pairings"));
}
