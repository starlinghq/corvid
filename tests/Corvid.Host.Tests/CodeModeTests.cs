using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Models;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// A7: execute_code in the composed runtime. A script's tool calls take the turn's own path (its side effect asks, its
/// network read taints), the script itself is approved as code, and the tool is offered only to a generator that runs
/// outside this process.
/// </summary>
public sealed class CodeModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A network tool whose result is outside content (it never reaches a network here).</summary>
    private sealed class PageTools : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() =>
        [
            new TestTool(new ToolDescriptor("fetch_page", "Fetch a web page.", """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""",
                new ToolRisk(SideEffects.None, Network: true, Destructive: false), [new ToolParameter("url", "the page", true, false)]),
                _ => "<html>outside text</html>"),
        ];
    }

    private sealed class Recording : IApprovalPrompter
    {
        public List<ApprovalPrompt> Prompts { get; } = [];

        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            lock (Prompts)
                Prompts.Add(prompt);
            return ValueTask.FromResult(ApprovalAnswer.Approve());
        }
    }

    // A raw client in Python: the frozen wire format, as code mode's generated clients speak it.
    private const string Script =
        """
        import json, os, socket
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        s.connect(os.environ["CORVID_TOOLS_SOCKET"])
        f = s.makefile("rwb")
        def line(o):
            f.write((json.dumps(o) + "\n").encode()); f.flush()
            return json.loads(f.readline())
        line({"token": open(os.environ["CORVID_TOOLS_TOKEN_FILE"]).read().strip(), "v": 1})
        print(line({"id": 1, "tool": "add_note", "args": {"text": "from the program"}})["ok"])
        print(line({"id": 2, "tool": "fetch_page", "args": {"url": "https://example.com/"}})["tainted"])
        print(line({"id": 3, "tool": "list_notes"})["result"])
        """;

    private static FakeModels Models(PrivacyClass generator, FakeGenerator script)
    {
        var models = new FakeModels(script);
        // The script's model, as a cloud or local server would be, or in process.
        models.Provider.Add("code-gen", [ModelSlots.Generator], () => script, privacy: generator);
        // Ranks execute_code first for a request that asks for a program, so the five-tool shortlist holds it.
        models.Provider.Add("code-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("program", "script"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    private static Dictionary<string, string?> Settings(bool code = true) => new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Tools:Code:Enabled"] = code ? "true" : "false",
        ["Corvid:Tools:Code:Tools:0"] = "add_note",
        ["Corvid:Tools:Code:Tools:1"] = "fetch_page",
        ["Corvid:Tools:Code:Tools:2"] = "list_notes",
        ["Corvid:Models:Slots:generator:Model"] = "code-gen",
        ["Corvid:Models:Slots:embedder.text:Model"] = "code-embed",
    };

    private static void RequireSandbox()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !new[] { "/usr/bin/bwrap", "/bin/bwrap", "/usr/local/bin/bwrap" }.Any(File.Exists),
            "bubblewrap is not installed, so scripts cannot run.");
        Assert.SkipWhen(CodeRuntimes.Discover().Python is null, "python3 is not installed, so the Python script cannot run.");
    }

    [Fact]
    public async Task Code_mode_is_off_by_default_and_its_calls_are_assessed_as_code()
    {
        await using (var off = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" }))
        await using (var runtime = await off.StartAsync())
            Assert.False(runtime.Tools.TryGet(CodeTool.ToolName, out _));

        await using var host = TestHost.Create(Models(PrivacyClass.LocalServer, new FakeGenerator()), Settings());
        await using var on = await host.StartAsync();
        Assert.True(on.Tools.TryGet(CodeTool.ToolName, out var tool));
        using var arguments = JsonDocument.Parse("""{"language":"python","code":"open('/etc/shadow')"}""");
        var analysis = on.Approvals.Analyze(new ToolAction(tool.Descriptor, arguments.RootElement.Clone()));
        Assert.Equal("code", analysis.Kind);
        Assert.Empty(analysis.Targets);
    }

    [Fact]
    public async Task A_scripts_calls_take_the_turns_path_their_approvals_and_taint()
    {
        RequireSandbox();
        var generator = new FakeGenerator(
            Reply.Call(CodeTool.ToolName, JsonSerializer.Serialize(new { language = "python", code = Script })),
            Reply.Say("The program ran."));
        await using var host = TestHost.Create(Models(PrivacyClass.LocalServer, generator), Settings(),
            services => services.AddSingleton<IToolProvider>(new PageTools()));
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var prompter = new Recording();

        using (runtime.AttachPrompter(session.Id, prompter))
            await session.SendAsync("write a program that adds a note", Ct);

        Assert.Contains(CodeTool.ToolName, generator.OfferedTools[0]);
        var events = session.Snapshot().Transcript.Select(entry => entry.Content).ToList();
        var parent = events.OfType<ToolCallIssued>().Single(call => call.ToolName == CodeTool.ToolName);
        var nested = events.OfType<ToolCallIssued>().Where(call => call.ParentCallId == parent.CallId).ToList();
        Assert.Equal(["add_note", "fetch_page", "list_notes"], nested.Select(call => call.ToolName));
        Assert.Equal([$"{parent.CallId}.1", $"{parent.CallId}.2", $"{parent.CallId}.3"], nested.Select(call => call.CallId));

        // The script was approved as code, its source on the card; then its side effect asked on its own, under its own id.
        Assert.Equal(2, prompter.Prompts.Count);
        Assert.Equal("code", prompter.Prompts[0].Analysis.Kind);
        Assert.Equal(Script, prompter.Prompts[0].Analysis.Text);
        Assert.Equal(parent.CallId, prompter.Prompts[0].CallId);
        Assert.Equal($"{parent.CallId}.1", prompter.Prompts[1].CallId);
        Assert.Contains("from the program", host.Models.Tools.Notes);

        var result = events.OfType<ToolResult>().Single(item => item.CallId == parent.CallId);
        Assert.False(result.IsError, result.Result.GetRawText());
        Assert.StartsWith("True\nTrue\n", result.Result.GetProperty("stdout").GetString(), StringComparison.Ordinal);
        Assert.True(result.Result.GetProperty("tainted").GetBoolean());
        Assert.Equal(3, result.Result.GetProperty("tool_calls").GetInt32());
        // The fetched page was outside content: the session is tainted, as after the model's own fetch.
        Assert.True(runtime.TryGetSession(session.Id, out var live) && live.Tainted);
    }

    [Fact]
    public async Task An_in_process_generator_is_not_offered_code_mode()
    {
        var generator = new FakeGenerator(
            Reply.Call(CodeTool.ToolName, JsonSerializer.Serialize(new { language = "python", code = "print(1)" })),
            Reply.Say("Done."));
        await using var host = TestHost.Create(Models(PrivacyClass.Local, generator), Settings());
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var prompter = new Recording();

        using (runtime.AttachPrompter(session.Id, prompter))
            await session.SendAsync("write a program that adds a note", Ct);

        Assert.DoesNotContain(CodeTool.ToolName, generator.OfferedTools[0]);
        // Its call is refused as not offered, before any approval.
        Assert.Empty(prompter.Prompts);
        var result = session.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolResult>().Single();
        Assert.True(result.IsError);
    }
}
