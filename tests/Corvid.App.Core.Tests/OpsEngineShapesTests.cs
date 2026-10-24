using Corvid.App.Core.Gateway;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Secrets;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// A3 ops: the shapes the engine gives that the demo never does, and how the pages say them: a scheduler off in this
/// process for a reason, an adapter whose state this app cannot see, and secrets whose policy Vault did not report.
/// </summary>
public sealed class OpsEngineShapesTests
{
    private static string Text(IEnumerable<Models.TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    [Fact]
    public void ASchedulerOffHereSaysWhyRatherThanThatNoProcessRunsIt()
    {
        var time = ManualTimeProvider.Demo();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        shell.Navigate(Area.Jobs);
        using var jobs = new JobsViewModel(new Jobs(new SchedulerStatus(SchedulerHost.Off, "This app does not run the scheduler: Corvid:Scheduler:Enabled is off")),
            shell, bar, time);

        Assert.Equal("off here", jobs.SchedulerTag);
        Assert.Equal(Tone.Grey, jobs.SchedulerTone);
        Assert.StartsWith("This app does not run the scheduler: Corvid:Scheduler:Enabled is off. Jobs run only in a process that runs the scheduler",
            Text(jobs.SchedulerText), StringComparison.Ordinal);

        // The demo's off scheduler (no holder) keeps its sentence.
        using var demo = new JobsViewModel(new DemoJobsService(time, SchedulerHost.Off), shell, bar, time);
        Assert.Equal("off", demo.SchedulerTag);
        Assert.StartsWith("No process runs the scheduler", Text(demo.SchedulerText), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobCreatedOnAnEngineIsSelectedWhenItsChangeArrivesAfterTheCreateReturns()
    {
        var time = ManualTimeProvider.Demo();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        shell.Navigate(Area.Jobs);
        var service = new LateJobs();
        using var jobs = new JobsViewModel(service, shell, bar, time);
        jobs.NewJobCommand.Execute(null);
        jobs.DraftName = "digest";
        jobs.DraftSchedule = "in 30m";
        jobs.DraftPrompt = "Summarise the inbox.";

        await jobs.CreateCommand.ExecuteAsync();
        Assert.Equal("j-0", Assert.Single(jobs.Jobs).Id);
        Assert.Equal("j-0", jobs.Selected?.Id);

        // The engine's change is posted after the create returned: the new job is selected then.
        service.Raise();
        Assert.Equal("j-1", jobs.Selected?.Id);
    }

    [Fact]
    public void AnAdapterThisAppDoesNotHostIsNotCalledOffOrUp()
    {
        Assert.Equal("not hosted here", GatewayText.State(AdapterState.Unknown));
        Assert.Equal(Tone.Grey, GatewayText.StateTone(AdapterState.Unknown));
        Assert.Equal("telegram not hosted here · slack off",
            GatewayText.Summary([new GatewayAdapter("telegram", AdapterState.Unknown), new GatewayAdapter("slack", AdapterState.Off)]));
    }

    [Fact]
    public async Task ASecretWhosePolicyVaultDidNotReportSaysSoAndTheAuditSummaryReplacesTodaysCounts()
    {
        var service = new Secrets(new SecretsSnapshot(
            new VaultStatus(VaultState.Running) { Version = "not reported", Socket = "/run/vault.sock", AuditSummary = "since Vault's audit log began: 4 requests sent" },
            [new SecretInfo("telegram.bot-token") { Hosts = ["api.telegram.org"], PolicyReported = false }],
            [new SecretConsumer("gateway:telegram", ConsumerKind.Gateway, "telegram", "gateway adapter", "telegram.bot-token", "api.telegram.org")]));
        using var secrets = new SecretsViewModel(service, ManualTimeProvider.DemoLocal());
        await secrets.Loaded;

        var row = Assert.Single(secrets.Secrets);
        Assert.Equal(["policy not reported"], row.Methods.Select(tag => tag.Text));
        Assert.Equal(SecretRowView.NotReported, row.Methods[0].Detail);
        Assert.Equal(("any toolset", "budget not reported", "last use not reported", "in a tainted turn: not reported"),
            (row.Use, row.Budget, row.LastUsed, row.TaintedUse));
        Assert.False(row.HasBudget);
        var audit = secrets.AuditFacts[0];
        Assert.Equal("audit", audit.Key);
        Assert.Equal("since Vault's audit log began: 4 requests sent", Text(audit.Value));
        // The feature is ready: the name is in Vault and bound to its host.
        Assert.Equal("ready", Assert.Single(secrets.Consumers).State);
    }

    private sealed class Jobs(SchedulerStatus scheduler) : IJobsService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new JobsSnapshot(scheduler, [], ["kept"]));

        public Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobRunRecord>>([]);

        public Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken) => Task.FromResult(SchedulePreview.Invalid("no"));

        public Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A jobs service whose change, like an engine's, reaches the page only when it is posted later (<see cref="Raise"/>).</summary>
    private sealed class LateJobs : IJobsService
    {
        private readonly List<JobRecord> _jobs = [new("j-0", "tidy", "daily at 9am", JobState.Active) { Prompt = "Tidy." }];

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JobsSnapshot(new SchedulerStatus(SchedulerHost.Here, "this app"), [.. _jobs], ["kept"]));

        public Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobRunRecord>>([]);

        public Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken) =>
            Task.FromResult(new SchedulePreview(true, "once", [DateTimeOffset.UnixEpoch]));

        public Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken)
        {
            var job = new JobRecord("j-1", draft.Name, draft.Schedule, JobState.Active) { Prompt = draft.Prompt };
            _jobs.Add(job);
            return Task.FromResult(job);
        }

        public Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Secrets(SecretsSnapshot snapshot) : ISecretsService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
