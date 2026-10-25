using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;
using Corvid.App.Engine.Gateway;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Gateway over the pairing store, the delivery ledger and the configuration, and the gateway this engine hosts (G-14,
    /// src/Corvid.App.Engine/Gateway/).
    /// </summary>
    public override IGatewayService CreateGatewayService(AppComposition app) => new EngineGatewayService(Engine, app.Dispatcher, app.Time);
}
