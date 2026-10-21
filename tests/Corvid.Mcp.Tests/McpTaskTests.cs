using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Corvid.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Corvid.Mcp.Tests;

/// <summary>The MCP tasks extension (2026-07-28): long calls are started as tasks and polled, and cancelled when Corvid stops waiting.</summary>
public sealed class McpTaskTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALongCallRunsAsATaskAndIsPolledToItsResult()
    {
        await using var server = new TaskServer(TimeSpan.FromMilliseconds(300));
        var registry = new ToolRegistry();
        await using var clients = new McpClients([Definition()], server, registry);
        clients.Start();
        await clients.Started.WaitAsync(Token);
        Assert.True(Assert.Single(clients.Status()).Tasks);

        var result = (JsonObject)(await Invoke(registry, "slow__wait"))!;
        Assert.Equal("done: hi", result["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATaskThatRunsTooLongIsCancelledOnTheServer()
    {
        await using var server = new TaskServer(Timeout.InfiniteTimeSpan);
        var registry = new ToolRegistry();
        await using var clients = new McpClients([Definition() with { TaskTimeout = TimeSpan.FromMilliseconds(500) }], server, registry);
        clients.Start();
        await clients.Started.WaitAsync(Token);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => Invoke(registry, "slow__wait"));
        Assert.Contains("the task was cancelled", exception.Message);
        // The server saw the cancellation: the tool's own token fired.
        await server.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    private static McpServerDefinition Definition() => new()
    {
        Name = "slow",
        Command = "slow-server",
        Workspace = "/tmp/unused",
        StartTimeout = TimeSpan.FromSeconds(10),
        CallTimeout = TimeSpan.FromSeconds(10),
    };

    private static async Task<object?> Invoke(ToolRegistry registry, string name)
    {
        Assert.True(registry.TryGet(name, out var tool));
        return await tool.InvokeAsync(new AIFunctionArguments { ["text"] = "hi" }, Token);
    }

    /// <summary>A server with the SDK's tasks extension (an in-memory task store), hosted the way the SDK's DI builder does it.</summary>
    private sealed class TaskServer(TimeSpan delay) : IMcpTransports, IAsyncDisposable
    {
        private readonly List<IHost> _hosts = [];

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IClientTransport Create(McpServerDefinition server, Action<string> standardError)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            var tool = McpServerTool.Create(
                [Description("Waits, then answers.")] async (string text, CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Cancelled.TrySetResult();
                        throw;
                    }
                    return "done: " + text;
                },
                new McpServerToolCreateOptions { Name = "wait" });
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                .WithTools([tool])
                .WithTasks(new InMemoryMcpTaskStore());
            var host = builder.Build();
            host.StartAsync(Token).GetAwaiter().GetResult();
            _hosts.Add(host);
            return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var host in _hosts)
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }
    }
}
