using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Turns;
using Corvid.Kernel.Workspaces;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// A6 (G-4): git worktrees per session with the real git CLI: a session in a new worktree, <c>/workspace</c>, and
/// <c>delegate_task</c>'s <c>isolation: worktree</c>. Each creation and removal asks the person whatever the mode says.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class WorktreeSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers every prompt (approving unless told to deny worktrees) and keeps it.</summary>
    private sealed class Recording(bool worktrees = true) : IApprovalPrompter
    {
        public ConcurrentQueue<ApprovalPrompt> Prompts { get; } = new();

        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            var worktree = prompt.Reasons.Any(reason => reason.Code == ApprovalReasons.Required);
            return ValueTask.FromResult(!worktree || worktrees ? ApprovalAnswer.Approve() : ApprovalAnswer.Deny());
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

    private static TestHost Host(FakeModels models, bool repository = true)
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "git"))))
            Assert.Skip("git is not installed");
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512, tools: ToolCallSupport.None);
        var host = TestHost.Create(models, new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:Tools:Delegate"] = "true",
            ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
            ["Corvid:Sessions:WorktreesDirectory"] = "{root}/worktrees",
        });
        var work = Path.Combine(host.Root, "work");
        if (repository)
        {
            Git(work, "init", "-q");
            File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
            Git(work, "add", ".");
            Git(work, "commit", "-q", "-m", "first");
        }
        return host;
    }

    private static IReadOnlyList<SessionEvent> Events(CorvidRuntime runtime, string sessionId) =>
        [.. runtime.Log.Read(sessionId, limit: 4096, cancellationToken: Ct).Select(item => item.Payload)];

    [Fact]
    public async Task A_session_in_a_new_worktree_works_there_after_the_person_approves_it()
    {
        await using var host = Host(new FakeModels(new FakeGenerator(
            Reply.Call("write_file", """{"path":"notes.txt","content":"in the worktree"}"""), Reply.Say("Wrote it."))));
        var work = Path.Combine(host.Root, "work");
        await using var runtime = await host.StartAsync();
        var asking = runtime.OpenSession(surface: "test");
        var prompts = new Recording();
        using var attached = runtime.AttachPrompter(asking.Id, prompts);

        var session = await runtime.OpenWorktreeSessionAsync(asking.Id, "fix-1", "test", Ct);

        // It asked once, in the asking session, as a forced prompt (offered once only) naming where it writes.
        var prompt = Assert.Single(prompts.Prompts);
        Assert.Equal(asking.Id, prompt.SessionId);
        Assert.Contains(prompt.Reasons, reason => reason.Code == ApprovalReasons.Required && reason.Detail.Contains(Path.Combine(host.Root, "worktrees"), StringComparison.Ordinal));
        Assert.Equal([ApprovalScope.Once], prompt.Offered);
        Assert.Contains(Events(runtime, asking.Id), item => item is ApprovalResolved { Approved: true });
        var bound = Assert.IsType<WorkspaceBound>(Events(runtime, session.Id)[0]);
        Assert.Equal((work, "corvid/fix-1"), (bound.Repository, bound.Branch));
        Assert.StartsWith(Path.Combine(host.Root, "worktrees"), session.Workspace.Root, StringComparison.Ordinal);
        Assert.Equal("main", Git(work, "branch", "--show-current").Trim());

        using (runtime.AttachPrompter(session.Id, prompts))
            await session.SendAsync("write the notes", Ct);

        Assert.Equal("in the worktree", await File.ReadAllTextAsync(Path.Combine(session.Workspace.Root, "notes.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(work, "notes.txt")));
        var listed = Assert.Single(await runtime.ListWorktreesAsync(asking.Id, Ct));
        Assert.Equal("fix-1", listed.Worktree.Name);
        Assert.Equal([session.Id], listed.Sessions);
    }

    [Fact]
    public async Task A_denied_worktree_is_not_made_and_nobody_to_ask_is_a_refusal()
    {
        await using var host = Host(new FakeModels());
        await using var runtime = await host.StartAsync();
        var asking = runtime.OpenSession(surface: "test");

        // No surface attached: nobody can be asked.
        await Assert.ThrowsAsync<WorktreeRefusedException>(() => runtime.CreateWorktreeAsync(asking.Id, "nobody", cancellationToken: Ct));
        using (runtime.AttachPrompter(asking.Id, new Recording(worktrees: false)))
            await Assert.ThrowsAsync<WorktreeRefusedException>(() => runtime.CreateWorktreeAsync(asking.Id, "denied", cancellationToken: Ct));

        Assert.False(Directory.Exists(Path.Combine(host.Root, "worktrees")) && Directory.EnumerateDirectories(Path.Combine(host.Root, "worktrees"),
            "*", SearchOption.AllDirectories).Any(directory => Path.GetFileName(directory) is "nobody" or "denied"));
        Assert.Empty(await runtime.ListWorktreesAsync(asking.Id, Ct));
        Assert.DoesNotContain("corvid/", Git(Path.Combine(host.Root, "work"), "branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_asks_and_is_refused_while_a_session_works_there()
    {
        await using var host = Host(new FakeModels());
        await using var runtime = await host.StartAsync();
        var asking = runtime.OpenSession(surface: "test");
        var prompts = new Recording();
        using var attached = runtime.AttachPrompter(asking.Id, prompts);
        var session = await runtime.OpenWorktreeSessionAsync(asking.Id, "done", "test", Ct);
        var path = session.Workspace.Root;

        // A surface that shows the session keeps it, so the worktree stays.
        var viewer = runtime.AddViewer(id => id == session.Id);
        var busy = await Assert.ThrowsAsync<WorktreeRefusedException>(() => runtime.RemoveWorktreeAsync(asking.Id, "done", Ct));
        Assert.Contains(session.Id, busy.Message, StringComparison.Ordinal);
        Assert.True(runtime.TryGetSession(session.Id, out _));
        Assert.True(Directory.Exists(path));

        // Once nothing shows it, the idle session held here (as the window's Board leaves one) is let go of first.
        viewer.Dispose();
        var removed = await runtime.RemoveWorktreeAsync(asking.Id, "done", Ct);
        Assert.False(runtime.TryGetSession(session.Id, out _));

        Assert.Equal(2, prompts.Prompts.Count);
        Assert.False(Directory.Exists(path));
        Assert.Equal("corvid/done", removed.Branch);
        Assert.Contains("corvid/done", Git(Path.Combine(host.Root, "work"), "branch", "--list"), StringComparison.Ordinal);
        // The session that worked there is refused from now on, never moved to another directory.
        Assert.Throws<WorkspaceUnavailableException>(() => runtime.OpenSession(session.Id, "test"));
    }

    [Fact]
    public async Task The_workspace_command_shows_lists_makes_and_removes()
    {
        await using var host = Host(new FakeModels());
        var other = Directory.CreateDirectory(Path.Combine(host.Root, "elsewhere")).FullName;
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        using var attached = runtime.AttachPrompter(session.Id, new Recording());
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        Task<CommandOutput> Run(string line) => commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct).AsTask();

        var shown = await Run("/workspace");
        Assert.Contains($"This session works in {Path.Combine(host.Root, "work")} (the configured workspace).", shown.Lines);
        var opened = await Run($"/workspace open {other}");
        Assert.Equal(OutputTone.Success, opened.Tone);
        Assert.Contains($"in {other}.", opened.Lines[0], StringComparison.Ordinal);
        Assert.Contains("corvid chat --session", opened.Lines[1], StringComparison.Ordinal);
        var made = await Run("/workspace new topic");
        Assert.Equal(OutputTone.Success, made.Tone);
        Assert.Contains("branch corvid/topic", made.Lines[0], StringComparison.Ordinal);
        var listed = await Run("/workspace list");
        Assert.Equal("topic", Assert.Single(listed.Table!.Rows)[0]);
        Assert.Equal(OutputTone.Success, (await Run("/workspace remove topic")).Tone);
        Assert.Equal(OutputTone.Error, (await Run("/workspace remove topic")).Tone);
        Assert.Equal(OutputTone.Error, (await Run("/workspace open relative/path")).Tone);
        Assert.Equal(OutputTone.Error, (await Run("/workspace new Not_Valid")).Tone);
        Assert.Contains("workspace:", (await Run("/status")).Lines.First(line => line.StartsWith("workspace:", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_asking_for_isolation_works_in_a_worktree_of_its_own()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, JsonSerializer.Serialize(new { goal = "write the notes", tools = new[] { "write_file" }, isolation = "worktree" })),
            // The child: one write, then its answer.
            Reply.Call("write_file", """{"path":"notes.txt","content":"from the child"}"""),
            Reply.Say("Written."),
            Reply.Say("The helper wrote them.")));
        await using var host = Host(models);
        var work = Path.Combine(host.Root, "work");
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession(surface: "test");
        var prompts = new Recording();
        using var attached = runtime.AttachPrompter(parent.Id, prompts);

        var delivery = await parent.SendAsync("write the notes with a helper", Ct);

        Assert.Equal("The helper wrote them.", delivery.Result!.Text);
        var spawned = Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>());
        var bound = Assert.Single(Events(runtime, spawned.ChildId).OfType<WorkspaceBound>());
        Assert.StartsWith(GitWorktrees.BranchPrefix + "child-", bound.Branch, StringComparison.Ordinal);
        Assert.Equal("from the child", await File.ReadAllTextAsync(Path.Combine(bound.Path, "notes.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(work, "notes.txt")));
        // The spawn, the worktree (forced, in the parent with the call's id) and the child's write each asked.
        var worktree = Assert.Single(prompts.Prompts, prompt => prompt.Reasons.Any(reason => reason.Code == ApprovalReasons.Required));
        Assert.Equal((parent.Id, spawned.CallId), (worktree.SessionId, worktree.CallId));
        Assert.Contains(prompts.Prompts, prompt => prompt.Action is ToolAction { Tool.Name: "write_file" });
        // A6 review (M3): the child left work there, so its worktree stays and the parent model is told where.
        var result = Events(runtime, parent.Id).OfType<ToolResult>().Last().Result;
        Assert.Equal(bound.Path, result.GetProperty("worktree").GetString());
        Assert.Equal(bound.Branch, result.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task A_child_is_refused_a_worktree_the_person_denies_or_one_outside_a_repository()
    {
        var call = JsonSerializer.Serialize(new { goal = "write the notes", tools = new[] { "write_file" }, isolation = "worktree" });
        await using (var host = Host(new FakeModels(new FakeGenerator(Reply.Call(DelegateTaskTool.ToolName, call), Reply.Say("No helper, then.")))))
        await using (var runtime = await host.StartAsync())
        {
            var parent = runtime.OpenSession(surface: "test");
            using var attached = runtime.AttachPrompter(parent.Id, new Recording(worktrees: false));
            await parent.SendAsync("write the notes with a helper", Ct);
            var result = Events(runtime, parent.Id).OfType<ToolResult>().Last();
            Assert.True(result.IsError);
            Assert.Contains("No worktree for the child", result.Result.GetRawText(), StringComparison.Ordinal);
            Assert.Empty(Events(runtime, parent.Id).OfType<ChildSpawned>());
        }

        await using var plain = Host(new FakeModels(), repository: false);
        await using var bare = await plain.StartAsync();
        var session = bare.OpenSession(surface: "test");
        var previews = await bare.PreviewDelegationAsync(session.Id, JsonDocument.Parse(call).RootElement, Ct);
        Assert.Contains("cannot have a worktree", Assert.Single(previews).Refusal, StringComparison.Ordinal);
    }
}
