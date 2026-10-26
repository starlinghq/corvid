using Corvid.Kernel.Turns;
using Corvid.Tools;

namespace Corvid.Kernel.Tests;

/// <summary>
/// A7V1-7: the turn's tool filter (code mode's: never for an in-process generator) is applied at every step, so a tool it
/// withholds once the generator changed partway through a turn (a fallback, a privacy retreat) is not offered again.
/// </summary>
public sealed class ToolFilterStepTests
{
    private static readonly ToolRisk ReadOnly = new(SideEffects.None, Network: false, Destructive: false);
    private const string Schema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    [Fact]
    public async Task A_tool_the_filter_withholds_after_the_first_step_is_not_offered_to_the_next()
    {
        var codeAllowed = true;
        var lookup = TestTool.Create("lookup", "Look something up.", ReadOnly, Schema, (_, _) =>
        {
            // The generator changed during the step: the filter now withholds the script tool.
            codeAllowed = false;
            return ValueTask.FromResult<object?>("found");
        }, new ToolParameter("query", null, true, false));
        var script = TestTool.Create("run_script", "Run a script.", ReadOnly, Schema,
            (_, _) => ValueTask.FromResult<object?>("ran"), new ToolParameter("query", null, true, false));
        var generator = new FakeGenerator(Reply.Call("lookup", """{"query":"x"}""", "l1"), Reply.Say("Found it."));
        using var harness = new TurnHarness(generator,
            options: new TurnOptions { ToolFilter = tool => tool.Name == "lookup" || tool.Name == "run_script" && codeAllowed },
            tools: tools =>
            {
                tools.Add(lookup);
                tools.Add(script);
            });

        var result = await harness.RunAsync("look it up");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Equal(2, generator.Calls.Count);
        Assert.Contains("run_script", generator.Calls[0].ToolNames);
        Assert.Contains("lookup", generator.Calls[1].ToolNames);
        Assert.DoesNotContain("run_script", generator.Calls[1].ToolNames);
    }
}
