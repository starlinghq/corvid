using Corvid.App.Core.Composition;
using Corvid.App.Core.Mcp;
using Corvid.App.Engine.Mcp;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>MCP over the runtime's MCP clients, the pin store and the config writer (src/Corvid.App.Engine/Mcp/).</summary>
    public override IMcpService CreateMcpService(AppComposition app) => new EngineMcpService(Engine, app.Dispatcher, app.Time);
}
