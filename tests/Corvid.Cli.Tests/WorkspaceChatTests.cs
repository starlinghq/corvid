using System.Diagnostics;
using Corvid.Host.Tests;

namespace Corvid.Cli.Tests;

/// <summary>A6 (G-4): <c>corvid chat --workspace</c> and <c>/workspace</c>, with the real git CLI for worktrees.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class WorkspaceChatTests
{
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var argument in new[] { "-c", "user.name=User", "-c", "user.email=user@example.com", "-c", "init.defaultBranch=main" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    [Fact]
    public async Task A_chat_started_in_another_workspace_says_so_and_a_missing_one_is_refused()
    {
        using var run = new CliRun(new FakeModels());
        var other = Directory.CreateDirectory(Path.Combine(run.Root, "other")).FullName;

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["chat", "--workspace", other], "/workspace", "/exit"));
        Assert.Contains($"Workspace: {other}", run.Output, StringComparison.Ordinal);
        Assert.Contains($"This session works in {other}.", run.Output, StringComparison.Ordinal);
        // A new session is not one resumed: its only event so far is where it works.
        Assert.DoesNotContain("Resumed:", run.Output, StringComparison.Ordinal);

        using var missing = new CliRun(new FakeModels());
        Assert.Equal(ExitCodes.Failure, await missing.RunAsync(["chat", "--workspace", Path.Combine(missing.Root, "nowhere")], "/exit"));
        Assert.Contains("is not a directory that exists", missing.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_new_asks_makes_a_worktree_and_remove_takes_it_away()
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "git"))))
            Assert.Skip("git is not installed");
        using var run = new CliRun(new FakeModels());
        var work = Path.Combine(run.Root, "work");
        Git(work, "init", "-q");
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Git(work, "add", ".");
        Git(work, "commit", "-q", "-m", "first");
        var worktrees = Path.Combine(run.Root, "worktrees");

        await run.RunAsync(["chat", "--set", $"Sessions:WorktreesDirectory={worktrees}"],
            "/workspace new nope", "n", "/workspace new topic", "y", "/workspace list", "/workspace remove topic", "y", "/exit");

        Assert.Contains("The worktree was not approved.", run.Output, StringComparison.Ordinal);
        Assert.Contains("on the branch corvid/topic", run.Output, StringComparison.Ordinal);
        Assert.Contains("Removed the worktree", run.Output, StringComparison.Ordinal);
        Assert.Contains("branch corvid/topic stays", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("corvid/nope", Git(work, "branch", "--list"), StringComparison.Ordinal);
        Assert.Contains("corvid/topic", Git(work, "branch", "--list"), StringComparison.Ordinal);
        Assert.Equal("main", Git(work, "branch", "--show-current").Trim());
        Assert.Empty(Directory.EnumerateDirectories(worktrees, "topic", SearchOption.AllDirectories));
    }
}
