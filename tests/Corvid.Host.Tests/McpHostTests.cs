using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Mcp;
using Corvid.Mcp.Tests;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Corvid.Host.Tests;

/// <summary>C-018 in the host: configuration, a start that never waits on a server, and MCP tools in real turns.</summary>
public sealed class McpHostTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ServerConfigurationIsValidatedWithEverythingElse()
    {
        var options = new CorvidOptions();
        options.Models.Slots[ModelSlots.Generator] = new SlotSettings { Model = "fake-gen" };
        options.Models.Slots[ModelSlots.TextEmbedder] = new SlotSettings { Model = "fake-embed" };
        options.Mcp.Servers["github"] = new McpServerSettings
        {
            Command = "github-mcp",
            Environment = new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_x" },
        };
        options.Mcp.Servers["remote"] = new McpServerSettings { Url = "http://mcp.example/", Secret = "mcp-token" };
        options.Mcp.Servers["odd"] = new McpServerSettings { Transport = "websocket", Command = "x" };
        options.Mcp.Servers["Bad Name"] = new McpServerSettings { Command = "x" };
        options.Mcp.Servers["off"] = new McpServerSettings { Enabled = false, Transport = "websocket" };
        var result = new CorvidOptionsValidator(new Models.ModelCatalogue([new FakeModels().Provider])).Validate(null, options);
        Assert.True(result.Failed);
        var failures = result.Failures!.ToList();
        Assert.Contains(failures, failure => failure.Contains("'GITHUB_PERSONAL_ACCESS_TOKEN' looks like a secret", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("must use https", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("Transport 'websocket' is not stdio or http", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("'Bad Name' is not a valid server name", StringComparison.Ordinal));
        // A disabled server is not checked.
        Assert.Single(failures, failure => failure.Contains("websocket", StringComparison.Ordinal));
    }

    [Fact]
    public void ServersAreDeniedVaultAndCorvidStateAndGetAPrivateWorkspace()
    {
        var options = new CorvidOptions { DataDirectory = "/tmp/corvid-data-x", Workspace = "/tmp" };
        options.Mcp.Servers["notes"] = new McpServerSettings { Command = "notes-mcp" };
        var (servers, problems) = McpComposition.Definitions(options, McpComposition.DeniedPaths(options, "/tmp/corvid-x.json"));
        Assert.Empty(problems);
        var server = Assert.Single(servers);
        Assert.Contains("/tmp/corvid-data-x", server.DeniedPaths);
        Assert.Contains("/tmp/corvid-x.json", server.DeniedPaths);
        Assert.Contains(server.DeniedPaths, path => path.Contains("corvid-vault", StringComparison.Ordinal));
        Assert.EndsWith(Path.Combine("corvid-mcp", "notes"), server.Workspace);
        Assert.Equal(McpTrust.Untrusted, server.Trust);
        Assert.True(server.Sandboxed);
        Assert.False(server.Network);
    }

    [Fact]
    public async Task AServerThatCannotStartNeverHoldsUpTheRuntime()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Mcp:Servers:broken:Command"] = "/nonexistent/mcp-server",
            ["Corvid:Mcp:Servers:broken:Sandboxed"] = "false",
            ["Corvid:Mcp:Servers:broken:Workspace"] = "{root}/broken",
        });
        var clock = Stopwatch.StartNew();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        // The turn runs whatever the server is doing.
        await session.SendAsync("hello", Token);
        await runtime.Mcp!.Started.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));

        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var output = await commands.ExecuteAsync("/mcp", new SlashCommandContext(runtime, session, commands), Token);
        var row = Assert.Single(output.Table!.Rows);
        Assert.Equal(["broken", "stdio", "untrusted", "failed"], row.Take(4));
        Assert.Equal(OutputTone.Warning, output.Tone);

        var doctor = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: Token);
        Assert.Contains(doctor, check => check is { Area: "mcp", Name: "broken", Status: CheckStatus.Warning });
    }

    [Fact]
    public async Task AnUntrustedServersToolAsksAndTaintsWhileATrustedReadOnlyOneRunsUnasked()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call("fake__send", """{"text":"hi"}"""), Reply.Say("Sent."),
            Reply.Call("vetted__lookup", """{"text":"hi"}"""), Reply.Say("Found it.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        await using var server = new FakeMcpServer()
            .Add("send", "Send a message to the server.")
            .Add("lookup", "Look a word up.", annotations: new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false });
        McpServerDefinition Definition(string name, McpTrust trust, string tool) => new()
        {
            Name = name,
            Command = name,
            Workspace = Path.Combine(host.Root, name),
            Trust = trust,
            Tools = [tool],
        };
        await using var clients = new McpClients([Definition("fake", McpTrust.Untrusted, "send"), Definition("vetted", McpTrust.Trusted, "lookup")],
            server, runtime.Tools);
        clients.Start();
        await clients.Started.WaitAsync(Token);

        var prompter = new CompositionTests.RecordingPrompter(Approvals.ApprovalAnswer.Approve());
        var untrusted = runtime.OpenSession();
        using (runtime.AttachPrompter(untrusted.Id, prompter))
            await untrusted.SendAsync("send hi to the server", Token);
        Assert.Single(prompter.Prompts);
        Assert.True(untrusted.Tainted);
        Assert.Contains(server.Calls, call => call.Name == "send");

        var trusted = runtime.OpenSession();
        using (runtime.AttachPrompter(trusted.Id, prompter))
            await trusted.SendAsync("look hi up", Token);
        Assert.Single(prompter.Prompts);
        Assert.False(trusted.Tainted);
        var result = trusted.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolResult>().Single();
        Assert.False(result.IsError);
        Assert.Contains("lookup", result.Result.GetRawText());
    }
}
