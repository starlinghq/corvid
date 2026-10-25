using Corvid.App.Core.Gateway;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Gateway;
using Corvid.Gateway;
using Corvid.Gateway.Tests;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AdapterState = Corvid.App.Core.Gateway.AdapterState;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>
/// G-14 with U-8: the app hosts the gateway when it is the engine, under the one-lock rule <c>corvid gateway</c> also keeps;
/// the Gateway page's status comes from the host's status API, and pairing and revoke act on the gateway that runs here.
/// The platform is the gateway tests' fake: no chat service is ever contacted.
/// </summary>
public sealed class GatewayHostingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Fake = new()
    {
        ["Corvid:Gateway:Adapters:fake:Enabled"] = "true",
    };

    private static InboundMessage Message(string sender, string text) =>
        new("fake", "c-" + sender, sender, text) { Direct = true, Addressed = true, SenderName = "dana" };

    private static Task<OpsFixture> StartAsync(FakeAdapter adapter, FakeModels? models = null) =>
        OpsFixture.StartAsync(Fake, services => services.AddSingleton(new GatewayAdapterRegistration("fake", () => adapter)), models,
            hostGateway: true);

    private static async Task<GatewaySnapshot> UntilAsync(EngineGatewayService service, Func<GatewaySnapshot, bool> condition, string because)
    {
        GatewaySnapshot snapshot = await service.LoadAsync(Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition(snapshot))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting: " + because);
            await Task.Delay(10, Token);
            snapshot = await service.LoadAsync(Token);
        }
        return snapshot;
    }

    [Fact]
    public async Task TheAppHostsTheGatewayAndThePageShowsItsLiveState()
    {
        var adapter = new FakeAdapter();
        await using var fixture = await StartAsync(adapter);
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await UntilAsync(service, s => s.Adapters.Any(a => a.Name == "fake" && a.State == AdapterState.Up), "the adapter to connect");

        Assert.True(snapshot.Host.HostedHere);
        Assert.Equal(EngineGatewayService.HereHolder, snapshot.Host.Holder);
        Assert.NotNull(snapshot.Host.Since);
        var fake = snapshot.Adapters.Single(a => a.Name == "fake");
        Assert.NotNull(fake.Since);
        Assert.Equal(1, adapter.Connects);
        // Another process (corvid gateway status, a second app) reads who runs it from the lock's note.
        var options = fixture.Host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var seen = GatewayStatus.Probe(options);
        Assert.Equal(GatewayHosting.Elsewhere, seen.Hosting);
        Assert.Equal(Environment.ProcessId, seen.Holder!.ProcessId);
        Assert.Equal(snapshot.Host.Since, seen.Holder.Since);
    }

    [Fact]
    public async Task APairingRequestReachesThePageLiveAndPairingAndRevokeActOnTheRunningGateway()
    {
        var adapter = new FakeAdapter();
        await using var fixture = await StartAsync(adapter, new FakeModels(new FakeGenerator(Reply.Say("Hello, guest."))));
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        await service.IdleAsync();
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);
        await UntilAsync(service, s => s.Host.HostedHere, "hosting");

        // Someone messages the bot: the gateway running here asks them to pair, and the page hears of it.
        adapter.Write(Message("1001", "hi there"));
        await Wait.Until(() => adapter.Sent.Count == 1, "the pairing notice");
        await Wait.Until(() => Volatile.Read(ref changes) > 0, "the page to hear of the request");
        var request = Assert.Single((await service.LoadAsync(Token)).Requests);
        Assert.Contains(PairingStore.Display(request.Id), adapter.Sent.Single().Text, StringComparison.Ordinal);

        var guest = await service.PairAsync(request.Id, PairingRole.Guest, Token);
        Assert.Equal("fake:1001", guest.Id);
        adapter.Write(Message("1001", "what can you do?"));
        await Wait.Until(() => adapter.Sent.Count == 2, "the guest's answer");
        var answer = adapter.Sent.Last();
        Assert.Equal(("Hello, guest.", true), (answer.Text, answer.Tainted));

        await service.RevokeAsync(guest.Id, Token);
        adapter.Write(Message("1001", "still there?"));
        await Wait.Until(() => adapter.Sent.Count == 3, "a new pairing notice");
        Assert.Contains("must approve you", adapter.Sent.Last().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenAnotherProcessRunsTheGatewayThePageNamesItAndTakesOverOnceItExits()
    {
        var adapter = new FakeAdapter();
        var theirs = new FakeAdapter();
        await using var fixture = await OpsFixture.StartAsync(Fake,
            services => services.AddSingleton(new GatewayAdapterRegistration("fake", () => adapter)), hostGateway: true);
        // "corvid gateway" got there first: the same hosting, over its own runtime.
        var options = fixture.Host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var other = await fixture.Host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Token);
        var cli = new HostedGateway(other, fixture.Host.Services.GetRequiredService<SlashCommandRegistry>(), options,
            [new GatewayAdapterRegistration("fake", () => theirs)]);
        try
        {
            // The engine's gateway was not started yet (nothing asked for it): the CLI's starts first.
            Assert.Equal(GatewayHosting.Here, (await cli.StartAsync(Token)).Hosting);
            using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

            var snapshot = await service.LoadAsync(Token);

            Assert.False(snapshot.Host.HostedHere);
            Assert.False(snapshot.Host.Nowhere);
            Assert.Contains($"(pid {Environment.ProcessId})", snapshot.Host.Holder, StringComparison.Ordinal);
            Assert.Equal(cli.Status.Holder!.Since, snapshot.Host.Since);
            var fake = snapshot.Adapters.Single(a => a.Name == "fake");
            Assert.Equal(AdapterState.Unknown, fake.State);
            Assert.Contains("known there", fake.Detail, StringComparison.Ordinal);
            Assert.Equal(0, adapter.Connects);

            await cli.StopAsync();
            snapshot = await UntilAsync(service, s => s.Host.HostedHere, "the take-over");
            Assert.Equal(EngineGatewayService.HereHolder, snapshot.Host.Holder);
            await Wait.Until(() => adapter.Connects == 1, "this app's adapter to connect");
        }
        finally
        {
            await cli.DisposeAsync();
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithNoAdapterEnabledNoProcessHostsTheGatewayAndThePageSaysWhy()
    {
        await using var fixture = await OpsFixture.StartAsync(hostGateway: true);
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);

        Assert.True(snapshot.Host.Nowhere);
        Assert.Contains("No gateway adapter is enabled", snapshot.Host.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheAppStopsItsGatewayAndLetsGoOfTheLock()
    {
        var adapter = new FakeAdapter();
        var fixture = await StartAsync(adapter);
        var options = fixture.Host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        try
        {
            var gateway = await fixture.Engine.GatewayAsync();
            Assert.Equal(GatewayHosting.Here, gateway!.Status.Hosting);
            await fixture.Engine.DisposeAsync();

            Assert.True(adapter.Disposed);
            Assert.Equal(GatewayHosting.Off, GatewayStatus.Probe(options).Hosting);
            Assert.False(File.Exists(ProcessLock.NotePath(GatewayStatus.LockPath(options))));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
