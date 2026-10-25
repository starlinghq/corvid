using System.Globalization;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Fabric;
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
            "memory_search", "memory_add", "memory_pending", "memory_approve", "memory_reject", "cron_create", "cron_list", "cron_pause",
            "cron_resume", "cron_remove",
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
    public async Task CorvidsOwnStateInsideTheWorkspaceIsProtected()
    {
        // A workspace that holds Corvid's data directory, rules file and configuration (the home directory, say): no tool
        // may reach the job store, the event log or the approval rules.
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_files", """{"path":"state"}"""),
            Reply.Say("Done.")));
        await using var host = TestHost.Create(models, new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:DataDirectory"] = "{root}/work/state",
            ["Corvid:Approvals:RulesFile"] = "{root}/work/rules.json",
        });
        var work = Path.Combine(host.Root, "work");
        await using var runtime = await host.StartAsync();

        var safety = runtime.Builtin.Environment.Protected.SafetyConfiguration;
        Assert.Contains(Path.Combine(work, "state"), safety);
        Assert.Contains(Path.Combine(work, "rules.json"), safety);
        Assert.Contains(Path.Combine(host.Root, "corvid.json"), safety);
        // The workspace itself is never protected, or nothing in it could be touched.
        Assert.DoesNotContain(work, safety);

        Assert.True(runtime.Tools.TryGet("shell", out var shell));
        foreach (var command in new[] { "rm state/jobs.db", "touch rules.json" })
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
            var state = new TurnStateBuilder().Build(new TurnStateInput("tidy up", "cli", "fake"));
            var assessment = await runtime.Approvals.AssessAsync(new ApprovalContext("s", ApprovalMode.Permissive, state),
                new ToolAction(shell.Descriptor, arguments.RootElement.Clone()), TestContext.Current.CancellationToken);
            Assert.True(assessment.Hardline, command);
        }

        var session = runtime.OpenSession();
        await session.SendAsync("list the files in state", TestContext.Current.CancellationToken);
        var result = Assert.Single(session.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.Contains("protected location", result.Result.GetRawText(), StringComparison.Ordinal);
    }

    // A5 (A4 leftover): Sandbox:LauncherPath naming the real corvid-sandbox put the launcher on the sandbox's deny list, and
    // its stage 2, which re-executes the launcher inside bubblewrap, failed every command. It stays protected from the
    // tools' writes; the shell and sandboxed MCP servers no longer hide it.
    [Fact]
    public async Task TheConfiguredLauncherIsProtectedButNeverHiddenFromItsOwnSandbox()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:Sandbox:LauncherPath"] = "{root}/bin/corvid-sandbox",
        });
        var launcher = Path.Combine(host.Root, "bin", "corvid-sandbox");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        await File.WriteAllTextAsync(launcher, "", TestContext.Current.CancellationToken);
        await using var runtime = await host.StartAsync();

        Assert.Contains(launcher, runtime.Builtin.Environment.Protected.SafetyConfiguration);
        var options = runtime.Options;
        var denied = CorvidRuntimeFactory.McpDeniedPaths(options, Path.Combine(host.Root, "corvid.json")).ToList();
        Assert.DoesNotContain(launcher, denied);
        Assert.Contains(Path.Combine(host.Root, "corvid.json"), denied);
        Assert.DoesNotContain(launcher, CorvidRuntimeFactory.WithoutLauncher(runtime.Builtin.Environment.Protected.SafetyConfiguration, options));
        // Outside the workspace it is read-only to commands already; inside it, it is bound read-only.
        Assert.Empty(CorvidRuntimeFactory.LauncherInside(options, Path.Combine(host.Root, "work")));
        Assert.Equal([launcher], CorvidRuntimeFactory.LauncherInside(options, host.Root));
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

        // "list" is one of the fake embedder's keywords: a message with none ties every tool, and the shortlist then takes the
        // first five by name, which since C-041's cron_pause and cron_resume are all cron tools.
        var delivery = await session.SendAsync("list the files in this folder", TestContext.Current.CancellationToken);

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
        var store = new JobStore(Path.Combine(host.Root, "data", "jobs.db"));
        var job = store.Add(new NewJob("greeting", "say hello", at));
        await using var first = await host.StartAsync();
        // Subscribed at once (C-039): the scheduler starts with the runtime, and the job is due in two or three seconds.
        var ran = new TaskCompletionSource<JobRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.JobCompleted += (_, run) => ran.TrySetResult(run);
        Assert.Equal("running in this process", first.SchedulerState);
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using (var second = await factory.StartAsync(TestContext.Current.CancellationToken))
            Assert.Equal("run by another corvid process", second.SchedulerState);

        var run = await ran.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(JobRunStatus.Ran, run.Status);
        // The store recorded the same run, finished, before Completed was raised.
        var recorded = Assert.Single(store.Runs(job.JobId));
        Assert.Equal(run.SessionId, recorded.SessionId);
        Assert.NotNull(recorded.FinishedAt);
        // The run's turn is in the shared log under its own session.
        Assert.Contains(first.Log.Read(run.SessionId, cancellationToken: TestContext.Current.CancellationToken),
            item => item.Payload is AssistantMessage);
    }

    [Fact]
    public async Task AHandlerAddedAfterARunStillHearsOfIt()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Scheduler:Enabled"] = "true" });
        var at = DateTimeOffset.Now.AddSeconds(3).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var store = new JobStore(Path.Combine(host.Root, "data", "jobs.db"));
        var job = store.Add(new NewJob("greeting", "say hello", at));
        var heard = new List<JobRun>();
        var runtime = await host.StartAsync();
        try
        {
            // A surface that subscribes late, as chat does after opening its session: the run is already recorded.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (store.Runs(job.JobId) is not [{ FinishedAt: not null }])
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The job did not run within 60 s.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.JobCompleted += (_, run) =>
            {
                lock (heard)
                    heard.Add(run);
                first.TrySetResult();
            };
            // Replayed at once if Completed already fired, or live if it fires between Finish and the replay buffer.
            await first.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        finally
        {
            // Disposing waits for the scheduler, so a duplicate, had there been one, has arrived by now.
            await runtime.DisposeAsync();
        }
        var only = Assert.Single(heard);
        Assert.Equal(Assert.Single(store.Runs(job.JobId)).RunId, only.RunId);
        Assert.Equal(job.JobId, only.JobId);
    }

    private static readonly Dictionary<string, string?> SchedulerOn = new() { ["Corvid:Scheduler:Enabled"] = "true" };

    private static string InSeconds(int seconds) =>
        DateTimeOffset.Now.AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AJobOverdueBeforeStartRunsWithoutWaitingAPollInterval()
    {
        // C-041's catch-up policy at start: a job that fell due while no corvid process ran runs on the scheduler's first
        // pass, before its first sleep, and chat hears of it through C-039's replay.
        await using var host = TestHost.Create(settings: SchedulerOn);
        var store = new JobStore(Path.Combine(host.Root, "data", "jobs.db"));
        var job = store.Add(new NewJob("missed", "say hello", InSeconds(1)));
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await using var runtime = await host.StartAsync();
        var ran = new TaskCompletionSource<JobRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.JobCompleted += (_, run) => ran.TrySetResult(run);

        // Well inside the 30 s poll interval.
        var run = await ran.Task.WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        Assert.Equal(job.JobId, run.JobId);
        Assert.Equal(job.NextRunAt, run.ScheduledFor);
    }

    [Fact]
    public async Task AJobCreatedThroughTheRuntimeWakesItsScheduler()
    {
        await using var host = TestHost.Create(settings: SchedulerOn);
        await using var runtime = await host.StartAsync();
        var ran = new TaskCompletionSource<JobRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.JobCompleted += (_, run) => ran.TrySetResult(run);
        // Let the first pass find nothing and sleep its whole poll interval (30 s).
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // The store cron_create, /cron and the scheduler share: Add raises Changed, and the scheduler recomputes its wait.
        var job = runtime.Builtin.Jobs.Add(new NewJob("soon", "say hello", InSeconds(2)));

        var run = await ran.Task.WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        Assert.Equal(job.JobId, run.JobId);
    }

    [Fact]
    public async Task ASessionOpenTakesTheSchedulerOverOnceItsHolderHasExited()
    {
        await using var host = TestHost.Create(settings: SchedulerOn);
        var first = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var second = await factory.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("running in this process", first.SchedulerState);
            Assert.Equal("run by another corvid process", second.SchedulerState);
            // While the first holds the lock, a session open in the second changes nothing.
            second.OpenSession();
            Assert.Equal("run by another corvid process", second.SchedulerState);
        }
        finally
        {
            // The chat that held the scheduler exits, releasing the lock.
            await first.DisposeAsync();
        }

        var store = new JobStore(Path.Combine(host.Root, "data", "jobs.db"));
        var job = store.Add(new NewJob("missed", "say hello", InSeconds(1)));
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var ran = new TaskCompletionSource<JobRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.JobCompleted += (_, run) => ran.TrySetResult(run);

        // A long-lived process (the gateway, ACP) opens a session for the next message: it takes the scheduler over.
        second.OpenSession();
        Assert.Equal("running in this process", second.SchedulerState);

        var run = await ran.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(job.JobId, run.JobId);
        Assert.Equal(JobRunStatus.Ran, run.Status);
        // A later open only wakes the scheduler it now holds.
        second.OpenSession();
        Assert.Equal("running in this process", second.SchedulerState);
    }

    [Fact]
    public async Task TheSchedulerCanBeTurnedOff()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        Assert.StartsWith("off", runtime.SchedulerState, StringComparison.Ordinal);
    }
}
