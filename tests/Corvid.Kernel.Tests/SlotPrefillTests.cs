using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;
using Corvid.Tools.Builtin;

namespace Corvid.Kernel.Tests;

/// <summary>
/// Slot pre-fill (plan §7 step 4) against the real file tools: an extracted span is a hint beside an argument that failed,
/// and only when the span itself passes the tool's own validation.
/// </summary>
public sealed class SlotPrefillTests : IDisposable
{
    // list_files's path description, which is the label the extractor is asked for.
    private const string PathLabel = "The directory, relative to the workspace; '.' is the workspace itself.";

    private readonly string _workspace = Directory.CreateTempSubdirectory("corvid-prefill-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private TurnHarness Harness(FakeGenerator generator, string word) =>
        new(generator, extractor: new FakeExtractor(0.95f, (PathLabel, word)), workspace: _workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name == "list_files" },
            tools: tools => tools.Add(new FileTools(new Workspace(_workspace))));

    private static ToolResult[] Results(TurnHarness harness) => [.. harness.Payloads().OfType<ToolResult>()];

    [Fact]
    public async Task A_valid_path_runs_on_the_first_call_when_the_request_says_workspace()
    {
        // PR #20's real-model run: "." was right, and the "workspace" span must not turn it into a repair.
        File.WriteAllText(Path.Combine(_workspace, "readme.md"), "hi\n");
        var generator = new FakeGenerator(Reply.Call("list_files", """{"path":"."}"""), Reply.Say("One file: readme.md."));
        using var harness = Harness(generator, "workspace");

        var result = await harness.RunAsync("list the files in my workspace");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Equal((1, 1), (result.Steps, result.ToolCalls));
        Assert.Empty(result.Disagreements);
        var listed = Assert.Single(Results(harness));
        Assert.False(listed.IsError);
        Assert.Contains("readme.md", listed.Result.GetProperty("entries")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_the_binding_refuses_is_shown_the_extracted_slot_when_it_resolves()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "notes"));
        var generator = new FakeGenerator(
            Reply.Call("list_files", """{"path":"nots"}"""),
            Reply.Call("list_files", """{"path":"notes"}"""),
            Reply.Say("The notes folder is empty."));
        using var harness = Harness(generator, "notes");

        var result = await harness.RunAsync("list the notes folder");

        Assert.Equal(TurnStatus.Answered, result.Status);
        var disagreement = Assert.Single(result.Disagreements);
        Assert.Equal(("path", "nots", "notes"), (disagreement.Parameter, disagreement.Argument, disagreement.Extracted));
        var results = Results(harness);
        Assert.Equal(2, results.Length);
        Assert.True(results[0].IsError);
        Assert.Equal("notes", results[0].Result.GetProperty("from_request").GetProperty("path").GetString());
        Assert.False(results[1].IsError);
    }

    [Fact]
    public async Task A_span_that_fails_the_tools_own_validation_is_not_offered()
    {
        // No "workspace" directory exists, so steering to it would only fail again.
        var generator = new FakeGenerator(
            Reply.Call("list_files", """{"path":"wrkspace"}"""),
            Reply.Call("list_files", """{"path":"."}"""),
            Reply.Say("The workspace is empty."));
        using var harness = Harness(generator, "workspace");

        var result = await harness.RunAsync("list the files in my workspace");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Empty(result.Disagreements);
        var results = Results(harness);
        Assert.Equal(2, results.Length);
        Assert.True(results[0].IsError);
        Assert.False(results[0].Result.TryGetProperty("from_request", out _));
        Assert.False(results[1].IsError);
    }
}
