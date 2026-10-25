using Corvid.App.Core.Composition;
using Corvid.App.Core.Board;
using Corvid.App.Engine.Board;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Board over the window's MCP, Gateway, Jobs and Memory services (src/Corvid.App.Engine/Board/), so the tray and the
    /// pages read and answer the same items. The composition makes it at start, so those four are made with it.
    /// </summary>
    public override IBoardService CreateBoardService(AppComposition app) =>
        new EngineBoardService(Engine, app.McpService, app.GatewayService, app.JobsService, app.MemoryService, app.Dispatcher, app.Time);
}
