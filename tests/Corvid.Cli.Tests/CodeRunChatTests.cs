using Corvid.Host.Tests;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Cli.Tests;

/// <summary>
/// A7: a code mode run in <c>corvid chat</c>: its approval card shows the whole source with hidden characters as escapes and
/// says each call is asked on its own, and the script's calls are printed indented under the run.
/// </summary>
public sealed class CodeRunChatTests
{
    private sealed class Tools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    [Fact]
    public async Task The_chat_shows_the_runs_source_on_its_card_and_its_calls_indented_under_it()
    {
        // The JSON escape puts a right-to-left override in the script; the card must show it as a code.
        using var run = new CliRun(new FakeModels(new FakeGenerator(
            Reply.Call("execute_code", """{"language":"python","code":"import corvid_tools as t\nprint(t.list_notes())  # \u202e"}"""),
            Reply.Say("The script listed your notes."))))
        {
            Services = services => services.AddSingleton<IToolProvider>(new Tools(new FakeCodeTool(("list_notes", "{}")))),
        };

        var exit = await run.RunAsync(["chat", "--set", "Approvals:Mode=Manual"], "run the weather script over my notes", "y", "/exit");

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        Assert.Contains("Code run: Python, 2 lines", output, StringComparison.Ordinal);
        Assert.Contains("goes through the approval pipeline on its own", output, StringComparison.Ordinal);
        Assert.Contains("The source holds hidden characters, shown below as escapes.", output, StringComparison.Ordinal);
        Assert.Contains("  import corvid_tools as t", output, StringComparison.Ordinal);
        Assert.Contains("  print(t.list_notes())  # \\u202E", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", output, StringComparison.Ordinal);
        // The script's call, indented under the run, and the run's line counting it.
        var lines = output.Split('\n');
        var nested = Array.FindIndex(lines, line => line.StartsWith("    ↳ ✓ list_notes succeeded", StringComparison.Ordinal));
        var parent = Array.FindIndex(lines, line => line.StartsWith("  ✓ execute_code succeeded · 1 tool call ·", StringComparison.Ordinal));
        Assert.True(nested >= 0 && parent > nested, output);
    }
}
