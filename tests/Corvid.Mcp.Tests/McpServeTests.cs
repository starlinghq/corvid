using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Memory;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Corvid.Mcp.Tests;

/// <summary>
/// Server mode (plan §12): Corvid's memory search and skills for another agent, read-only, never local-only facts. The
/// end-to-end tests run <c>corvid mcp serve</c> as a stdio server for Corvid's own client, unconfined and under the launcher.
/// </summary>
public sealed class McpServeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-mcp-" + Guid.NewGuid().ToString("N")[..12]);

    public McpServeTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheServerRefusesToolsWithSideEffects()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CorvidMcpServer([new McpClientTests.StubTool("read"), new SideEffectTool()]));
        Assert.Contains("read-only tools only", exception.Message);
    }

    [Fact]
    public async Task MemorySearchSharesFactsButNeverLocalOnlyOnes()
    {
        var database = new MemoryDatabase(Path.Combine(_root, "memory.db"));
        var facts = new FactGraph(database);
        facts.Add(new FactInput("Sam likes green tea"));
        facts.Add(new FactInput("Sam's salary is 90k and Sam likes tea money", LocalOnly: true));
        var retriever = new HybridRetriever(new EpisodicIndex(database), facts);
        var server = new CorvidMcpServer(new SharedMemoryTools(retriever).CreateTools());

        await using var client = await ConnectAsync(server);
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        var search = Assert.Single(tools);
        Assert.Equal("search_memory", search.Name);
        Assert.True(search.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(search.ProtocolTool.Annotations?.DestructiveHint);

        var result = await client.CallToolAsync("search_memory", new Dictionary<string, object?> { ["query"] = "Sam tea" }, cancellationToken: Token);
        Assert.NotEqual(true, result.IsError);
        var text = ((TextContentBlock)Assert.Single(result.Content)).Text;
        Assert.Contains("green tea", text);
        Assert.DoesNotContain("salary", text);

        // A bad argument is the tool's error, reported as a tool result rather than a protocol failure.
        var bad = await client.CallToolAsync("search_memory", new Dictionary<string, object?> { ["query"] = " " }, cancellationToken: Token);
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task CorvidServesItsOwnClientOverStdio()
    {
        var corvid = Path.Combine(AppContext.BaseDirectory, "corvid");
        var definition = ServeDefinition(corvid) with { Sandboxed = false };
        await AssertServesAsync(definition);
    }

    [Fact]
    public async Task CorvidServesOverStdioUnderTheSandboxLauncher()
    {
        if (BubblewrapProblem() is { } reason)
        {
            // CI has bubblewrap (the job checks it first), so there the test must run; elsewhere it says why it did not.
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", reason);
            Assert.Skip(reason);
        }
        var corvid = Path.Combine(AppContext.BaseDirectory, "corvid");
        await AssertServesAsync(ServeDefinition(corvid));
    }

    /// <summary>`corvid mcp serve` with its data, skills and configuration all inside the server's own workspace.</summary>
    private McpServerDefinition ServeDefinition(string corvid)
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var database = new MemoryDatabase(Path.Combine(data, "memory.db"));
        new FactGraph(database).Add(new FactInput("The user's favourite editor is Helix"));
        var environment = new Dictionary<string, string>
        {
            ["CORVID_CONFIG"] = Path.Combine(_root, "corvid.json"),
            ["CORVID__DataDirectory"] = data,
            ["CORVID__Skills__Directory"] = Path.Combine(_root, "skills"),
            ["CORVID__Workspace"] = _root,
        };
        // The apphost finds the runtime through DOTNET_ROOT where it is not installed system-wide (CI's tool cache).
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            environment["DOTNET_ROOT"] = root;
        return new McpServerDefinition
        {
            Name = "corvid",
            Command = corvid,
            Arguments = ["mcp", "serve"],
            Workspace = _root,
            Environment = environment.ToImmutableDictionary(),
            Trust = McpTrust.Trusted,
            StartTimeout = TimeSpan.FromSeconds(60),
            CallTimeout = TimeSpan.FromSeconds(30),
        };
    }

    private static async Task AssertServesAsync(McpServerDefinition definition)
    {
        var registry = new ToolRegistry();
        await using var clients = new McpClients([definition], new McpTransports(), registry);
        clients.Start();
        await clients.Started.WaitAsync(TimeSpan.FromSeconds(90), Token);
        var status = Assert.Single(clients.Status());
        Assert.True(status.State == McpServerState.Ready, status.Detail);
        // Corvid's server marks its tools read-only, and a trusted client honours that: they run without asking.
        Assert.Equal(["corvid__search_memory", "corvid__skill_list", "corvid__skill_read"], status.Tools);
        Assert.All(registry.List(), tool => Assert.True(tool.Risk.ReadOnly));

        Assert.True(registry.TryGet("corvid__search_memory", out var search));
        var result = (JsonObject)(await search.InvokeAsync(new AIFunctionArguments { ["query"] = JsonDocument.Parse("\"favourite editor\"").RootElement.Clone() }, Token))!;
        Assert.Contains("Helix", result["content"]!.GetValue<string>());
        Assert.True(registry.TryGet("corvid__skill_list", out var list));
        result = (JsonObject)(await list.InvokeAsync(new AIFunctionArguments(), Token))!;
        Assert.Contains("\"skills\"", result["content"]!.GetValue<string>());
    }

    private static string? BubblewrapProblem()
    {
        if (!OperatingSystem.IsLinux())
            return "The sandbox runs on Linux only.";
        var bwrap = new[] { "/usr/bin/bwrap", "/bin/bwrap", "/usr/local/bin/bwrap" }.FirstOrDefault(File.Exists);
        if (bwrap is null)
            return "bubblewrap is not installed.";
        var start = new System.Diagnostics.ProcessStartInfo(bwrap) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["--unshare-all", "--unshare-user", "--disable-userns", "--ro-bind", "/", "/", "--", "/bin/true"])
            start.ArgumentList.Add(argument);
        using var probe = System.Diagnostics.Process.Start(start)!;
        var error = probe.StandardError.ReadToEnd();
        probe.WaitForExit();
        return probe.ExitCode == 0 ? null : $"bubblewrap cannot create namespaces here (exit {probe.ExitCode}): {error.Trim()}";
    }

    private static async Task<McpClient> ConnectAsync(CorvidMcpServer server)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var mcp = server.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "corvid"));
        _ = mcp.RunAsync(Token);
        return await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: Token);
    }

    private sealed class SideEffectTool() : ToolFunction(new ToolDescriptor("write", "Writes.", """{"type":"object","properties":{}}""",
        new ToolRisk(SideEffects.Filesystem, false, false), []))
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>("written");
    }
}
