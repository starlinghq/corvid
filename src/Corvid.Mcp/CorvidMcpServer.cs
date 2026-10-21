using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Corvid.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Corvid.Mcp;

/// <summary>What <c>corvid mcp serve</c> offers another agent.</summary>
public sealed record McpServeOptions
{
    public string ServerName { get; init; } = "corvid";

    /// <summary>Told to the client at the handshake, for its model.</summary>
    public string? Instructions { get; init; }

    /// <summary>The session the calls are attributed to, for tools that record one.</summary>
    public string SessionId { get; init; } = "mcp-serve";

    /// <summary>Characters of one result sent back; the rest is cut and the result says so.</summary>
    public int MaxResultChars { get; init; } = 32_000;
}

/// <summary>
/// Corvid as an MCP server (plan §12: MCP server mode exposes memory and skills to other agents). It serves only read-only
/// Corvid tools: a tool with any side effect is refused at construction, so nothing another agent asks can write memory,
/// skills or files; writes stay on Corvid's own gated paths (the turn loop's approvals, review of staged memory). Calls run
/// as unattended and tainted (plan §10.3: a request from another agent is outside content), so a tool that would ask
/// someone refuses instead.
/// </summary>
public sealed class CorvidMcpServer
{
    private static readonly string Version =
        typeof(CorvidMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    private readonly ImmutableDictionary<string, ToolFunction> _tools;
    private readonly McpServeOptions _options;
    private long _calls;

    public CorvidMcpServer(IEnumerable<ToolFunction> tools, McpServeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var list = tools.ToList();
        var writer = list.FirstOrDefault(tool => !tool.Descriptor.Risk.ReadOnly);
        if (writer is not null)
            throw new ArgumentException($"'{writer.Name}' has side effects; the MCP server serves read-only tools only.", nameof(tools));
        _tools = list.ToImmutableDictionary(tool => tool.Name, StringComparer.Ordinal);
        _options = options ?? new McpServeOptions();
    }

    public IReadOnlyCollection<string> ToolNames => _tools.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>The server over <paramref name="transport"/>; run it with <see cref="McpServer.RunAsync"/>.</summary>
    public McpServer Create(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = _options.ServerName, Version = Version },
            ServerInstructions = _options.Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. _tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(Describe)] }),
                CallToolHandler = (request, cancellationToken) => CallAsync(request.Params, cancellationToken),
            },
        };
        return McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
    }

    /// <summary>Serves on this process's standard input and output until the client leaves or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunStdioAsync(CancellationToken cancellationToken = default)
    {
        await using var transport = new StdioServerTransport(_options.ServerName);
        await using var server = Create(transport);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Tool Describe(ToolFunction tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = tool.JsonSchema,
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = true,
            DestructiveHint = false,
            OpenWorldHint = tool.Descriptor.Risk.Network,
        },
    };

    private async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? request, CancellationToken cancellationToken)
    {
        if (request is null || !_tools.TryGetValue(request.Name, out var tool))
            throw new McpProtocolException($"Unknown tool '{request?.Name}'.", McpErrorCode.InvalidParams);
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in request.Arguments ?? new Dictionary<string, JsonElement>())
            arguments[name] = value.Clone();
        var context = new ToolCallContext(_options.SessionId, "mcp-" + Interlocked.Increment(ref _calls))
        {
            Tainted = true,
            Interactive = false,
        };
        try
        {
            var value = await tool.InvokeAsync(context.AttachTo(arguments), cancellationToken).ConfigureAwait(false);
            var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions.GetTypeInfo(value?.GetType() ?? typeof(object)));
            var text = element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
            if (text.Length > _options.MaxResultChars)
                return new CallToolResult { Content = [new TextContentBlock { Text = text[.._options.MaxResultChars] + "\n[cut]" }] };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }],
                StructuredContent = element.ValueKind == JsonValueKind.Object ? element : null,
            };
        }
        catch (Exception exception) when (exception is ToolArgumentException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = exception.Message }] };
        }
    }
}
