using System.Text.Json.Nodes;
using Corvid.Cli.Chat;
using Corvid.Host.Tests;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;

namespace Corvid.Cli.Tests;

/// <summary>Runs `corvid` in-process against a test console and scripted input, in a temporary directory.</summary>
internal sealed class CliRun : IDisposable
{
    public CliRun(FakeModels? models = null, bool useStarling = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "work"));
        Models = useStarling ? null : models ?? new FakeModels();
        Console.Profile.Width = 240;
    }

    public string Root { get; }

    public FakeModels? Models { get; }

    public TestConsole Console { get; } = new();

    /// <summary>More services for the host, after the fake slots (an in-process MCP server's transports, C-033).</summary>
    public Action<IServiceCollection>? Services { get; init; }

    public string ConfigFile => Path.Combine(Root, "corvid.json");

    public string Output => Console.Output;

    /// <summary>The run's settings as `--set` switches: fake slots (or an empty Starling cache), and a temporary data directory.</summary>
    public string[] Settings()
    {
        var settings = FakeModels.Settings(Root);
        if (Models is null)
        {
            settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Corvid:DataDirectory"] = Path.Combine(Root, "data"),
                ["Corvid:Skills:Directory"] = Path.Combine(Root, "skills"),
                ["Corvid:Models:Cache"] = Path.Combine(Root, "empty-cache"),
                ["Corvid:Approvals:RulesFile"] = Path.Combine(Root, "approvals.json"),
            };
        }
        return [.. settings.SelectMany(pair => new[] { "--set", $"{pair.Key["Corvid:".Length..]}={pair.Value}" }), "--config", ConfigFile];
    }

    public async Task<int> RunAsync(string[] args, params string?[] input)
    {
        var context = new CliContext(Console, new ScriptedChatInput(input))
        {
            ConfigureServices = services =>
            {
                Models?.Register(services);
                Services?.Invoke(services);
            },
        };
        // The test's own switches last, so they override the run's settings.
        return await CorvidCli.RunAsync([.. Settings(), .. args], context, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

public sealed class ChatTests
{
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    [Fact]
    public async Task AScriptedSessionStreamsTheAnswerShowsToolsAndRunsCommands()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes."))));
        var exit = await run.RunAsync(["chat"], "list my notes", "/status", "/goal tidy the notes", "/exit");

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        Assert.Contains("corvid · fake-gen@cpu · 4 tool(s)", output, StringComparison.Ordinal);
        // The tool panel, in line mode on a non-interactive console.
        Assert.Contains("⏺ list_notes running", output, StringComparison.Ordinal);
        Assert.Contains("✓ list_notes succeeded", output, StringComparison.Ordinal);
        // Streamed once, not printed again at the end of the turn.
        Assert.Equal(1, Count(output, "You have two notes."));
        Assert.Contains("· answered · 1 step(s), 1 tool call(s)", output, StringComparison.Ordinal);
        Assert.Contains("fake-gen@cpu", output[output.IndexOf("Status", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("Goal set: tidy the notes", output, StringComparison.Ordinal);
        Assert.Contains("resume it with `corvid chat --session cli-", output, StringComparison.Ordinal);
        Assert.Equal(1, run.Models!.Tools.List.Invocations);
    }

    [Fact]
    public async Task ApprovalsAreAskedInlineAndAnsweredOnTheNextLine()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added it."))));
        var exit = await run.RunAsync(["chat"], "add a note: water plants", "maybe", "s", "/approvals", "/exit");

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        Assert.Contains("approval needed", output, StringComparison.Ordinal);
        Assert.Contains("Allow? y once · s this session · a always · n deny", output, StringComparison.Ordinal);
        Assert.Contains("Answer y, n, s or a.", output, StringComparison.Ordinal);
        Assert.Contains("Approved (session).", output, StringComparison.Ordinal);
        Assert.Contains("water plants", run.Models!.Tools.Notes);
        Assert.Contains("✓ add_note succeeded", output, StringComparison.Ordinal);
        // The session rule the answer made is listed.
        Assert.Contains("session", output[output.LastIndexOf("Approvals", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelListsNumberedModelsAndANumberOnTheNextLineSwitches()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        var exit = await run.RunAsync(["chat"], "/model", "2", "/exit");

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        Assert.Contains("/model picks a model", output, StringComparison.Ordinal);
        Assert.Contains("generator: fake-gen@cpu", output, StringComparison.Ordinal);
        Assert.Contains("Switch generator to 2 fake-gen-large (Enter keeps it) ›", output, StringComparison.Ordinal);
        Assert.Contains("generator: fake-gen@cpu → fake-gen-large@cpu.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnythingButANumberAtThePickIsAnOrdinaryLine()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        var exit = await run.RunAsync(["chat"], "/model", "/status", "/model", "", "/exit");

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        // The command typed at the pick ran, and nothing switched.
        Assert.Contains("fake-gen@cpu", output[output.IndexOf("Status", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("→", output, StringComparison.Ordinal);
        Assert.Contains("Leaving session", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeniedCallDoesNotRun()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"feed cat"}"""), Reply.Say("I did not add it."))));
        await run.RunAsync(["chat"], "add a note: feed cat", "n", "/exit");

        Assert.Contains("Denied.", run.Output, StringComparison.Ordinal);
        Assert.Contains("⊘ add_note denied", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("feed cat", run.Models!.Tools.Notes);
        Assert.Equal(0, run.Models.Tools.Add.Invocations);
    }

    [Fact]
    public async Task TheEndOfInputDeniesAPendingApprovalAndLeaves()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"x"}"""), Reply.Say("Not added."))));
        var exit = await run.RunAsync(["chat"], "add a note: x");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, run.Models!.Tools.Add.Invocations);
    }

    [Fact]
    public async Task WithoutModelsChatSaysHowToAcquireThem()
    {
        // The real Starling provider over an empty model cache: nothing is downloaded, and the message says how.
        using var run = new CliRun(useStarling: true);
        var exit = await run.RunAsync(["chat"], "hello");

        Assert.Equal(ExitCodes.ModelsMissing, exit);
        var output = run.Output;
        Assert.Contains("Corvid cannot start: a configured model is not in the model cache.", output, StringComparison.Ordinal);
        foreach (var model in new[] { "qwen3.5-0.8b", "gliner2-base-v1", "embeddinggemma-2" })
            Assert.Contains($"corvid model download {model}", output, StringComparison.Ordinal);
        Assert.Contains("STARLING_MODELS", output, StringComparison.Ordinal);
        Assert.Contains("corvid doctor", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidConfigurationIsReportedBeforeAnythingLoads()
    {
        using var run = new CliRun();
        var exit = await run.RunAsync(["chat", "--set", "Models:Slots:generator:Model=nope", "--set", "Devices:CpuThreads=0"], "hello");
        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("The configuration is not valid", run.Output, StringComparison.Ordinal);
        Assert.Contains("no model is named 'nope'", run.Output, StringComparison.Ordinal);
        Assert.Contains("CpuThreads", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Models!.Provider.Loaded);
    }

    [Fact]
    public async Task ASessionResumesById()
    {
        using var run = new CliRun();
        await run.RunAsync(["chat"], "first message", "/exit");
        var id = System.Text.RegularExpressions.Regex.Match(run.Output, "session (cli-[0-9a-f-]+)").Groups[1].Value;
        Assert.NotEmpty(id);
        await run.RunAsync(["--session", id], "/exit");
        Assert.Contains("Resumed: 2 entries", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionAnotherProcessDrivesIsNotResumedHere()
    {
        using var run = new CliRun();
        await run.RunAsync(["chat"], "first message", "/exit");
        var id = System.Text.RegularExpressions.Regex.Match(run.Output, "session (cli-[0-9a-f-]+)").Groups[1].Value;
        Assert.NotEmpty(id);
        // G-12: another process (the app, another terminal) holds the session's driver lock.
        using (var held = Corvid.Host.Composition.ProcessLock.TryAcquire(Path.Combine(run.Root, "data", "drivers", id + ".lock")))
        {
            Assert.NotNull(held);
            Assert.Equal(ExitCodes.Failure, await run.RunAsync(["--session", id], "never sent", "/exit"));
            Assert.Contains($"Session {id} is driven by", run.Output, StringComparison.Ordinal);
            Assert.Contains($"(pid {Environment.ProcessId})", run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Resumed:", run.Output, StringComparison.Ordinal);
        }
        // Once it lets go, the terminal resumes it.
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["--session", id], "/exit"));
        Assert.Contains("Resumed: 2 entries", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandsAreReportedAndTheChatGoesOn()
    {
        using var run = new CliRun();
        await run.RunAsync(["chat"], "/frobnicate", "/help", "/exit");
        Assert.Contains("Unknown command /frobnicate.", run.Output, StringComparison.Ordinal);
        Assert.Contains("/memory", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutKevTheChatStartsOnTheDegradedTierAndSaysHowToFetchIt()
    {
        using var run = new CliRun(new FakeModels(kevReady: false));
        var exit = await run.RunAsync(["chat", "--set", "Models:Slots:decider:Model=fake-kev"], "hello", "/exit");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("degraded tier", run.Output, StringComparison.Ordinal);
        Assert.Contains("corvid model download fake-kev", run.Output, StringComparison.Ordinal);
        Assert.Contains("Hello from the fake generator.", run.Output, StringComparison.Ordinal);
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void OptionsMayComeBeforeOrAfterTheCommand()
    {
        var parsed = CliArguments.Parse(["--set", "Turn:MaxSteps=3", "chat", "--verbose", "--set", "Corvid:Approvals:Mode=Manual"]);
        Assert.Equal("chat", parsed.Command);
        Assert.Equal("3", parsed.Overrides["Corvid:Turn:MaxSteps"]);
        Assert.Equal("Manual", parsed.Overrides["Corvid:Approvals:Mode"]);
        Assert.True(parsed.Flag("--verbose"));
    }

    [Fact]
    public void ChatIsTheDefaultAndASessionImpliesIt()
    {
        Assert.Equal("chat", CliArguments.Parse([]).Command);
        var resumed = CliArguments.Parse(["--session", "cli-1"]);
        Assert.Equal("chat", resumed.Command);
        Assert.Equal("cli-1", resumed.Option("--session"));
    }

    [Fact]
    public void EvalsKeepsItsOwnHelp()
    {
        Assert.True(CliArguments.Parse(["--help"]).Help);
        var evals = CliArguments.Parse(["evals", "calibrate", "--help"]);
        Assert.False(evals.Help);
        Assert.Equal(["calibrate", "--help"], evals.Arguments);
    }

    [Theory]
    [InlineData("--set", "novalue")]
    [InlineData("--config")]
    public void MalformedOptionsAreUsageErrors(params string[] args) => Assert.Throws<ArgumentException>(() => CliArguments.Parse(args));

    [Fact]
    public async Task HelpVersionAndUnknownCommands()
    {
        var console = new TestConsole();
        var context = new CliContext(console, new ScriptedChatInput([]));
        Assert.Equal(ExitCodes.Success, await CorvidCli.RunAsync(["--help"], context, TestContext.Current.CancellationToken));
        Assert.Contains("usage: corvid", console.Output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, await CorvidCli.RunAsync(["--version"], context, TestContext.Current.CancellationToken));
        Assert.Contains("corvid ", console.Output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, await CorvidCli.RunAsync(["frobnicate", "--config", Path.Combine(Path.GetTempPath(), "none.json")],
            context, TestContext.Current.CancellationToken));
        Assert.Contains("Unknown command 'frobnicate'", console.Output, StringComparison.Ordinal);
    }
}

public sealed class ModelAndDoctorTests
{
    [Fact]
    public async Task ModelListsTheCatalogueWithoutModelsPresent()
    {
        using var run = new CliRun(useStarling: true);
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["model"]));
        var output = run.Output;
        foreach (var model in new[] { "qwen3.5-0.8b", "gliner2-base-v1", "embeddinggemma-2", "multilingual-e5-small", "kev-0.8b", "kev-4b" })
            Assert.Contains(model, output, StringComparison.Ordinal);
        Assert.Equal(6, output.Split("missing").Length - 1);
        Assert.Contains("Without kev-0.8b the decider runs on the degraded tier.", output, StringComparison.Ordinal);
        Assert.Contains("corvid model download <model>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchWritesTheConfigFileAfterValidating()
    {
        using var run = new CliRun();
        await File.WriteAllTextAsync(run.ConfigFile, """{ "corvid": { "Turn": { "MaxSteps": 4 } } }""", TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["model", "switch", "summarizer", "fake-gen"]));
        var written = JsonNode.Parse(await File.ReadAllTextAsync(run.ConfigFile, TestContext.Current.CancellationToken))!;
        Assert.Equal("fake-gen", (string?)written["corvid"]!["Models"]!["Slots"]!["summarizer"]!["Model"]);
        Assert.Equal(4, (int?)written["corvid"]!["Turn"]!["MaxSteps"]);

        var before = await File.ReadAllTextAsync(run.ConfigFile, TestContext.Current.CancellationToken);
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["model", "switch", "extractor", "fake-gen"]));
        Assert.Contains("'fake-gen' cannot serve extractor", run.Output, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(run.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["model", "switch", "nonslot", "fake-gen"]));
        // The escalation ladder cannot contain the default.
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["model", "switch", "generator", "fake-gen-large"]));
        Assert.Contains("cannot also be its own escalation", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadFetchesExplicitly()
    {
        using var run = new CliRun(new FakeModels(generatorReady: false));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["model", "download", "fake-gen"]));
        Assert.Contains("Downloading fake-gen", run.Output, StringComparison.Ordinal);
        Assert.Contains("✓ Model 'fake-gen' is ready.", run.Output, StringComparison.Ordinal);
        // Each file's line is written before the result line (C-039: progress is synchronous, not posted to the pool).
        var file = run.Output.IndexOf("model.onnx", StringComparison.Ordinal);
        Assert.InRange(file, 0, run.Output.IndexOf("✓ Model 'fake-gen' is ready.", StringComparison.Ordinal) - 1);
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["model", "download", "nope"]));
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["model", "download"]));
    }

    [Fact]
    public async Task DoctorFailsWithoutModelsAndSaysHowToFetchThem()
    {
        using var run = new CliRun(useStarling: true);
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["doctor"]));
        Assert.Contains("corvid model download qwen3.5-0.8b", run.Output, StringComparison.Ordinal);
        Assert.Contains("check(s) failed", run.Output, StringComparison.Ordinal);
        Assert.Contains("bubblewrap", run.Output, StringComparison.Ordinal);
    }
}

public sealed class GatewayCommandTests
{
    [Fact]
    public async Task PairingIsApprovedOnTheMachineOnlyWithAnExplicitRole()
    {
        using var run = new CliRun();
        var store = new Corvid.Gateway.PairingStore(Path.Combine(run.Root, "data", "gateway.db"));
        var code = store.Request("telegram", "42", "ada", "42").Request!.Code;

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "pair"]));
        Assert.Contains(Corvid.Gateway.PairingStore.Display(code), run.Output, StringComparison.Ordinal);

        // No default role: approving a stranger's code by habit must not make them the owner.
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["gateway", "pair", "approve", code]));
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["gateway", "pair", "approve", code, "--owner", "--guest"]));
        Assert.Empty(store.Users());
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "pair", "approve", code.ToLowerInvariant(), "--guest"]));
        Assert.Equal(Corvid.Gateway.SenderRole.Guest, Assert.Single(store.Users()).Role);
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["gateway", "pair", "approve", code, "--owner"]));

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "status"]));
        Assert.Contains("No adapter is enabled", run.Output, StringComparison.Ordinal);
        Assert.Contains("No gateway is running.", run.Output, StringComparison.Ordinal);
        Assert.Contains("1 paired, 0 waiting", run.Output, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "pair", "revoke", "telegram", "42"]));
        Assert.Empty(store.Users());
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["gateway", "frobnicate"]));
    }

    [Fact]
    public async Task AGuestGetsTheirOwnToolsFromTheMachine()
    {
        using var run = new CliRun();
        var store = new Corvid.Gateway.PairingStore(Path.Combine(run.Root, "data", "gateway.db"));
        store.Approve(store.Request("telegram", "42", "ada", "42").Request!.Code, Corvid.Gateway.SenderRole.Guest);
        store.Approve(store.Request("telegram", "7", "me", "7").Request!.Code, Corvid.Gateway.SenderRole.Owner);

        // G-14: the same allowlist the app's Gateway page sets, checked the same way.
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "pair", "tools", "telegram", "42", "list_notes", "web_fetch"]));
        Assert.Equal(["list_notes", "web_fetch"], store.GuestTools("telegram", "42"));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "pair"]));
        Assert.Contains("list_notes, web_fetch (their own)", run.Output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["gateway", "pair", "tools", "telegram", "42", "cron_create"]));
        Assert.Contains("'cron_create' cannot be a guest tool", run.Output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["gateway", "pair", "tools", "telegram", "7", "list_notes"]));
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["gateway", "pair", "tools", "telegram", "42"]));
        Assert.Equal(["list_notes", "web_fetch"], store.GuestTools("telegram", "42"));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["gateway", "pair", "tools", "telegram", "42", "--adapter"]));
        Assert.Null(store.GuestTools("telegram", "42"));
    }

    [Fact]
    public async Task RunningWithoutAnEnabledAdapterSaysWhatToConfigure()
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["gateway"]));
        Assert.Contains("No gateway adapter is enabled", run.Output, StringComparison.Ordinal);
    }
}

