using Corvid.App.Core.Mcp;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IMcpService? _mcpService;
    private McpViewModel? _mcp;

    /// <summary>
    /// The MCP area's service: servers, pins and held definitions. Demo mode (the only mode until Corvid.App.Engine exists)
    /// holds the Board's notes__search; it follows the Board's service, so an accept in either place is one accept.
    /// </summary>
    public IMcpService McpService => _mcpService ??= new DemoMcpService(Time, BoardService);

    /// <summary>The MCP area: held tools with their diffs, servers, trust and pins.</summary>
    public McpViewModel Mcp => _mcp ??= new McpViewModel(McpService, Shell, StatusBar, Time);
}
