using Corvid.App.Core.Mcp;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IMcpService? _mcpService;
    private McpViewModel? _mcp;

    /// <summary>
    /// The MCP area's service: servers, pins and held definitions. Demo mode (the engine's comes from <see cref="Services"/> instead)
    /// holds the Board's notes__search; accepting it here answers the Board's item, whose Review… only leads here.
    /// </summary>
    public IMcpService McpService => _mcpService ??= Services.CreateMcpService(this);

    /// <summary>The MCP area: held tools with their diffs, servers, trust and pins.</summary>
    public McpViewModel Mcp => _mcp ??= new McpViewModel(McpService, Shell, StatusBar, Time);
}
