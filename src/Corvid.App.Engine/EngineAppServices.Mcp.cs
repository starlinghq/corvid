using Corvid.App.Core.Composition;
using Corvid.App.Core.Mcp;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Mcp: refused until A3 ops builds it (src/Corvid.App.Engine/Mcp/).</summary>
    public override IMcpService CreateMcpService(AppComposition app) =>
        new Unavailable.Mcp(Unavailable.ComesWith("ops", "the MCP servers and their pins"));
}
