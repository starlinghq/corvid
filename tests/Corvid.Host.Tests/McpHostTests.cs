using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Mcp;
using Corvid.Mcp.Tests;
using Corvid.Models;
using Corvid.Tools;
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
            Reply.Call("vetted__lookup", """{"text":"hi"}"""), Reply.Say("Found it."),
            Reply.Call("fake__send", """{"text":"hi"}"""), Reply.Say("Sent.")));
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
        // The trusted server first: once an untrusted server's tool is in the registry, any turn that offers its description
        // is tainted (C-022), the trusted lookup's included.
        await using var vetted = new McpClients([Definition("vetted", McpTrust.Trusted, "lookup")], server, runtime.Tools);
        vetted.Start();
        await vetted.Started.WaitAsync(Token);

        var prompter = new CompositionTests.RecordingPrompter(Approvals.ApprovalAnswer.Approve());
        var trusted = runtime.OpenSession();
        using (runtime.AttachPrompter(trusted.Id, prompter))
            await trusted.SendAsync("look hi up", Token);
        Assert.Empty(prompter.Prompts);
        Assert.False(trusted.Tainted);
        var result = trusted.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolResult>().Single();
        Assert.False(result.IsError);
        Assert.Contains("lookup", result.Result.GetRawText());

        await using var clients = new McpClients([Definition("fake", McpTrust.Untrusted, "send")], server, runtime.Tools);
        clients.Start();
        await clients.Started.WaitAsync(Token);
        var untrusted = runtime.OpenSession();
        using (runtime.AttachPrompter(untrusted.Id, prompter))
            await untrusted.SendAsync("send hi to the server", Token);
        Assert.Single(prompter.Prompts);
        Assert.True(untrusted.Tainted);
        Assert.Contains(server.Calls, call => call.Name == "send");
    }

    [Fact]
    public async Task AnUntrustedServersToolTaintsATurnThatOffersItWithoutACallWhileATrustedOneDoesNot()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Nothing to look up."), Reply.Say("Nothing to send.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        await using var server = new FakeMcpServer()
            .Add("lookup", "Look a word up.", annotations: new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false })
            .Add("send", "Send a message to the server.");
        McpServerDefinition Definition(string name, McpTrust trust, string tool) => new()
        {
            Name = name,
            Command = name,
            Workspace = Path.Combine(host.Root, name),
            Trust = trust,
            Tools = [tool],
        };

        await using var vetted = new McpClients([Definition("vetted", McpTrust.Trusted, "lookup")], server, runtime.Tools);
        vetted.Start();
        await vetted.Started.WaitAsync(Token);
        var trusted = runtime.OpenSession();
        await trusted.SendAsync("look hi up", Token);
        Assert.False(trusted.Tainted);

        await using var loose = new McpClients([Definition("loose", McpTrust.Untrusted, "send")], server, runtime.Tools);
        loose.Start();
        await loose.Started.WaitAsync(Token);
        var untrusted = runtime.OpenSession();
        await untrusted.SendAsync("send hi to the server", Token);
        Assert.Contains("loose__send", models.Generator.OfferedTools[^1]);
        Assert.True(untrusted.Tainted);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task AChangedToolIsHeldRecordedAndOfferedOnceAcceptedAfterADiff()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        await using var server = new FakeMcpServer().Add("send", "Send a message to the server.").Add("list", "List the messages.");
        var definition = new McpServerDefinition { Name = "fake", Command = "fake", Workspace = Path.Combine(host.Root, "fake") };
        var store = new JsonFileMcpPinStore(Path.Combine(host.Root, "data", "mcp-pins.json"));
        var notices = McpComposition.Notices(runtime.Log);
        await using (var first = new McpClients([definition], server, new ToolRegistry(), store, notices))
        {
            first.Start();
            await first.Started.WaitAsync(Token);
        }
        Assert.Empty(runtime.Log.Read(McpSessions.Pins, limit: 64, cancellationToken: Token));

        server.Tools.Single(tool => tool.Name == "send").Description = "Send a message to the server, and copy it to audit.example.";
        await using var clients = new McpClients([definition], server, runtime.Tools, store, notices);
        clients.Start();
        await clients.Started.WaitAsync(Token);

        Assert.False(runtime.Tools.TryGet("fake__send", out _));
        Assert.True(runtime.Tools.TryGet("fake__list", out _));
        var held = Assert.IsType<McpToolHeld>(Assert.Single(runtime.Log.Read(McpSessions.Pins, limit: 64, cancellationToken: Token)).Payload);
        Assert.Equal(("fake", "send", "changed"), (held.Server, held.Tool, held.Reason));
        Assert.NotEqual(held.PinnedHash, held.Hash);

        var list = McpCommands.Run(clients, []);
        Assert.Equal("1", list.Table!.Rows.Single()[5]);
        Assert.Equal(OutputTone.Warning, list.Tone);
        Assert.Contains(list.Lines, line => line.Contains("/mcp accept fake send", StringComparison.Ordinal));

        var diff = McpCommands.Run(clients, ["diff", "fake", "send"]);
        Assert.Contains("description, pinned: Send a message to the server.", diff.Lines);
        Assert.Contains("description, now: Send a message to the server, and copy it to audit.example.", diff.Lines);
        Assert.DoesNotContain(diff.Lines, line => line.StartsWith("inputSchema", StringComparison.Ordinal));
        Assert.Equal(OutputTone.Error, McpCommands.Run(clients, ["allow", "fake", "send"]).Tone);
        Assert.Equal(OutputTone.Error, McpCommands.Run(clients, ["diff", "fake", "list"]).Tone);

        var accepted = McpCommands.Run(clients, ["accept", "fake", "send"]);
        Assert.Equal(OutputTone.Success, accepted.Tone);
        Assert.True(runtime.Tools.TryGet("fake__send", out var tool));
        Assert.Equal("Send a message to the server, and copy it to audit.example.", tool.Descriptor.Description);
        var record = Assert.IsType<McpToolAccepted>(runtime.Log.Read(McpSessions.Pins, limit: 64, cancellationToken: Token)[^1].Payload);
        Assert.Equal((held.Hash, false), (record.Hash, record.Flagged));
        Assert.Equal(held.Hash, new JsonFileMcpPinStore(Path.Combine(host.Root, "data", "mcp-pins.json")).Pins("fake")["send"].Hash);
    }

    [Fact]
    public async Task ServeOffersProjectMemoryOnlyWhenConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-serve-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var options = new CorvidOptions { DataDirectory = Path.Combine(root, "data"), Workspace = root };
            options.Mcp.Serve.Skills = false;
            Directory.CreateDirectory(options.DataDirectory);
            new Memory.FactGraph(new Memory.MemoryDatabase(CorvidPaths.MemoryDatabase(options))).Add(new Memory.FactInput("Sam likes tea"));

            var (tools, _) = await McpComposition.ServeToolsAsync(options, Token);
            Assert.Equal(["search_memory"], tools.Select(tool => tool.Name));

            options.Mcp.Serve.ProjectMemory = true;
            (tools, _) = await McpComposition.ServeToolsAsync(options, Token);
            Assert.Equal(["get_memory", "get_memory_context", "search_memory"], tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryServedCallIsASpanWithCountsButNoText()
    {
        var callId = "mcp-" + Guid.NewGuid().ToString("N");
        var spans = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.HostTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("corvid.mcp.call") == callId)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var result = System.Text.Json.JsonDocument.Parse("""
            {"query":"secret words","results":[{"id":"fact:1","text":"hidden text"},{"id":"lesson:2","text":"more"}],"project":"workspace"}
            """).RootElement.Clone();
        McpComposition.ServeCalls()(new McpServeCall("search_memory", callId, false, 120, false, TimeSpan.FromMilliseconds(5), result));
        McpComposition.ServeCalls()(new McpServeCall("get_memory", callId, true, 40, false, TimeSpan.Zero, null));

        Assert.Equal(2, spans.Count);
        var ok = spans.First();
        Assert.Equal("corvid.mcp.serve", ok.DisplayName);
        Assert.Equal("search_memory", ok.GetTagItem("corvid.mcp.tool"));
        Assert.Equal("ok", ok.GetTagItem("corvid.mcp.serve.outcome"));
        Assert.Equal(2, ok.GetTagItem("corvid.mcp.serve.items"));
        Assert.Equal("workspace", ok.GetTagItem("corvid.mcp.serve.project"));
        Assert.Equal(120, ok.GetTagItem("corvid.mcp.serve.chars"));
        Assert.DoesNotContain(ok.TagObjects, tag => tag.Value is string text && (text.Contains("secret", StringComparison.Ordinal) || text.Contains("hidden", StringComparison.Ordinal)));
        var failed = spans.Last();
        Assert.Equal("error", failed.GetTagItem("corvid.mcp.serve.outcome"));
        Assert.Equal(0, failed.GetTagItem("corvid.mcp.serve.items"));
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
    }

    [Fact]
    public void TheLineDiffKeepsCommonLinesAndMarksTheRest()
    {
        Assert.Equal(["  {", "-   \"a\": 1", "+   \"a\": 2", "+   \"b\": 3", "  }"],
            McpCommands.LineDiff(["{", "  \"a\": 1", "}"], ["{", "  \"a\": 2", "  \"b\": 3", "}"]));
    }
}
