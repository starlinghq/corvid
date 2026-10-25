using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;
using Corvid.App.Engine.Gateway;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Gateway over the pairing store, the delivery ledger and the configuration (src/Corvid.App.Engine/Gateway/). Hosting
    /// the gateway in this process is G-14's, still to come.
    /// </summary>
    public override IGatewayService CreateGatewayService(AppComposition app) => new EngineGatewayService(Engine, app.Dispatcher, app.Time);
}
