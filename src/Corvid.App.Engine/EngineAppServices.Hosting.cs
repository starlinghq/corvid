using Corvid.App.Core.Composition;
using Corvid.App.Core.Shell;
using Corvid.App.Engine.Hosting;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>The app's hosting of the gateway and the scheduler, off by default (U-8, src/Corvid.App.Engine/Hosting/).</summary>
    public override IHostingService CreateHostingService(AppComposition app) => new EngineHostingService(Engine, app.Dispatcher);
}
