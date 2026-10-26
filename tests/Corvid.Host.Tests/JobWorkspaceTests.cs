using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
using Corvid.Models;

namespace Corvid.Host.Tests;

/// <summary>
/// A6 (G-4): a scheduled job runs in the workspace of the session that created it, never in the configured one instead,
/// and a run whose workspace is gone is refused.
/// </summary>
public sealed class JobWorkspaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> FileTools = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
    };

    private static FakeModels Reads()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("read_file", """{"path":"where.txt"}"""), Reply.Say("Read it.")));
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("look", "text file"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    private static (string Other, string Home) Workspaces(TestHost host)
    {
        var other = Path.Combine(host.Root, "other");
        var home = Path.Combine(host.Root, "work");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(other, "where.txt"), "in the other workspace");
        File.WriteAllText(Path.Combine(home, "where.txt"), "in the configured workspace");
        return (other, home);
    }

    private static IReadOnlyList<object> Events(CorvidRuntime runtime, string session) =>
        [.. runtime.Log.Read(session, 0, long.MaxValue, 256, Ct).Select(stored => stored.Payload)];

    [Fact]
    public async Task A_job_made_in_another_workspace_runs_its_turn_there()
    {
        await using var host = TestHost.Create(Reads(), FileTools);
        var (other, _) = Workspaces(host);
        await using var runtime = await host.StartAsync();
        var creator = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other });
        Assert.Equal(other, runtime.WorkspaceOf(creator.Id).Path);
        var job = runtime.Builtin.Jobs.Add(new NewJob("check", "look at the text file where.txt", "in 1h", OriginSession: creator.Id));

        const string run = "job-run-elsewhere";
        await runtime.JobAgent().RunAsync(new JobAgentRequest(run, job, job.Prompt, Tainted: false), Ct);

        var result = Assert.Single(Events(runtime, run).OfType<ToolResult>());
        Assert.False(result.IsError);
        Assert.Contains("in the other workspace", result.Result.GetRawText(), StringComparison.Ordinal);
        // The run's session records the workspace, so its Review, Rewind and approvals are that workspace's too.
        Assert.Equal(other, Assert.IsType<WorkspaceBound>(Events(runtime, run)[0]).Path);
        Assert.Equal(other, runtime.WorkspaceServicesOf(run).Root);
    }

    [Fact]
    public async Task A_job_with_no_workspace_of_its_own_runs_in_the_configured_one()
    {
        await using var host = TestHost.Create(Reads(), FileTools);
        var (_, home) = Workspaces(host);
        await using var runtime = await host.StartAsync();
        var job = runtime.Builtin.Jobs.Add(new NewJob("check", "look at the text file where.txt", "in 1h"));

        const string run = "job-run-home";
        await runtime.JobAgent().RunAsync(new JobAgentRequest(run, job, job.Prompt, Tainted: false), Ct);

        var result = Assert.Single(Events(runtime, run).OfType<ToolResult>());
        Assert.Contains("in the configured workspace", result.Result.GetRawText(), StringComparison.Ordinal);
        // A6 review: the run records the configured workspace, so it is resumed there from anywhere.
        Assert.Equal(runtime.DefaultWorkspace.Root, Assert.IsType<WorkspaceBound>(Events(runtime, run)[0]).Path);
        Assert.Same(runtime.DefaultWorkspace, runtime.WorkspaceServicesOf(run));
        Assert.Equal(runtime.DefaultWorkspace.Root, Path.GetFullPath(home));
    }

    [Fact]
    public async Task A_job_whose_workspace_is_gone_is_refused_and_nothing_runs_elsewhere()
    {
        await using var host = TestHost.Create(Reads(), FileTools);
        var (other, _) = Workspaces(host);
        await using var runtime = await host.StartAsync();
        var creator = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other });
        Assert.Equal(other, runtime.WorkspaceOf(creator.Id).Path);
        var job = runtime.Builtin.Jobs.Add(new NewJob("check", "look at the text file where.txt", "in 1h", "cat where.txt", OriginSession: creator.Id));
        Directory.Delete(other, recursive: true);

        var script = await runtime.JobScripts().RunAsync(new JobScriptRequest("job-run-gone", job.JobId, "cat where.txt", default!), Ct);
        var refused = await Assert.ThrowsAsync<WorkspaceUnavailableException>(() =>
            runtime.JobAgent().RunAsync(new JobAgentRequest("job-run-gone", job, job.Prompt, Tainted: false), Ct));

        Assert.False(script.Ran);
        Assert.Contains(other, script.Refusal, StringComparison.Ordinal);
        Assert.Contains(other, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Events(runtime, "job-run-gone"), payload => payload is ToolResult);
    }
}
