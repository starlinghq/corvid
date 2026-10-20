using System.Globalization;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
using Corvid.Kernel.Turns;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

public sealed class BuiltinWiringTests
{
    private static readonly Dictionary<string, string?> BuiltinOn = new() { ["Corvid:Tools:Builtin"] = "true" };

    [Fact]
    public async Task TheBuiltInToolsAreRegisteredBesideTheOnesInDependencyInjection()
    {
        await using var host = TestHost.Create(settings: BuiltinOn);
        await using var runtime = await host.StartAsync();

        string[] builtin =
        [
            "read_file", "list_files", "search_files", "write_file", "patch_file", "shell", "web_fetch",
            "memory_search", "memory_add", "memory_pending", "memory_approve", "memory_reject", "cron_create", "cron_list", "cron_remove",
        ];
        var names = runtime.Tools.List().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(builtin, name => Assert.Contains(name, names));
        Assert.Contains("list_notes", names);
    }

    [Fact]
    public async Task TheShellIsAssessedAsItsCommandAndVaultIsProtected()
    {
        await using var host = TestHost.Create(settings: BuiltinOn);
        await using var runtime = await host.StartAsync();

        Assert.Equal("command", runtime.Approvals.Options.ShellTools["shell"]);
        Assert.True(runtime.Tools.TryGet("shell", out var shell));
        using var arguments = JsonDocument.Parse("""{"command":"ls -la"}""");
        var analysis = runtime.Approvals.Analyze(new ToolAction(shell.Descriptor, arguments.RootElement.Clone()));
        Assert.Equal("ls", Assert.Single(analysis.Commands).Verb);
        // Vault's default locations for this user are in the approval environment's protected paths.
        Assert.NotEmpty(runtime.Builtin.Environment.Protected.Vault);
        Assert.Equal(Path.Combine(host.Root, "work"), runtime.Builtin.Environment.Workspace);
    }

    [Fact]
    public async Task AFileToolRunsInTheConfiguredWorkspace()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_files", """{"path":"."}"""), Reply.Say("There is one file.")));
        await using var host = TestHost.Create(models, BuiltinOn);
        Directory.CreateDirectory(Path.Combine(host.Root, "work"));
        await File.WriteAllTextAsync(Path.Combine(host.Root, "work", "hello.txt"), "hi", TestContext.Current.CancellationToken);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        var delivery = await session.SendAsync("what files are in this folder", TestContext.Current.CancellationToken);

        Assert.Equal(TurnStatus.Answered, delivery.Result!.Status);
        var result = Assert.Single(session.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolResult>());
        Assert.False(result.IsError);
        Assert.Contains("hello.txt", result.Result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneProcessRunsTheSchedulerAndDueJobsRun()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Scheduler:Enabled"] = "true" });
        // In the store before the scheduler starts, so its first wait is until this job rather than a whole poll interval.
        var at = DateTimeOffset.Now.AddSeconds(3).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        new JobStore(Path.Combine(host.Root, "data", "jobs.db")).Add(new NewJob("greeting", "say hello", at));
        await using var first = await host.StartAsync();
        Assert.Equal("running in this process", first.SchedulerState);
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using (var second = await factory.StartAsync(TestContext.Current.CancellationToken))
            Assert.Equal("run by another corvid process", second.SchedulerState);

        var ran = new TaskCompletionSource<JobRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.JobCompleted += (_, run) => ran.TrySetResult(run);

        var run = await ran.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(JobRunStatus.Ran, run.Status);
        // The run's turn is in the shared log under its own session.
        Assert.Contains(first.Log.Read(run.SessionId, cancellationToken: TestContext.Current.CancellationToken),
            item => item.Payload is AssistantMessage);
    }

    [Fact]
    public async Task TheSchedulerCanBeTurnedOff()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        Assert.StartsWith("off", runtime.SchedulerState, StringComparison.Ordinal);
    }
}
