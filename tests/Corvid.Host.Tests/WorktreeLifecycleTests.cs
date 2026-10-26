using System.Diagnostics;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Kernel.Turns;

namespace Corvid.Host.Tests;

/// <summary>
/// A6 review: a removal holds its worktree while the person decides (M2), children's worktrees are cleaned up when nothing
/// was done in them or the children never ran (M3), and a fork checks its source's workspace before copying anything.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class WorktreeLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>Approves everything; while <see cref="Hold"/> is set, a worktree's question waits for <see cref="Answer"/>.</summary>
    private sealed class Gated : IApprovalPrompter
    {
        public bool Hold { get; set; }

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ApprovalAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            if (!Hold || !prompt.Reasons.Any(reason => reason.Code == ApprovalReasons.Required))
                return ApprovalAnswer.Approve();
            Asked.TrySetResult();
            return await Answer.Task.WaitAsync(cancellationToken);
        }
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var argument in new[] { "-c", "user.name=User", "-c", "user.email=user@example.com", "-c", "init.defaultBranch=main" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    private static TestHost Host(FakeModels models, Dictionary<string, string?>? more = null)
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "git"))))
            Assert.Skip("git is not installed");
        var settings = new Dictionary<string, string?>
        {
            ["Corvid:Tools:Delegate"] = "true",
            ["Corvid:Sessions:WorktreesDirectory"] = "{root}/worktrees",
        };
        foreach (var (key, value) in more ?? [])
            settings[key] = value;
        var host = TestHost.Create(models, settings);
        var work = Path.Combine(host.Root, "work");
        Git(work, "init", "-q");
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Git(work, "add", ".");
        Git(work, "commit", "-q", "-m", "first");
        return host;
    }

    private static IReadOnlyList<SessionEvent> Events(CorvidRuntime runtime, string sessionId) =>
        [.. runtime.Log.Read(sessionId, limit: 4096, cancellationToken: Ct).Select(item => item.Payload)];

    // M2: while the person decides on a removal, nothing starts in the worktree, and a second removal is refused.
    [Fact]
    public async Task While_a_removal_is_being_decided_no_session_starts_in_the_worktree()
    {
        await using var host = Host(new FakeModels());
        await using var runtime = await host.StartAsync();
        var asking = runtime.OpenSession(surface: "test");
        var prompter = new Gated();
        using var attached = runtime.AttachPrompter(asking.Id, prompter);
        var workspace = await runtime.CreateWorktreeAsync(asking.Id, "gated", cancellationToken: Ct);

        prompter.Hold = true;
        var removal = runtime.RemoveWorktreeAsync(asking.Id, "gated", Ct);
        await prompter.Asked.Task.WaitAsync(Wait, Ct);

        var refused = Assert.Throws<WorkspaceUnavailableException>(() =>
            runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = workspace.Path }));
        Assert.Contains("being removed", refused.Message, StringComparison.Ordinal);
        var twice = await Assert.ThrowsAsync<WorktreeRefusedException>(() => runtime.RemoveWorktreeAsync(asking.Id, "gated", Ct));
        Assert.Contains("already being removed", twice.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(workspace.Path));

        prompter.Answer.SetResult(ApprovalAnswer.Approve());
        await removal.WaitAsync(Wait, Ct);
        Assert.False(Directory.Exists(workspace.Path));
    }

    // M3: a child that did nothing in its worktree leaves neither the worktree nor its branch.
    [Fact]
    public async Task A_child_that_did_nothing_in_its_worktree_leaves_no_worktree_or_branch()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, JsonSerializer.Serialize(new { goal = "look around", isolation = "worktree" })),
            Reply.Say("Nothing needed doing."),
            Reply.Say("The helper found nothing to do.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession(surface: "test");
        using var attached = runtime.AttachPrompter(parent.Id, new Gated());

        await parent.SendAsync("look around with a helper", Ct);

        var spawned = Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>());
        var bound = Assert.Single(Events(runtime, spawned.ChildId).OfType<WorkspaceBound>());
        Assert.NotNull(bound.Branch);
        Assert.False(Directory.Exists(bound.Path));
        Assert.Empty(await runtime.ListWorktreesAsync(parent.Id, Ct));
        Assert.DoesNotContain("corvid/", Git(Path.Combine(host.Root, "work"), "branch", "--list"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"worktree\"", Events(runtime, parent.Id).OfType<ToolResult>().Last().Result.GetRawText(), StringComparison.Ordinal);
    }

    // M3: a worktree made for a child that then could not open (no place left) goes again; so does the one that ran clean.
    [Fact]
    public async Task Worktrees_of_children_that_never_ran_are_removed()
    {
        var call = JsonSerializer.Serialize(new
        {
            tasks = new[]
            {
                new { goal = "first", isolation = "worktree" },
                new { goal = "second", isolation = "worktree" },
            },
        });
        var models = new FakeModels(new FakeGenerator(Reply.Call(DelegateTaskTool.ToolName, call), Reply.Say("Done."), Reply.Say("One helper ran.")));
        await using var host = Host(models, new() { ["Corvid:Agents:MaxConcurrent"] = "1" });
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession(surface: "test");
        using var attached = runtime.AttachPrompter(parent.Id, new Gated());

        await parent.SendAsync("two helpers", Ct);

        var result = Events(runtime, parent.Id).OfType<ToolResult>().Last().Result.GetRawText();
        Assert.Contains("refused", result, StringComparison.Ordinal);
        Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>());
        Assert.Empty(await runtime.ListWorktreesAsync(parent.Id, Ct));
        Assert.DoesNotContain("corvid/", Git(Path.Combine(host.Root, "work"), "branch", "--list"), StringComparison.Ordinal);
    }

    // A fork of a session whose worktree is gone is refused before its log is copied.
    [Fact]
    public async Task A_fork_of_a_session_whose_worktree_is_gone_leaves_no_log_behind()
    {
        await using var host = Host(new FakeModels());
        await using var runtime = await host.StartAsync();
        var asking = runtime.OpenSession(surface: "test");
        using var attached = runtime.AttachPrompter(asking.Id, new Gated());
        var source = await runtime.OpenWorktreeSessionAsync(asking.Id, "gone", "test", Ct);
        runtime.Log.Append(source.Id, runtime.Log.GetHead(source.Id, Ct),
            [EventData.Create(new UserMessage("hello")), EventData.Create(new AssistantMessage("Hello."))], Ct);
        await runtime.RemoveWorktreeAsync(asking.Id, "gone", Ct);
        var before = runtime.Log.ListSessions(Ct).Count;

        await Assert.ThrowsAsync<WorkspaceUnavailableException>(() => runtime.ForkAsync(source.Id, new ForkOptions { Surface = "test" }, Ct));

        Assert.Equal(before, runtime.Log.ListSessions(Ct).Count);
    }
}
