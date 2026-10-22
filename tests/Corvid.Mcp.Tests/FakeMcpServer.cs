using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using Corvid.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Shared with Corvid.Host.Tests (linked), so the turn-loop tests use the same server.
namespace Corvid.Mcp.Tests;

/// <summary>
/// An MCP server in the test process, reached over in-memory pipes: each connection the client makes starts a fresh
/// server on the SDK's stream transport, so a test can end the server (<see cref="EndAll"/>) and watch the client reconnect.
/// </summary>
internal sealed class FakeMcpServer : IMcpTransports, IAsyncDisposable
{
    private readonly ConcurrentBag<(McpServer Server, CancellationTokenSource Stop, Pipe ToServer, Pipe ToClient, Task Run)> _running = [];

    public List<Tool> Tools { get; } = [];

    /// <summary>Answers tools/call; by default echoes the tool name and arguments as text.</summary>
    public Func<CallToolRequestParams, CancellationToken, ValueTask<CallToolResult>> Call { get; set; } = (request, _) =>
        ValueTask.FromResult(Text($"{request.Name} {JsonSerializer.Serialize(request.Arguments)}"));

    /// <summary>The server never reads its input: the client's handshake waits forever.</summary>
    public bool Silent { get; set; }

    /// <summary>Thrown by the transport factory instead of connecting (a launcher refusal, a missing Vault).</summary>
    public Exception? Refuse { get; set; }

    public int Connections;

    public ConcurrentQueue<CallToolRequestParams> Calls { get; } = new();

    public static CallToolResult Text(string text, bool error = false) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = error ? true : null };

    public FakeMcpServer Add(string name, string description = "A test tool.", string schema = """{"type":"object","properties":{"text":{"type":"string"}}}""",
        ToolAnnotations? annotations = null)
    {
        using var document = JsonDocument.Parse(schema);
        Tools.Add(new Tool { Name = name, Description = description, InputSchema = document.RootElement.Clone(), Annotations = annotations });
        return this;
    }

    public IClientTransport Create(McpServerDefinition server, Action<string> standardError)
    {
        Interlocked.Increment(ref Connections);
        if (Refuse is { } refusal)
            throw refusal;
        var toServer = new Pipe();
        var toClient = new Pipe();
        if (!Silent)
        {
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "fake", Version = "1.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. Tools] }),
                    CallToolHandler = (request, cancellationToken) =>
                    {
                        Calls.Enqueue(request.Params!);
                        return Call(request.Params!, cancellationToken);
                    },
                },
            };
            var mcp = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "fake"), options);
            var stop = new CancellationTokenSource();
            _running.Add((mcp, stop, toServer, toClient, mcp.RunAsync(stop.Token)));
        }
        standardError("fake server started");
        return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
    }

    /// <summary>Sends <c>notifications/tools/list_changed</c> from every running server (C-033).</summary>
    public async Task NotifyToolsChangedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var running in _running)
            await running.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken);
    }

    /// <summary>Ends every running server, as if its process exited: the client sees its connection close.</summary>
    public async Task EndAll()
    {
        while (_running.TryTake(out var running))
        {
            await running.Stop.CancelAsync();
            await running.ToClient.Writer.CompleteAsync();
            await running.ToServer.Reader.CompleteAsync();
            await running.Run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await running.Server.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await EndAll();
}
