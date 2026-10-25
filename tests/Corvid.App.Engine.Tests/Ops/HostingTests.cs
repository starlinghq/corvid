using Corvid.App.Core.Gateway;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;
using Corvid.App.Engine.Gateway;
using Corvid.App.Engine.Hosting;
using Corvid.App.Engine.Jobs;
using Corvid.Gateway;
using Corvid.Gateway.Tests;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>
/// U-8 as the user decided it (security M1): the app hosts the gateway and the scheduler only when asked. Off by default,
/// so nothing takes either lock when the app starts or a page loads; asking starts it (and is remembered in corvid.json);
/// while another process holds a lock the engine's own timer takes over once it lets go (review M3), without a page load;
/// Stop releases the lock. The platform is the gateway tests' fake: no chat service is ever contacted.
/// </summary>
public sealed class HostingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Settings(bool hostGateway = false, bool hostScheduler = false) => new()
    {
        ["Corvid:Gateway:Adapters:fake:Enabled"] = "true",
        ["Corvid:Scheduler:Enabled"] = "true",
        ["Corvid:App:HostGateway"] = hostGateway ? "true" : "false",
        ["Corvid:App:HostScheduler"] = hostScheduler ? "true" : "false",
    };

    private static Action<IServiceCollection> Adapter(FakeAdapter adapter) =>
        services => services.AddSingleton(new GatewayAdapterRegistration("fake", () => adapter));

    private static CorvidOptions Options(Corvid.Host.Tests.TestHost host) => host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;

    [Fact]
    public async Task TheAppStartsHostingNothingAndAPageLoadTakesNoLock()
    {
        var adapter = new FakeAdapter();
        await using var host = EngineHosts.Create(settings: Settings(), services: Adapter(adapter));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);
        var runtime = await engine.RuntimeAsync();
        var options = Options(host);

        Assert.Equal(CorvidRuntime.SchedulerNotHosted, runtime.SchedulerState);
        Assert.Equal(HostingState.None, engine.HostingState);
        Assert.Equal(LockState.Free, ProcessLock.Probe(GatewayStatus.LockPath(options), withoutNote: true).State);
        Assert.Equal(LockState.Free, ProcessLock.Probe(runtime.SchedulerLockPath, withoutNote: true).State);

        // The pages load: they show who hosts (nobody), and take nothing over.
        using var gateway = new EngineGatewayService(engine, new InlineDispatcher(), TimeProvider.System);
        using var jobs = new EngineJobsService(engine, new InlineDispatcher(), TimeProvider.System);
        var page = await gateway.LoadAsync(Token);
        var scheduler = (await jobs.LoadAsync(Token)).Scheduler;
        // Opening a session wakes the scheduler (C-041): not one this app does not host.
        runtime.OpenSession(surface: "app");

        Assert.True(page.Host.Nowhere);
        Assert.Contains("Corvid:App:HostGateway is off", page.Host.Reason, StringComparison.Ordinal);
        Assert.Equal(SchedulerHost.Off, scheduler.Host);
        Assert.Contains("Corvid:App:HostScheduler is off", scheduler.Holder, StringComparison.Ordinal);
        Assert.Equal(0, adapter.Connects);
        Assert.Equal(LockState.Free, ProcessLock.Probe(GatewayStatus.LockPath(options), withoutNote: true).State);
        Assert.Equal(LockState.Free, ProcessLock.Probe(runtime.SchedulerLockPath, withoutNote: true).State);
    }

    [Fact]
    public async Task TheSettingsTurnHostingOnAtStart()
    {
        var adapter = new FakeAdapter();
        await using var host = EngineHosts.Create(settings: Settings(hostGateway: true, hostScheduler: true), services: Adapter(adapter));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);
        var runtime = await engine.RuntimeAsync();

        var gateway = await engine.GatewayAsync();

        Assert.Equal(GatewayHosting.Here, gateway!.Status.Hosting);
        Assert.Equal(new HostingState(true, true, true, true), engine.HostingState);
        Assert.Equal("gateway · scheduler", engine.HostingState.Notice);
        Assert.Equal("running in this process", runtime.SchedulerState);
    }

    [Fact]
    public async Task StartHostingStartsBothAndIsRememberedAndStopReleasesTheLocks()
    {
        var adapter = new FakeAdapter();
        await using var fixture = await OpsFixture.StartAsync(Settings(), Adapter(adapter));
        // The fixture's runtime starts its scheduler as the CLI's does; the app's does not.
        await fixture.Engine.SetHostingAsync(HostedPart.Scheduler, false);
        var options = Options(fixture.Host);
        using var hosting = new EngineHostingService(fixture.Engine, new InlineDispatcher());
        var changes = 0;
        hosting.Changed += (_, _) => changes++;
        Assert.Equal(HostingState.None, hosting.State);

        var said = await hosting.SetAsync(HostedPart.Gateway, true, Token);
        await hosting.SetAsync(HostedPart.Scheduler, true, Token);

        Assert.StartsWith("This app hosts the gateway now.", said, StringComparison.Ordinal);
        Assert.Equal(new HostingState(true, true, true, true), hosting.State);
        Assert.True(changes > 0);
        Assert.Equal(LockState.HeldElsewhere, ProcessLock.Probe(GatewayStatus.LockPath(options), withoutNote: true).State);
        Assert.Equal(LockState.HeldElsewhere, ProcessLock.Probe(fixture.Runtime.SchedulerLockPath, withoutNote: true).State);
        Assert.Contains("\"HostGateway\": true", fixture.ConfigText, StringComparison.Ordinal);
        Assert.Contains("\"HostScheduler\": true", fixture.ConfigText, StringComparison.Ordinal);
        await Wait.Until(() => Volatile.Read(ref adapter.Connects) == 1, "the adapter to connect");

        said = await hosting.SetAsync(HostedPart.Gateway, false, Token);
        await hosting.SetAsync(HostedPart.Scheduler, false, Token);

        Assert.StartsWith("This app stopped hosting the gateway", said, StringComparison.Ordinal);
        Assert.Equal(HostingState.None, hosting.State);
        Assert.True(adapter.Disposed);
        Assert.Equal(LockState.Free, ProcessLock.Probe(GatewayStatus.LockPath(options), withoutNote: true).State);
        Assert.Equal(LockState.Free, ProcessLock.Probe(fixture.Runtime.SchedulerLockPath, withoutNote: true).State);
        Assert.Equal(CorvidRuntime.SchedulerNotHosted, fixture.Runtime.SchedulerState);
        Assert.Contains("\"HostGateway\": false", fixture.ConfigText, StringComparison.Ordinal);
        // Stopped means stopped: the engine's timer takes nothing over again.
        fixture.Time.Advance(AppEngine.TakeOverInterval);
        await fixture.Engine.TakeOverIdleAsync();
        Assert.Equal(HostingState.None, hosting.State);
    }

    [Fact]
    public async Task WhileAnotherProcessHoldsTheGatewayTheEnginesTimerTakesItOverWithoutAPageLoad()
    {
        var adapter = new FakeAdapter();
        var theirs = new FakeAdapter();
        await using var fixture = await OpsFixture.StartAsync(Settings(), Adapter(adapter), hostGateway: true);
        var options = Options(fixture.Host);
        // "corvid gateway" got there first, over its own runtime.
        var other = await fixture.Host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Token);
        var cli = new HostedGateway(other, fixture.Host.Services.GetRequiredService<SlashCommandRegistry>(), options,
            [new GatewayAdapterRegistration("fake", () => theirs)]);
        try
        {
            Assert.Equal(GatewayHosting.Here, (await cli.StartAsync(Token)).Hosting);
            var gateway = await fixture.Engine.GatewayAsync();
            Assert.Equal(GatewayHosting.Elsewhere, gateway!.Status.Hosting);
            fixture.Time.Advance(AppEngine.TakeOverInterval);
            await fixture.Engine.TakeOverIdleAsync();
            Assert.Equal(GatewayHosting.Elsewhere, gateway.Status.Hosting);

            await cli.StopAsync();
            fixture.Time.Advance(AppEngine.TakeOverInterval);
            await fixture.Engine.TakeOverIdleAsync();

            Assert.Equal(GatewayHosting.Here, gateway.Status.Hosting);
            Assert.True(fixture.Engine.HostingState.GatewayHere);
            await Wait.Until(() => Volatile.Read(ref adapter.Connects) == 1, "this app's adapter to connect");
        }
        finally
        {
            await cli.DisposeAsync();
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhileAnotherProcessHoldsTheSchedulerTheEnginesTimerTakesItOverWithoutAPageLoad()
    {
        // This runtime (the app's) lost the lock to another one (a terminal's) at start.
        await using var holder = await OpsFixture.StartAsync(Settings());
        var factory = holder.Host.Services.GetRequiredService<CorvidRuntimeFactory>();
        var ours = await factory.StartAsync(Token);
        await using var engine = AppEngine.ForRuntime(holder.Host.Host, ours, time: holder.Time);
        Assert.Equal("run by another corvid process", ours.SchedulerState);
        Assert.True(engine.HostsScheduler);
        holder.Time.Advance(AppEngine.TakeOverInterval);
        await engine.TakeOverIdleAsync();
        Assert.False(engine.HostingState.SchedulerHere);

        await holder.Engine.SetHostingAsync(HostedPart.Scheduler, false);
        holder.Time.Advance(AppEngine.TakeOverInterval);
        await engine.TakeOverIdleAsync();

        Assert.True(engine.HostingState.SchedulerHere);
        Assert.Equal("running in this process", ours.SchedulerState);
    }
}