public sealed class EvalsTests
{
    [Fact]
    public async Task EvalsRunsCorvidEvalsWithTheUsersLogByDefault()
    {
        using var run = new CliRun();
        // A session first, so the default log exists.
        await run.RunAsync(["chat"], "hello", "/exit");
        var exit = await run.RunAsync(["evals", "report"]);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("degraded", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvalsPassesHelpThrough()
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["evals", "--help"]));
        Assert.Contains("usage: corvid-evals", run.Output, StringComparison.Ordinal);
    }
}

public sealed class McpCommandTests
{
    [Fact]
    public async Task ListSaysWhenNothingIsConfiguredAndReportsAServerThatFails()
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp"]));
        Assert.Contains("No MCP servers are configured", run.Output, StringComparison.Ordinal);

        var exit = await run.RunAsync(["mcp", "list", "--set", "Mcp:Servers:broken:Command=/nonexistent/mcp-server",
            "--set", "Mcp:Servers:broken:Sandboxed=false", "--set", $"Mcp:Servers:broken:Workspace={Path.Combine(run.Root, "broken")}"]);
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("broken", run.Output, StringComparison.Ordinal);
        Assert.Contains("failed", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownSubcommandIsAUsageError()
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["mcp", "frobnicate"]));
        Assert.Contains("usage: corvid mcp", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("diff", "notes")]
    [InlineData("diff", "notes", "send", "extra")]
    [InlineData("accept", "notes")]
    [InlineData("accept", "notes", "--all", "--hash", "0123456789ab")]
    [InlineData("allow", "notes")]
    [InlineData("forget")]
    [InlineData("forget", "notes", "--unconfigured")]
    [InlineData("list", "--hash", "0123456789ab")]
    [InlineData("accept", "notes", "send", "--hash")]
    public async Task ReviewVerbsCheckTheirArity(params string[] arguments)
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["mcp", .. arguments]));
        Assert.Contains("usage: corvid mcp", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowOutsideAChatNeedsTheReviewedHash()
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Usage, await run.RunAsync(["mcp", "allow", "notes", "send"]));
        Assert.Contains("needs --hash", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("diff", "nobody", "send")]
    [InlineData("accept", "nobody", "send")]
    [InlineData("allow", "nobody", "send", "--hash", "0123456789ab")]
    public async Task ReviewingAnUnknownServerFails(params string[] arguments)
    {
        using var run = new CliRun();
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["mcp", .. arguments]));
        Assert.Contains("There is no enabled MCP server named 'nobody'", run.Output, StringComparison.Ordinal);
    }

    private static JsonFileMcpPinStore Seed(CliRun run, params string[] servers)
    {
        var store = new JsonFileMcpPinStore(Path.Combine(run.Root, "data", "mcp-pins.json"));
        var definition = McpToolDefinition.From(new ModelContextProtocol.Protocol.Tool { Name = "send", Description = "Send." });
        foreach (var server in servers)
            store.Set(server, new Dictionary<string, McpToolPin> { ["send"] = new(definition.Hash, definition.Canonical, DateTimeOffset.UnixEpoch) });
        return store;
    }

    [Fact]
    public async Task ForgetDropsOneServersPinsAndRecordsIt()
    {
        using var run = new CliRun();
        var store = Seed(run, "notes", "github");

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "forget", "notes"]));

        Assert.Contains("Forgot notes's 1 pinned definition", run.Output, StringComparison.Ordinal);
        Assert.Contains("picks this up only when it restarts", run.Output, StringComparison.Ordinal);
        Assert.Equal(["github"], store.Servers());
        var log = new Kernel.Events.SqliteEventLog(Path.Combine(run.Root, "data", "events.db"));
        var record = Assert.IsType<Abstractions.Events.McpPinsForgotten>(Assert.Single(log.Read(Abstractions.Events.McpSessions.Pins, cancellationToken: TestContext.Current.CancellationToken)).Payload);
        Assert.Equal(("notes", 1), (record.Server, record.Tools));

        // Nothing to forget is not a failure.
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "forget", "notes"]));
        Assert.Contains("has no pinned definitions", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgetUnconfiguredKeepsDisabledServersAndListSaysWhatIsStale()
    {
        using var run = new CliRun();
        var store = Seed(run, "stale", "paused");
        string[] paused = ["--set", "Mcp:Servers:paused:Enabled=false", "--set", "Mcp:Servers:paused:Command=paused-mcp"];

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "list", .. paused]));
        Assert.Contains("stale: pinned but no longer configured (corvid mcp forget stale", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("paused: pinned but no longer configured", run.Output, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "forget", "--unconfigured", .. paused]));
        Assert.Equal(["paused"], store.Servers());
    }

    // Review of #47, finding 5: a successful review outside a chat, over an in-process server registered as the transports.
    private static string[] FakeServer(CliRun run) =>
    [
        "--set", "Mcp:Servers:fake:Command=fake", "--set", "Mcp:Servers:fake:Trust=Trusted",
        "--set", $"Mcp:Servers:fake:Workspace={Path.Combine(run.Root, "fake")}",
    ];

    private static string Next(CliRun run, string verb)
    {
        var marker = $"next: corvid mcp {verb} fake send --hash ";
        var output = run.Output;
        var at = output.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, output);
        return output.Substring(at + marker.Length, McpClients.MinHashPrefix);
    }

    [Fact]
    public async Task DiffThenAcceptWithTheHashPinsTheChangedDefinitionAndRecordsIt()
    {
        await using var server = new Mcp.Tests.FakeMcpServer().Add("send", "Send a message.");
        using var run = new CliRun { Services = services => services.AddSingleton<IMcpTransports>(server) };
        var store = Seed(run, "fake");
        var listed = McpToolDefinition.From(server.Tools[0]).Hash;

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "diff", "fake", "send", .. FakeServer(run)]));
        Assert.Contains("fake/send: held (changed)", run.Output, StringComparison.Ordinal);
        var hash = Next(run, "accept");
        Assert.Equal(listed[..McpClients.MinHashPrefix], hash);

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "accept", "fake", "send", "--hash", hash, .. FakeServer(run)]));
        Assert.Contains("Accepted fake/send as fake__send", run.Output, StringComparison.Ordinal);
        Assert.Contains("picks this up only when it restarts", run.Output, StringComparison.Ordinal);
        Assert.Equal(listed, store.Pins("fake")["send"].Hash);
        var log = new Kernel.Events.SqliteEventLog(Path.Combine(run.Root, "data", "events.db"));
        var record = Assert.IsType<Abstractions.Events.McpToolAccepted>(log.Read(Abstractions.Events.McpSessions.Pins,
            cancellationToken: TestContext.Current.CancellationToken)[^1].Payload);
        Assert.Equal((listed, false), (record.Hash, record.Flagged));

        // Nothing is held any more: a second accept fails.
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["mcp", "accept", "fake", "send", "--hash", hash, .. FakeServer(run)]));
    }

    [Fact]
    public async Task DiffThenAllowWithTheHashOffersAWithheldDefinitionAsUntrusted()
    {
        await using var server = new Mcp.Tests.FakeMcpServer().Add("send", "Send a message.");
        using var run = new CliRun { Services = services => services.AddSingleton<IMcpTransports>(server) };
        var listed = McpToolDefinition.From(server.Tools[0]);
        var store = new JsonFileMcpPinStore(Path.Combine(run.Root, "data", "mcp-pins.json"));
        store.Set("fake", new Dictionary<string, McpToolPin>
        {
            ["send"] = new(listed.Hash, listed.Canonical, DateTimeOffset.UnixEpoch, Withheld: listed.Hash, WithheldDecision: "d-9"),
        });

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "diff", "fake", "send", .. FakeServer(run)]));
        Assert.Contains("fake/send: held (injection)", run.Output, StringComparison.Ordinal);
        Assert.Contains("decision d-9", run.Output, StringComparison.Ordinal);
        var hash = Next(run, "allow");

        // accept refuses a withheld definition, even with the right hash.
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["mcp", "accept", "fake", "send", "--hash", hash, .. FakeServer(run)]));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["mcp", "allow", "fake", "send", "--hash", hash, .. FakeServer(run)]));
        Assert.Contains("Allowed fake/send as fake__send", run.Output, StringComparison.Ordinal);
        var pin = store.Pins("fake")["send"];
        Assert.Equal((listed.Hash, (string?)null), (pin.AllowedFlagged, pin.Withheld));
    }
}

