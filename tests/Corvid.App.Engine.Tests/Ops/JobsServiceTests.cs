using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Jobs;
using Corvid.Kernel.Jobs;
using CoreJobState = Corvid.App.Core.Jobs.JobState;
using CoreRunStatus = Corvid.App.Core.Jobs.JobRunStatus;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>The Jobs page over the runtime's job store and scheduler (C-012, C-041, G-13), on a test host's own store.</summary>
public sealed class JobsServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> SchedulerOn = new() { ["Corvid:Scheduler:Enabled"] = "true" };

    [Fact]
    public async Task AJobTheModelMadeLoadsWithItsScheduleOriginAndLastRun()
    {
        await using var fixture = await OpsFixture.StartAsync();
        var store = fixture.Runtime.Builtin.Jobs;
        var made = store.Add(new NewJob("nightly-tidy", "Tidy the workspace.", "daily at 9am", OriginSession: "cli-9f2e", CreatedTainted: true));
        // One due run, claimed and finished as the scheduler would.
        var claimed = Assert.Single(store.ClaimDue(made.NextRunAt!.Value));
        store.Finish(claimed.Run.RunId, Corvid.Kernel.Jobs.JobRunStatus.Ran, claimed.Run.StartedAt.AddSeconds(41),
            output: "3 merged branches\nand 12 old TODOs", detail: "completed");
        using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);

        // The test host turns the scheduler off: the page says why this app does not run it.
        Assert.Equal(SchedulerHost.Off, snapshot.Scheduler.Host);
        Assert.Equal("This app does not run the scheduler: Corvid:Scheduler:Enabled is off", snapshot.Scheduler.Holder);
        Assert.Equal([EngineJobsService.KeptInSession], snapshot.Destinations);
        var job = Assert.Single(snapshot.Jobs);
        Assert.Equal((made.JobId, "nightly-tidy", "daily at 9am", CoreJobState.Active), (job.Id, job.Name, job.Schedule, job.State));
        Assert.Equal("cron 0 9 * * *", job.Parsed);
        Assert.Equal(store.TimeZone.Id, job.Zone);
        Assert.True(job.CreatedTainted);
        Assert.StartsWith("cli-9f2e through cron_create, on ", job.CreatedBy, StringComparison.Ordinal);
        Assert.Equal(EngineJobsService.KeptInSession, job.Deliver);
        Assert.Equal(store.Get(made.JobId)!.NextRunAt, job.NextRun);
        var last = Assert.IsType<JobRunRecord>(job.LastRun);
        Assert.Equal((claimed.Run.RunId, CoreRunStatus.Ok, claimed.Run.SessionId, "completed: 3 merged branches and 12 old TODOs"),
            (last.RunId, last.Status, last.SessionId, last.Detail));
        Assert.Equal(claimed.Run.StartedAt.AddSeconds(41), last.FinishedAt);

        var runs = await service.RunsAsync(made.JobId, Token);
        Assert.Equal(claimed.Run.RunId, Assert.Single(runs).RunId);
        Assert.Empty(await service.RunsAsync("job-none", Token));
    }

    [Fact]
    public void RunStatusesMapToThePagesAndSayWhatTheStoreSaysBeyondOkOrFailed()
    {
        var at = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
        JobRun Run(string status, string? detail = null, string? output = null) =>
            new("run-1", "job-1", "job:job-1:run-1", at, at, at.AddSeconds(3), status, null, null, output, detail);

        Assert.Equal((CoreRunStatus.Running, ""), Map(Run(Corvid.Kernel.Jobs.JobRunStatus.Running)));
        Assert.Equal((CoreRunStatus.Ok, "script only: 4 packages behind"), Map(Run(Corvid.Kernel.Jobs.JobRunStatus.ScriptOnly, output: "4 packages\nbehind")));
        Assert.Equal((CoreRunStatus.Skipped, "wake: skip (nothing new)"), Map(Run(Corvid.Kernel.Jobs.JobRunStatus.Skipped, "wake: skip (nothing new)")));
        Assert.Equal((CoreRunStatus.Failed, "script refused: network egress asks"),
            Map(Run(Corvid.Kernel.Jobs.JobRunStatus.ScriptRefused, "network egress asks")));
        Assert.Equal((CoreRunStatus.Failed, "interrupted: Corvid stopped during the run"),
            Map(Run(Corvid.Kernel.Jobs.JobRunStatus.Interrupted, "Corvid stopped during the run")));
        Assert.Equal((CoreRunStatus.Failed, "the generator timed out"), Map(Run(Corvid.Kernel.Jobs.JobRunStatus.Failed, "the generator timed out")));
        // A run that answered: its detail is the turn's status, and its answer says what it did.
        Assert.Equal((CoreRunStatus.Ok, "answered: Hello there!"), Map(Run(Corvid.Kernel.Jobs.JobRunStatus.Ran, "answered", "Hello\nthere!")));
        var long_ = Map(Run(Corvid.Kernel.Jobs.JobRunStatus.Ran, new string('x', 400))).Detail;
        Assert.Equal(160, long_.Length);
        Assert.EndsWith("…", long_, StringComparison.Ordinal);

        static (CoreRunStatus Status, string Detail) Map(JobRun run)
        {
            var record = EngineJobsService.Run(run);
            return (record.Status, record.Detail);
        }
    }

    [Theory]
    [InlineData("once 2026-10-24T21:39:04.4654001+00:00", "once 2026-10-24 21:39 UTC")]
    [InlineData("every 1800s", "every 30m")]
    [InlineData("every 7200s", "every 2h")]
    [InlineData("every 86400s", "every 1d")]
    [InlineData("every 90s", "every 90s")]
    [InlineData("cron 0 9 * * 1-5", "cron 0 9 * * 1-5")]
    public void TheStoresScheduleReadsAsAPersonWritesIt(string canonical, string shown) =>
        Assert.Equal(shown, EngineJobsService.Readable(canonical));

    [Fact]
    public async Task ThePreviewIsTheKernelsReadingAndWritesNothing()
    {
        await using var fixture = await OpsFixture.StartAsync();
        using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var weekdays = await service.PreviewAsync("weekdays at 9am", Token);
        Assert.True(weekdays.Valid);
        Assert.Equal("cron 0 9 * * 1-5", weekdays.Parsed);
        Assert.Equal(ScheduleGrammar.Shown, weekdays.Next.Count);
        Assert.All(weekdays.Next, next => Assert.True(next > fixture.Time.GetUtcNow()));

        var once = await service.PreviewAsync("in 30m", Token);
        Assert.Equal([fixture.Time.GetUtcNow().AddMinutes(30)], once.Next);
        Assert.StartsWith("once ", once.Parsed, StringComparison.Ordinal);

        var bad = await service.PreviewAsync("whenever you like", Token);
        Assert.False(bad.Valid);
        Assert.Contains("a delay ('in 30m', '2h')", bad.Error, StringComparison.Ordinal);
        Assert.False((await service.PreviewAsync("every 10s", Token)).Valid);
        Assert.Equal("The schedule holds hidden characters; type it again.", (await service.PreviewAsync("daily at 9\u200Bam", Token)).Error);
        Assert.StartsWith("Say when it runs:", (await service.PreviewAsync("  ", Token)).Error, StringComparison.Ordinal);
        Assert.Empty(fixture.Runtime.Builtin.Jobs.List(includeFinished: true));
    }

    [Fact]
    public async Task CreatingWritesTheStoreAndRefusesWhatTheStoreOrThePageWouldNot()
    {
        await using var fixture = await OpsFixture.StartAsync();
        using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        await service.IdleAsync();
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var job = await service.CreateAsync(new JobDraft("weekly-deps", "fridays at 4pm", "Summarise the outdated packages.", "scripts/outdated.sh",
            false, EngineJobsService.KeptInSession), Token);

        Assert.Equal(CoreJobState.Active, job.State);
        Assert.Equal("cron 0 16 * * 5", job.Parsed);
        Assert.Equal("scripts/outdated.sh", job.Script);
        Assert.StartsWith("outside a session (this app or /cron), on ", job.CreatedBy, StringComparison.Ordinal);
        Assert.NotNull(job.NextRun);
        var stored = Assert.Single(fixture.Runtime.Builtin.Jobs.List());
        Assert.Equal((job.Id, "Summarise the outdated packages.", (string?)null), (stored.JobId, stored.Prompt, stored.OriginSession));
        Assert.True(changes >= 1);

        async Task<string> Refused(JobDraft draft) =>
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(draft, Token))).Message;
        Assert.Equal("There is already a job named Weekly-Deps.",
            await Refused(new JobDraft("Weekly-Deps", "daily at 9am", "Again.", null, false, "")));
        Assert.Contains("'whenever'", await Refused(new JobDraft("other", "whenever", "Do it.", null, false, "")), StringComparison.Ordinal);
        Assert.Equal("The job holds hidden characters; nothing was created.",
            await Refused(new JobDraft("other", "daily at 9am", "Do\u200B it.", null, false, "")));
        Assert.Equal("A no-agent job runs only its script; give it one.", await Refused(new JobDraft("other", "daily at 9am", "", " ", true, "")));
        Assert.Equal("Say what the job asks Corvid to do.", await Refused(new JobDraft("other", "daily at 9am", " ", null, false, "")));
        Assert.StartsWith("A job made here keeps its result in the run's session",
            await Refused(new JobDraft("other", "daily at 9am", "Do it.", null, false, "the Telegram home chat")), StringComparison.Ordinal);
        var tooLong = new string('a', EngineJobsService.MaxPromptLength + 1);
        Assert.StartsWith($"The prompt is {tooLong.Length} characters", await Refused(new JobDraft("other", "daily at 9am", tooLong, null, false, "")),
            StringComparison.Ordinal);
        Assert.StartsWith($"The script is {tooLong.Length} characters", await Refused(new JobDraft("other", "daily at 9am", "", tooLong, true, "")),
            StringComparison.Ordinal);
        Assert.Single(fixture.Runtime.Builtin.Jobs.List());

        // A no-agent job keeps no prompt.
        var script = await service.CreateAsync(new JobDraft("df", "every 2 hours", "ignored", "df -h", true, ""), Token);
        Assert.Equal(("", true, "df -h"), (script.Prompt, script.NoAgent, script.Script));
    }

    [Fact]
    public async Task PauseResumeAndRemoveAreCronsAndRefuseWhatItWouldRefuse()
    {
        await using var fixture = await OpsFixture.StartAsync();
        var store = fixture.Runtime.Builtin.Jobs;
        var made = store.Add(new NewJob("tidy", "Tidy.", "daily at 9am"));
        using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var paused = await service.PauseAsync(made.JobId, Token);
        Assert.Equal((CoreJobState.Paused, (DateTimeOffset?)null), (paused.State, paused.NextRun));
        Assert.True(store.Get(made.JobId)!.Paused);
        Assert.Equal("tidy is not an active, unpaused job.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.PauseAsync(made.JobId, Token))).Message);

        var resumed = await service.ResumeAsync(made.JobId, Token);
        Assert.Equal(CoreJobState.Active, resumed.State);
        Assert.NotNull(resumed.NextRun);
        Assert.Equal("tidy is not paused.", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeAsync(made.JobId, Token))).Message);

        await service.RemoveAsync(made.JobId, Token);
        Assert.Equal(Corvid.Kernel.Jobs.JobState.Removed, store.Get(made.JobId)!.State);
        Assert.Empty((await service.LoadAsync(Token)).Jobs);
        Assert.Equal("tidy was already removed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(made.JobId, Token))).Message);
        Assert.Equal("There is no job job-nothing.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.PauseAsync("job-nothing", Token))).Message);
    }

    [Fact]
    public async Task AJobWhosePromptHidesCharactersIsNotResumedFromHere()
    {
        await using var fixture = await OpsFixture.StartAsync();
        var store = fixture.Runtime.Builtin.Jobs;
        // As cron_create makes it from a chat: unchecked.
        var made = store.Add(new NewJob("sneaky", "Summarise\u200B the inbox.", "daily at 9am", OriginSession: "cli-77b0"));
        store.Pause(made.JobId);
        using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeAsync(made.JobId, Token));
        Assert.Equal("sneaky's prompt holds hidden characters; it cannot be resumed.", refusal.Message);
        Assert.True(store.Get(made.JobId)!.Paused);
    }

    [Fact]
    public async Task AChangeInTheStoreFromASessionReachesThePageUntilDisposed()
    {
        await using var fixture = await OpsFixture.StartAsync();
        var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        await service.IdleAsync();
        var changes = 0;
        service.Changed += (_, _) => changes++;

        // cron_create in a session writes the same store instance.
        fixture.Runtime.Builtin.Jobs.Add(new NewJob("from-chat", "Hello.", "in 30m", OriginSession: "cli-1"));
        Assert.Equal(1, changes);

        service.Dispose();
        fixture.Runtime.Builtin.Jobs.Add(new NewJob("later", "Hello.", "in 30m"));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task TheSchedulerIsHereWhenThisAppHoldsItsLockAndElsewhereWhenAnotherDoes()
    {
        await using (var fixture = await OpsFixture.StartAsync(SchedulerOn))
        {
            using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);
            var here = (await service.LoadAsync(Token)).Scheduler;
            Assert.Equal((SchedulerHost.Here, "this app"), (here.Host, here.Holder));
            // G-14: since it took the lock.
            Assert.Equal(fixture.Runtime.SchedulerHolder!.Since, here.Since);
        }

        // Another process holds jobs.db.lock: this app's runtime leaves the scheduler to it.
        var data = Path.Combine(Path.GetTempPath(), "corvid-jobs-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            using (new FileStream(Path.Combine(data, "jobs.db.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await using var fixture = await OpsFixture.StartAsync(new Dictionary<string, string?>(SchedulerOn) { ["Corvid:DataDirectory"] = data });
                using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);
                var scheduler = (await service.LoadAsync(Token)).Scheduler;
                Assert.Equal(SchedulerHost.Elsewhere, scheduler.Host);
                Assert.Equal("another corvid process (it holds jobs.db.lock)", scheduler.Holder);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task TheCompositionMakesTheEnginesJobsAndARuntimeThatCannotStartIsTheNotice()
    {
        await using var fixture = await OpsFixture.StartAsync();
        using (var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time,
            new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null)))
        {
            Assert.IsType<EngineJobsService>(composition.JobsService);
            Assert.Empty((await composition.JobsService.LoadAsync(Token)).Jobs);
        }

        await using var host = EngineHosts.Create(new Corvid.Host.Tests.FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);
        using var missing = new EngineJobsService(engine, new InlineDispatcher(), fixture.Time);
        var refusal = await Assert.ThrowsAsync<ServiceUnavailableException>(() => missing.LoadAsync(Token));
        Assert.StartsWith("Corvid cannot start:", refusal.Message, StringComparison.Ordinal);
        await missing.IdleAsync();
    }
}
