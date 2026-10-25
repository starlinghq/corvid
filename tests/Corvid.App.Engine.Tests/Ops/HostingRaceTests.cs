using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;
using Corvid.App.Engine.Gateway;
using Corvid.Gateway;
using Corvid.Gateway.Tests;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>A4 verification: a Gateway page load that would take the gateway over never undoes a Stop answered meanwhile.</summary>
public sealed class HostingRaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStopAnsweredWhileThePageLoadsWinsOverTheLoadsTakeOver()
    {
        var adapter = new FakeAdapter();
        var theirs = new FakeAdapter();
        var settings = new Dictionary<string, string?>
        {
            ["Corvid:Gateway:Adapters:fake:Enabled"] = "true",
            ["Corvid:App:HostGateway"] = "true",
        };
        await using var fixture = await OpsFixture.StartAsync(settings,
            services => services.AddSingleton(new GatewayAdapterRegistration("fake", () => adapter)), hostGateway: true);
        var options = fixture.Host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        // "corvid gateway" holds it, then exits; the page loads before the engine's timer looks.
        var other = await fixture.Host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Token);
        var cli = new HostedGateway(other, fixture.Host.Services.GetRequiredService<SlashCommandRegistry>(), options,
            [new GatewayAdapterRegistration("fake", () => theirs)]);
        try
        {
            Assert.Equal(GatewayHosting.Here, (await cli.StartAsync(Token)).Hosting);
            Assert.Equal(GatewayHosting.Elsewhere, (await fixture.Engine.GatewayAsync())!.Status.Hosting);
            await cli.StopAsync();

            var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Engine.BeforeGatewayTakeOver = async () =>
            {
                decided.SetResult();
                await proceed.Task;
            };
            using var page = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), TimeProvider.System);
            var loading = page.LoadAsync(Token);
            await decided.Task.WaitAsync(Token);

            // Stop hosting is answered while the load has decided to take the gateway over.
            await fixture.Engine.SetHostingAsync(HostedPart.Gateway, false);
            proceed.SetResult();
            var snapshot = await loading;

            Assert.False(snapshot.Host.HostedHere);
            Assert.False(fixture.Engine.HostingState.GatewayHere);
            Assert.Equal(LockState.Free, ProcessLock.Probe(GatewayStatus.LockPath(options), withoutNote: true).State);
            Assert.Equal(0, Volatile.Read(ref adapter.Connects));
        }
        finally
        {
            await cli.DisposeAsync();
            await other.DisposeAsync();
        }
    }
}
