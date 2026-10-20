using System.Text.Json.Nodes;
using Corvid.Cli.Chat;
using Corvid.Host.Tests;
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
                ["Corvid:Models:Cache"] = Path.Combine(Root, "empty-cache"),
                ["Corvid:Approvals:RulesFile"] = Path.Combine(Root, "approvals.json"),
            };
        }
        return [.. settings.SelectMany(pair => new[] { "--set", $"{pair.Key["Corvid:".Length..]}={pair.Value}" }), "--config", ConfigFile];
    }

    public async Task<int> RunAsync(string[] args, params string?[] input)
    {
        var context = new CliContext(Console, new ScriptedChatInput(input)) { ConfigureServices = Models is null ? null : Models.Register };
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
        Assert.Contains("corvid · fake-gen@cpu · 2 tool(s)", output, StringComparison.Ordinal);
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
    public void ApprovalChoicesFollowWhatIsOffered()
    {
        Assert.Equal("Allow? [green]y[/] once · [red]n[/] deny ›", ChatRenderer.Choices([Corvid.Approvals.ApprovalScope.Once]));
        Assert.Contains("[green]a[/] always", ChatRenderer.Choices([Corvid.Approvals.ApprovalScope.Once, Corvid.Approvals.ApprovalScope.Always]),
            StringComparison.Ordinal);
    }
}
