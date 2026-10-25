using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Jobs;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>
/// G-14 on the Jobs page: the scheduler's holder and since when, the take-over once another process lets go, the store
/// followed while another process runs the jobs, and job runs that report back to a gateway chat.
/// </summary>
public sealed class JobsHostingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> SchedulerOn = new() { ["Corvid:Scheduler:Enabled"] = "true" };

    [Fact]
    public async Task AnotherProcessHoldingTheSchedulerIsNamedAndTheAppTakesOverOnceItExits()
    {
        // "corvid chat" started first and holds jobs.db.lock; the app's runtime starts second.
        await using var host = EngineHosts.Create(settings: SchedulerOn);
        var chat = await host.StartAsync();
        var app = await host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Token);
        await using var engine = AppEngine.ForRuntime(host.Host, app);
        var time = Core.Tests.ManualTimeProvider.Demo();
        using var service = new EngineJobsService(engine, new InlineDispatcher(), time);
        try
        {
            var elsewhere = (await service.LoadAsync(Token)).Scheduler;

            Assert.Equal(SchedulerHost.Elsewhere, elsewhere.Host);
            Assert.Contains($"(pid {Environment.ProcessId}) (it holds jobs.db.lock)", elsewhere.Holder, StringComparison.Ordinal);
            Assert.Equal(chat.SchedulerHolder!.Since, elsewhere.Since);

            // The other process's scheduler changes its own store: the page hears of it at the next look, not only at a reload.
            var changes = 0;
            service.Changed += (_, _) => changes++;
            chat.Builtin.Jobs.Add(new NewJob("theirs", "Say hello.", "daily at 9am"));
            time.Advance(EngineJobsService.ElsewhereInterval);
            await service.IdleAsync();
            Assert.Equal(1, changes);
            time.Advance(EngineJobsService.ElsewhereInterval);
            await service.IdleAsync();
            Assert.Equal(1, changes);
        }
        finally
        {
            await chat.DisposeAsync();
        }

        var here = (await service.LoadAsync(Token)).Scheduler;
        Assert.Equal(SchedulerHost.Here, here.Host);
        Assert.Equal("running in this process", app.SchedulerState);
        Assert.Equal(app.SchedulerHolder!.Since, here.Since);
    }

    private static readonly Dictionary<string, string?> HomeChat = new(SchedulerOn)
    {
        ["Corvid:Gateway:Adapters:telegram:Enabled"] = "true",
        ["Corvid:Gateway:Adapters:telegram:Secret"] = "telegram.bot-token",
        ["Corvid:Gateway:Adapters:telegram:HomeChat"] = "-100123",
    };

    [Fact]
    public async Task AJobMadeHereCanReportToAnAdaptersHomeChat()
    {
        await using var fixture = await OpsFixture.StartAsync(HomeChat);
        using var service = new EngineJobsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);
        Assert.Equal([EngineJobsService.KeptInSession, "the telegram home chat (-100123)"], snapshot.Destinations);

        var job = await service.CreateAsync(new JobDraft("morning", "daily at 9am", "Summarise the inbox.", null, false,
            "the telegram home chat (-100123)"), Token);

        // Made as that chat's own job is: its run's result is queued for the chat in the delivery ledger, which the gateway sends.
        Assert.Equal("gateway:telegram:-100123", fixture.Runtime.Builtin.Jobs.Get(job.Id)!.OriginSession);
        Assert.Equal("the telegram chat -100123", job.Deliver);
        Assert.StartsWith("gateway:telegram:-100123 (through cron_create there, or here to report there)", job.CreatedBy, StringComparison.Ordinal);
        Assert.False(fixture.Runtime.Builtin.Jobs.Get(job.Id)!.CreatedTainted);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new JobDraft("other", "daily at 9am",
            "Do it.", null, false, "the discord home chat (1)"), Token));
        Assert.Contains("is neither", refused.Message, StringComparison.Ordinal);
    }
}
