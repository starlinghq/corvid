using System.Text.RegularExpressions;
using Corvid.Host.Tests;
using Corvid.Kernel.Turns;

namespace Corvid.Cli.Tests;

/// <summary>A5: child sessions in <c>corvid chat</c>: the spawn's card, the children's start and end, and a child is read-only.</summary>
public sealed partial class DelegationChatTests
{
    [Fact]
    public async Task The_chat_shows_the_spawn_card_the_childs_end_and_refuses_to_drive_a_child()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, """{"goal":"count the items","tools":["list_notes"],"max_steps":2}"""),
            Reply.Say("Two notes."),
            Reply.Say("Your helper counted two notes."))));

        var exit = await run.RunAsync(["chat", "--set", "Tools:Delegate=true", "--set", "Approvals:Mode=Manual"],
            "ask a helper", "y", "/agents", "/exit");

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        // The card: the child's plan as it would start.
        Assert.Contains("Child: count the items", output, StringComparison.Ordinal);
        Assert.Contains("tools: list_notes", output, StringComparison.Ordinal);
        Assert.Contains("budget: 2 steps", output, StringComparison.Ordinal);
        // Its start and end, then /agents.
        var child = ChildId().Match(output).Value;
        Assert.Contains($"↳ {child} started by the agent: count the items", output, StringComparison.Ordinal);
        Assert.Contains($"↳ {child} answered after 0 steps: Two notes.", output, StringComparison.Ordinal);
        Assert.Contains("models: generator fake-gen (local, escalates to fake-gen-large)", output, StringComparison.Ordinal);
        Assert.Contains("Child sessions", output, StringComparison.Ordinal);

        var refused = await RunIn(run, ["chat", "--session", child], "hello", "/exit");
        Assert.Equal(ExitCodes.Failure, refused);
        Assert.Contains($"{child} is a child session of cli-", run.Output, StringComparison.Ordinal);
        Assert.Contains("it is read-only", run.Output, StringComparison.Ordinal);
    }

    private static Task<int> RunIn(CliRun run, string[] args, params string?[] input) => run.RunAsync(args, input);

    [GeneratedRegex("child-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}")]
    private static partial Regex ChildId();
}