public sealed class RendererTests
{
    [Fact]
    public void TheLivePanelLeavesItsLastStateInTheScrollback()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        var renderer = new ChatRenderer(console, live: true);
        renderer.BeginTurn();
        renderer.ToolStarted("c1", "list_notes", approval: false);
        renderer.ToolStarted("c2", "fetch_page", approval: false);
        renderer.ToolFinished("c1", "list_notes", "succeeded", TimeSpan.FromMilliseconds(12));
        renderer.ToolFinished("c2", "fetch_page", "failed", TimeSpan.FromMilliseconds(1500));
        renderer.Text("Done.");

        Assert.Contains("list_notes", console.Output, StringComparison.Ordinal);
        Assert.Contains("succeeded · 12 ms", console.Output, StringComparison.Ordinal);
        Assert.Contains("failed · 1.5 s", console.Output, StringComparison.Ordinal);
        Assert.Contains("Done.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLivePanelShowsTheStepAndARunningCallsLastLineCleanedForTheTerminal()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        var renderer = new ChatRenderer(console, live: true);
        renderer.BeginTurn();
        renderer.Progress(2, 8);
        renderer.ToolStarted("c1", "shell", approval: false);
        // K-2: untrusted output; an escape sequence must not reach the terminal.
        renderer.ToolOutput("c1", "shell", "Building…\n\u001b[31mPassed 11   Failed 1\u001b[0m\n");
        renderer.Text("Still running.");

        Assert.Contains("step 2 of 8", console.Output, StringComparison.Ordinal);
        Assert.Contains("Passed 11   Failed 1", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m", console.Output, StringComparison.Ordinal);
        Assert.Equal("[31mPassed 11   Failed 1[0m", ChatRenderer.LastLine("Building…\n\u001b[31mPassed 11   Failed 1\u001b[0m\n\n"));
    }

    [Fact]
    public void AClarifyingQuestionsChoicesAreListedAndANumberPicksOne()
    {
        var console = new TestConsole();
        var renderer = new ChatRenderer(console, live: false);
        var question = new ClarifyingQuestion("q-1", "Which version?", ["0.2.0", "0.2.0-rc1"]);
        var result = new TurnResult(TurnStatus.Clarifying, "Which version?", 0, 1, false, "reply", [], []) { Question = question };
        renderer.BeginTurn();
        renderer.EndTurn(new Delivery(new Corvid.Abstractions.Sessions.Envelope("cli-1", "cli", "tag it", Corvid.Abstractions.Sessions.EnvelopeOrigin.User), result));

        Assert.Contains("Which version?", console.Output, StringComparison.Ordinal);
        Assert.Contains("1 0.2.0 · 2 0.2.0-rc1", console.Output, StringComparison.Ordinal);
        // K-6: a number picks the choice as offered; anything else is the answer as typed.
        Assert.Equal("0.2.0-rc1", ChatRenderer.Answer(question, " 2 "));
        Assert.Equal("3", ChatRenderer.Answer(question, "3"));
        Assert.Equal("rc1 please", ChatRenderer.Answer(question, "rc1 please"));
        Assert.Equal("1", ChatRenderer.Answer(null, "1"));
    }

    [Fact]
    public void ApprovalChoicesFollowWhatIsOffered()
    {
        Assert.Equal("Allow? [green]y[/] once · [red]n[/] deny ›", ChatRenderer.Choices([Corvid.Approvals.ApprovalScope.Once]));
        Assert.Contains("[green]a[/] always", ChatRenderer.Choices([Corvid.Approvals.ApprovalScope.Once, Corvid.Approvals.ApprovalScope.Always]),
            StringComparison.Ordinal);
    }
}
