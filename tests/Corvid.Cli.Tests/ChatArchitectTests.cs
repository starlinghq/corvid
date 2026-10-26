using Corvid.Abstractions.Events;
using Corvid.Cli.Chat;
using Corvid.Host.Tests;
using Spectre.Console.Testing;

namespace Corvid.Cli.Tests;

/// <summary>A7: <c>corvid chat</c> shows an architect/editor turn's plan step, who planned and who edited.</summary>
public sealed class ChatArchitectTests
{
    [Fact]
    public async Task Architect_in_chat_prints_the_plan_step_before_the_editors_call_and_says_who_planned_in_the_footer()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        using var run = new CliRun(models);

        var exit = await run.RunAsync(["chat"], new ScriptedChatInput(["/architect", "list my notes", "/exit"]));

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        Assert.Contains("The next turn plans first: fake-gen-large@cpu plans, fake-gen@cpu edits.", output, StringComparison.Ordinal);
        var step = output.IndexOf("planned by fake-gen-large@cpu · edited by fake-gen@cpu", StringComparison.Ordinal);
        Assert.True(step >= 0, output);
        // The plan is printed once, as the plan, before the editor's call and its answer.
        var plan = output.IndexOf("Hello from the larger model.", StringComparison.Ordinal);
        Assert.True(plan > step);
        Assert.Equal(plan, output.LastIndexOf("Hello from the larger model.", StringComparison.Ordinal));
        Assert.True(plan < output.IndexOf("list_notes", plan, StringComparison.Ordinal));
        Assert.True(plan < output.IndexOf("You have two notes.", StringComparison.Ordinal));
        Assert.Contains("planned by fake-gen-large@cpu, edited by fake-gen@cpu", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_is_printed_cleaned_for_the_terminal()
    {
        var console = new TestConsole();
        var renderer = new ChatRenderer(console, live: false);
        renderer.BeginTurn();

        renderer.Planned(new TurnPlan("big\u001b[31m", "small", "stuck", 2), "1. Edit a.cs‮.\n2. Run the tests.");

        var output = console.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("re-planned by", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m", output, StringComparison.Ordinal);
        Assert.DoesNotContain("‮", output, StringComparison.Ordinal);
        Assert.Contains("    2. Run the tests.", output, StringComparison.Ordinal);
    }
}
