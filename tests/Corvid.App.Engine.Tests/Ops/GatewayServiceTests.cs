using Corvid.Abstractions.Events;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Gateway;
using Corvid.Gateway;
using AdapterState = Corvid.App.Core.Gateway.AdapterState;
using GatewayHost = Corvid.App.Core.Gateway.GatewayHost;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>The Gateway page over the pairing store, the delivery ledger and the configuration (C-020); hosting is G-14's.</summary>
public sealed class GatewayServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Telegram = new()
    {
        ["Corvid:Gateway:Adapters:telegram:Enabled"] = "true",
        ["Corvid:Gateway:Adapters:telegram:Secret"] = "telegram.bot-token",
        ["Corvid:Gateway:Adapters:telegram:GuestTools:0"] = "list_notes",
        ["Corvid:Gateway:Adapters:telegram:Chats:42:Persona"] = "terse",
        ["Corvid:Gateway:Adapters:telegram:Chats:42:Slots:generator"] = "fake-gen",
    };

    private static PairingStore Pairing(OpsFixture fixture) => new(Path.Combine(fixture.Host.Root, "data", "gateway.db"));

    [Fact]
    public async Task TheStoresAndTheConfigurationLoadAndTheGatewayIsNotHostedHere()
    {
        await using var fixture = await OpsFixture.StartAsync(Telegram);
        var pairing = Pairing(fixture);
        var dana = pairing.Request("telegram", "1001", "dana", "1001").Request!;
        pairing.Approve(pairing.Request("telegram", "7", "kestrel", "7").Request!.Code, SenderRole.Owner);
        var ledger = new DeliveryLedger(fixture.Runtime.Log);
        ledger.Queue(new DeliveryQueued("d-open", "telegram", "42", "the summary", "job", SourceSession: "job:job-1:run-1"));
        ledger.Queue(new DeliveryQueued("d-sent", "telegram", "42", "sent already", "reply"));
        ledger.Resolve("d-sent", DeliveryStatus.Delivered);
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);

        Assert.Equal(new GatewayHost(false, EngineGatewayService.Holder, null), snapshot.Host);
        Assert.Equal(["discord", "slack", "telegram"], snapshot.Adapters.Select(adapter => adapter.Name));
        var telegram = snapshot.Adapters.Single(adapter => adapter.Name == "telegram");
        Assert.Equal(AdapterState.Unknown, telegram.State);
        Assert.Contains("telegram.bot-token", telegram.Detail, StringComparison.Ordinal);
        Assert.Contains("G-14", telegram.Detail, StringComparison.Ordinal);
        Assert.Equal(AdapterState.Off, snapshot.Adapters.Single(adapter => adapter.Name == "slack").State);
        var request = Assert.Single(snapshot.Requests);
        Assert.Equal((dana.Code, "telegram", "dana (1001)", PairingStore.Display(dana.Code), "direct message"),
            (request.Id, request.Adapter, request.Handle, request.Code, request.Chat));
        var owner = Assert.Single(snapshot.Users);
        Assert.Equal(("telegram:7", PairingRole.Owner, "kestrel (7)"), (owner.Id, owner.Role, owner.Handle));
        var delivery = Assert.Single(snapshot.Deliveries);
        Assert.Equal(("d-open", "job from job:job-1:run-1", "Telegram chat 42"), (delivery.Id, delivery.What, delivery.To));
        Assert.Contains("G-14", delivery.Why, StringComparison.Ordinal);
        Assert.Equal(new ChatOverride("telegram", "42", "terse", "generator: fake-gen"), Assert.Single(snapshot.Overrides));
        Assert.Contains("list_notes", snapshot.GuestTools);
    }

    [Fact]
    public async Task WithoutAPairingStoreNothingWaitsAndNothingIsCreated()
    {
        await using var fixture = await OpsFixture.StartAsync();
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);

        Assert.Empty(snapshot.Requests);
        Assert.Empty(snapshot.Users);
        Assert.All(snapshot.Adapters, adapter => Assert.Equal(AdapterState.Off, adapter.State));
        Assert.False(File.Exists(Path.Combine(fixture.Host.Root, "data", "gateway.db")));
        Assert.Equal("That pairing request was already answered, or its code expired.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.PairAsync("ABCDEFGH", PairingRole.Guest, Token))).Message);
    }

    [Fact]
    public async Task PairingAsAGuestGivesTheAdaptersGuestToolsAndACodeIsUsedOnce()
    {
        await using var fixture = await OpsFixture.StartAsync(Telegram);
        var pairing = Pairing(fixture);
        var code = pairing.Request("telegram", "1001", "dana", "1001").Request!.Code;
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var guest = await service.PairAsync(code, PairingRole.Guest, Token);

        Assert.Equal(("telegram:1001", PairingRole.Guest), (guest.Id, guest.Role));
        Assert.Equal(["list_notes"], guest.Allowlist);
        Assert.Equal(SenderRole.Guest, pairing.RoleOf(new AdapterOptions("telegram"), "1001"));
        Assert.Equal(1, changes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PairAsync(code, PairingRole.Owner, Token));
        Assert.Equal(SenderRole.Guest, pairing.RoleOf(new AdapterOptions("telegram"), "1001"));
    }

    [Fact]
    public async Task AHandleThatHidesACharacterIsNeverPairedAndIgnoreDropsItsCode()
    {
        await using var fixture = await OpsFixture.StartAsync(Telegram);
        var pairing = Pairing(fixture);
        var code = pairing.Request("telegram", "1002", "da\u200Bna", "1002").Request!.Code;
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PairAsync(code, PairingRole.Owner, Token));
        Assert.Equal("The handle holds hidden characters; it cannot be paired. Ignore the request.", refused.Message);
        Assert.Equal(SenderRole.None, pairing.RoleOf(new AdapterOptions("telegram"), "1002"));
        Assert.True(SafeText.HasHidden(Assert.Single((await service.LoadAsync(Token)).Requests).Handle));

        await service.IgnoreAsync(code, Token);
        Assert.Empty(pairing.Pending());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IgnoreAsync(code, Token));
    }

    [Fact]
    public async Task RevokingKeepsTheLastOwnerAndTheAllowlistIsPerAdapterSoItIsNotEditedForOneGuest()
    {
        await using var fixture = await OpsFixture.StartAsync(Telegram);
        var pairing = Pairing(fixture);
        pairing.Approve(pairing.Request("telegram", "7", "kestrel", "7").Request!.Code, SenderRole.Owner);
        pairing.Approve(pairing.Request("telegram", "8", "river", "8").Request!.Code, SenderRole.Guest);
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        Assert.Equal("That is the only owner; pair another owner before revoking this one.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevokeAsync("telegram:7", Token))).Message);
        var allowlist = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAllowlistAsync("telegram:8", ["list_notes", "add_note"], Token));
        Assert.StartsWith("The engine keeps one guest allowlist per adapter, not per person: Corvid:Gateway:Adapters:telegram:GuestTools", allowlist.Message,
            StringComparison.Ordinal);
        Assert.Equal("An owner has every tool; the allowlist is for guests.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAllowlistAsync("telegram:7", [], Token))).Message);

        await service.RevokeAsync("telegram:8", Token);
        Assert.Equal(SenderRole.None, pairing.RoleOf(new AdapterOptions("telegram"), "8"));
        Assert.Equal("There is no paired user telegram:8.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevokeAsync("telegram:8", Token))).Message);

        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time, new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null));
        Assert.IsType<EngineGatewayService>(composition.GatewayService);
    }
}
