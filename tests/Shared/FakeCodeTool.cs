using System.Text.Json;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Tests;

/// <summary>
/// A7: a stand-in for code mode's <c>execute_code</c> (the code host builds the real one) for the surfaces' tests: it takes
/// the real tool's arguments (<c>language</c>, <c>code</c>) and "runs" the script by making the nested calls it was given,
/// through the parent call's invoker, exactly the path a script's requests on the tools socket take. Its result has the
/// shape a run reports (<c>calls</c>, <c>exit_code</c>, <c>stdout</c>). A side effect (it runs a process), so it asks.
/// </summary>
internal sealed class FakeCodeTool(params (string Tool, string Arguments)[] calls) : ToolFunction(new ToolDescriptor("execute_code",
    "Run a weather script in the sandbox; the script may call other tools.",
    """{"type":"object","properties":{"language":{"type":"string"},"code":{"type":"string"}},"required":["language","code"]}""",
    new ToolRisk(SideEffects.Process, Network: false, Destructive: false),
    [new ToolParameter("language", null, true, false), new ToolParameter("code", null, true, false)]))
{
    public List<NestedToolResult> Results { get; } = [];

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = ToolCallContext.Require(arguments, "execute_code");
        foreach (var (tool, json) in calls)
        {
            using var document = JsonDocument.Parse(json);
            Results.Add(await context.InvokeToolAsync(tool, document.RootElement.Clone(), cancellationToken));
        }
        using var result = JsonDocument.Parse($$"""{"calls":{{calls.Length}},"exit_code":0,"stdout":"ran {{calls.Length}} call(s)"}""");
        return result.RootElement.Clone();
    }
}
