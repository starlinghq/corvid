using System.Diagnostics;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Kernel.Workspaces;
using Corvid.Tests;
using Corvid.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// Where A6's groups meet in the host: search (G-5) finds a session that works in another workspace (G-4) at its own turn,
/// images a session names (K-8) are read from its own workspace, and a subagent in a worktree of its own keeps its
/// concurrency place, when abandoned, until its tool returns (the ship group's release of an abandoned child).
/// </summary>
public sealed class A6MeetingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // Titles off: the background titler would take the fake generator's scripted replies.
    private static readonly Dictionary<string, string?> NoTitles = new() { ["Corvid:Sessions:Titles"] = "false" };

    private sealed class Approving : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalAnswer.Approve());
    }

    private sealed class GateTool(TaskCompletionSource gate, TaskCompletionSource entered) : ToolFunction(
        new ToolDescriptor("wait_gate", "Wait for the gate.", """{"type":"object","properties":{}}""",
            new ToolRisk(SideEffects.None, Network: false, Destructive: false), []))
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            return "opened";
        }
    }

    private sealed class Provider(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    private static string Other(TestHost host) => Directory.CreateDirectory(Path.Combine(host.Root, "other")).FullName;

    [Fact]
    public async Task A_search_finds_a_session_in_another_workspace_at_its_own_turn()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Noted."), Reply.Say("Fine.")));
        await using var host = TestHost.Create(models, NoTitles);
        await using var runtime = await host.StartAsync();
        var elsewhere = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = Other(host) });
        await elsewhere.SendAsync("the okapi notes live in the other folder", Ct);
        var home = runtime.OpenSession(surface: "test");
        await home.SendAsync("nothing to find here", Ct);

        var hit = Assert.Single(runtime.SearchSessions("okapi", cancellationToken: Ct).Hits);

        Assert.Equal((elsewhere.Id, 1, "you"), (hit.SessionId, hit.Turn, hit.Who));
        // The session's first event is its workspace's, not a turn's: the hit's turn starts at the person's message.
        var first = runtime.Log.Read(elsewhere.Id, limit: 1, cancellationToken: Ct).Single();
        Assert.IsType<WorkspaceBound>(first.Payload);
        Assert.True(hit.Sequence > first.Sequence);
        Assert.Equal(hit.Sequence, hit.TurnSequence);
    }

    [Fact]
    public async Task Images_a_session_names_are_read_from_its_own_workspace()
    {
        await using var host = TestHost.Create(new FakeModels(), NoTitles);
        await using var runtime = await host.StartAsync();
        var other = Other(host);
        await File.WriteAllBytesAsync(Path.Combine(other, "shot.png"), TestImages.Png(), Ct);
        await File.WriteAllBytesAsync(Path.Combine(runtime.DefaultWorkspace.Root, "shot.png"), TestImages.Gif(), Ct);
        var elsewhere = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other });
        var home = runtime.OpenSession(surface: "test");

        var there = ImageAttachments.EnvironmentFor(runtime, elsewhere.Id);
        var (named, problems) = ImageAttachments.FromReferences("what is in @shot.png?", there);
        Assert.Empty(problems);
        Assert.Equal(TestImages.Png(), Assert.Single(named).Data.ToArray());
        Assert.Equal(TestImages.Png(), ImageAttachments.FromPath("shot.png", there).Data.ToArray());
        // The window's own session reads the window's workspace, as before.
        Assert.Equal(TestImages.Gif(), ImageAttachments.FromPath("shot.png", ImageAttachments.EnvironmentFor(runtime, home.Id)).Data.ToArray());
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

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task A_subagent_in_its_own_worktree_keeps_its_place_until_its_tool_returns()
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "git"))))
            Assert.Skip("git is not installed");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, JsonSerializer.Serialize(new { goal = "wait for it", tools = new[] { "wait_gate" }, isolation = "worktree" })),
            Reply.Call("wait_gate"),
            Reply.Say("the helper was stopped")));
        await using var host = TestHost.Create(models, new Dictionary<string, string?>
        {
            ["Corvid:Tools:Delegate"] = "true",
            ["Corvid:Approvals:Mode"] = "Manual",
            ["Corvid:Agents:MaxConcurrent"] = "1",
            ["Corvid:Sessions:WorktreesDirectory"] = "{root}/worktrees",
            ["Corvid:Sessions:Titles"] = "false",
        }, services => services.AddSingleton<IToolProvider>(new Provider(new GateTool(gate, entered))));
        var work = Path.Combine(host.Root, "work");
        Git(work, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "hello\n", Ct);
        Git(work, "add", ".");
        Git(work, "commit", "-q", "-m", "first");
        await using var runtime = await host.StartAsync();
        runtime.ChildStopGrace = TimeSpan.Zero;
        var parent = runtime.OpenSession(surface: "test");
        using var attached = runtime.AttachPrompter(parent.Id, new Approving());

        var delivery = await parent.PostAsync("wait with a helper", EnvelopeOrigin.User, Ct);
        await entered.Task.WaitAsync(Wait, Ct);
        var child = Assert.Single(runtime.Children(parent.Id)).ChildId;
        // The child's tool runs in the child's own worktree, with the tool the host added.
        var bound = Assert.Single(runtime.Log.Read(child, limit: 64, cancellationToken: Ct).Select(item => item.Payload).OfType<WorkspaceBound>());
        Assert.StartsWith(GitWorktrees.BranchPrefix + "child-", bound.Branch, StringComparison.Ordinal);
        Assert.Equal(bound.Path, runtime.WorkspaceOf(child).Path);
        Assert.True(runtime.StopChild(child));
        await delivery.WaitAsync(Wait, Ct);

        // The parent's call is over, but the child's tool still runs: its place is taken, so the one place is full.
        var plan = await runtime.PlanChildAsync(parent.Id, new ChildRequest("another"), Ct);
        Assert.Throws<ChildRefusedException>(() => runtime.OpenChildSession(plan));

        gate.SetResult();
        await runtime.ChildReleasesIdleAsync().WaitAsync(Wait, Ct);

        // The tool returned and the child went idle: its place is free, and its worktree is still there for the person.
        runtime.OpenChildSession(await runtime.PlanChildAsync(parent.Id, new ChildRequest("another"), Ct));
        Assert.True(Directory.Exists(bound.Path));
        Assert.Contains(bound.Branch!, Git(work, "branch", "--list"), StringComparison.Ordinal);
    }
}
