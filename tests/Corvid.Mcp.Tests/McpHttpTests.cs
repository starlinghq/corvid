using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Models.Remote.Tests;
using Corvid.Tools;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;
using Microsoft.Extensions.AI;

namespace Corvid.Mcp.Tests;

/// <summary>
/// Streamable HTTP servers (plan §10.5): a server with a token is reached only through Vault, with a placeholder in its
/// Authorization header, never the token; a keyless server is reached directly without any credential header.
/// </summary>
public sealed class McpHttpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AServerWithASecretIsReachedOnlyThroughVaultWithAPlaceholder()
    {
        var server = new JsonRpcServer();
        await using var vault = new FakeVault(request => server.Answer(request.Method, Encoding.UTF8.GetString(request.Body ?? [])) is var (status, body)
            ? new ForwardResponse
            {
                Status = status,
                Headers = body.Length == 0 ? [] : [new WireHeader("Content-Type", "application/json")],
                Body = Encoding.UTF8.GetBytes(body),
            }
            : throw new InvalidOperationException());
        var access = vault.Access();
        var direct = new FakeNetwork();
        var transports = new McpTransports(new McpTransportOptions { Vault = access.Client, Placeholders = access.Placeholders, Network = direct });
        var registry = new ToolRegistry();
        var definition = new McpServerDefinition
        {
            Name = "remote",
            Transport = McpTransportKind.Http,
            Url = new Uri("https://mcp.example/mcp"),
            Secret = new SecretRef("mcp-token"),
            StartTimeout = TimeSpan.FromSeconds(20),
        };
        await using var clients = new McpClients([definition], transports, registry);
        clients.Start();
        await clients.Started.WaitAsync(Token);
        var status = Assert.Single(clients.Status());
        Assert.True(status.State == McpServerState.Ready, status.Detail);
        Assert.Equal(["remote__echo"], status.Tools);
        // An HTTP server always reaches the network, so an untrusted one's tool asks in a tainted session and taints with its result.
        Assert.True(registry.List().Single().Risk.Network);

        Assert.True(registry.TryGet("remote__echo", out var tool));
        var context = new ToolCallContext("s1", "c1") { Tainted = true };
        var result = (JsonObject)(await tool.InvokeAsync(context.AttachTo(new AIFunctionArguments { ["text"] = "hi" }), Token))!;
        Assert.Equal("echo: hi", result["content"]!.GetValue<string>());

        // Every request went through Vault with the placeholder, the toolset and the caller's taint; none went direct.
        Assert.Empty(direct.Requests);
        var forwarded = vault.Forwarded.ToArray();
        Assert.NotEmpty(forwarded);
        var placeholder = Assert.Single(vault.Minted);
        Assert.All(forwarded, request =>
        {
            Assert.Equal("https://mcp.example/mcp", request.Url);
            Assert.Equal("Bearer " + placeholder, FakeVault.Header(request, "Authorization"));
            Assert.Equal("mcp:remote", request.Toolset);
        });
        var call = forwarded.Single(request => Encoding.UTF8.GetString(request.Body ?? []).Contains("\"tools/call\"", StringComparison.Ordinal));
        Assert.True(call.Tainted);
        Assert.False(forwarded.Single(request => Encoding.UTF8.GetString(request.Body ?? []).Contains("\"tools/list\"", StringComparison.Ordinal)).Tainted);
    }

    [Fact]
    public async Task WithoutVaultAServerWithASecretDoesNotStart()
    {
        var direct = new FakeNetwork();
        var registry = new ToolRegistry();
        await using var clients = new McpClients([new McpServerDefinition
        {
            Name = "remote",
            Transport = McpTransportKind.Http,
            Url = new Uri("https://mcp.example/mcp"),
            Secret = new SecretRef("mcp-token"),
        }], new McpTransports(new McpTransportOptions { Network = direct }), registry);
        clients.Start();
        await clients.Started.WaitAsync(Token);
        var status = Assert.Single(clients.Status());
        Assert.Equal(McpServerState.Failed, status.State);
        Assert.Contains("Vault is not configured", status.Detail);
        Assert.Empty(direct.Requests);
    }

    [Fact]
    public async Task AKeylessServerIsReachedDirectlyWithoutCredentials()
    {
        var server = new JsonRpcServer();
        var direct = new FakeNetwork((request, body) =>
        {
            var (status, answer) = server.Answer(request.Method.Method, body);
            var response = new HttpResponseMessage((HttpStatusCode)status);
            if (answer.Length > 0)
                response.Content = new StringContent(answer, Encoding.UTF8, "application/json");
            return response;
        });
        var registry = new ToolRegistry();
        await using var clients = new McpClients([new McpServerDefinition
        {
            Name = "open",
            Transport = McpTransportKind.Http,
            Url = new Uri("http://127.0.0.1:9/mcp"),
        }], new McpTransports(new McpTransportOptions { Network = direct }), registry);
        clients.Start();
        await clients.Started.WaitAsync(Token);
        Assert.Equal(McpServerState.Ready, Assert.Single(clients.Status()).State);
        Assert.All(direct.Requests, request => Assert.False(request.Request.Headers.Contains("Authorization")));
    }

    /// <summary>Just enough of a streamable HTTP MCP server, answering JSON: the handshake, tools/list and tools/call.</summary>
    private sealed class JsonRpcServer
    {
        public ConcurrentQueue<string> Methods { get; } = new();

        public (int Status, string Body) Answer(string method, string body)
        {
            if (method != "POST")
                return (405, "");
            var message = JsonNode.Parse(body)!.AsObject();
            var name = message["method"]?.GetValue<string>();
            Methods.Enqueue(name ?? "(response)");
            if (message["id"] is not { } id)
                return (202, "");
            JsonNode result;
            switch (name)
            {
                case "initialize":
                    result = new JsonObject
                    {
                        ["protocolVersion"] = message["params"]!["protocolVersion"]!.GetValue<string>(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "remote", ["version"] = "1.0" },
                    };
                    break;
                case "tools/list":
                    result = new JsonObject
                    {
                        ["tools"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "echo",
                            ["description"] = "Echo the text.",
                            ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}"""),
                        }),
                    };
                    break;
                case "tools/call":
                    var text = message["params"]!["arguments"]!["text"]!.GetValue<string>();
                    result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "echo: " + text }) };
                    break;
                default:
                    // server/discover and anything else: not offered, so the client falls back to the initialize handshake.
                    return (200, new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                    }.ToJsonString());
            }
            return (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString());
        }
    }
}
