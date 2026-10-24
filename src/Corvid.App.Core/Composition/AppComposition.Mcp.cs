using Corvid.App.Core.Mcp;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private McpViewModel? _mcp;

    /// <summary>The MCP area: MCP servers, pins and held tools.</summary>
    public McpViewModel Mcp => _mcp ??= new McpViewModel();
}
