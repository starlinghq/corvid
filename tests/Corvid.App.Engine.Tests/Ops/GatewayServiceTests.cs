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

/// <summary>
/// The Gateway page over the pairing store, the delivery ledger and the configuration (C-020), on an engine that hosts no
/// gateway (it reads the lock as another process would); hosting itself (G-14) is <see cref="GatewayHostingTests"/>.
/// </summary>
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
    public async Task TheStoresAndTheConfigurationLoadAndNoProcessHostsTheGateway()
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

        Assert.Equal(new GatewayHost(false, "", null) { Reason = "No gateway is running." }, snapshot.Host);
        Assert.True(snapshot.Host.Nowhere);
        Assert.Equal(["discord", "slack", "telegram"], snapshot.Adapters.Select(adapter => adapter.Name));
        var telegram = snapshot.Adapters.Single(adapter => adapter.Name == "telegram");
        Assert.Equal(AdapterState.Unknown, telegram.State);
        Assert.Contains("telegram.bot-token", telegram.Detail, StringComparison.Ordinal);
        Assert.Contains("no process runs the gateway", telegram.Detail, StringComparison.Ordinal);
        Assert.Equal(AdapterState.Off, snapshot.Adapters.Single(adapter => adapter.Name == "slack").State);
        var request = Assert.Single(snapshot.Requests);
        Assert.Equal((dana.Code, "telegram", "dana (1001)", PairingStore.Display(dana.Code), "direct message"),
            (request.Id, request.Adapter, request.Handle, request.Code, request.Chat));
        var owner = Assert.Single(snapshot.Users);
        Assert.Equal(("telegram:7", PairingRole.Owner, "kestrel (7)"), (owner.Id, owner.Role, owner.Handle));
        var delivery = Assert.Single(snapshot.Deliveries);
        Assert.Equal(("d-open", "job from job:job-1:run-1", "Telegram chat 42"), (delivery.Id, delivery.What, delivery.To));
        Assert.Contains("no process runs the gateway", delivery.Why, StringComparison.Ordinal);
        Assert.Equal(new ChatOverride("telegram", "42", "terse", "generator: fake-gen"), Assert.Single(snapshot.Overrides));
        Assert.Contains("list_notes", snapshot.GuestTools);
        // Offered only what a guest may be given: no memory writes, no cron_create.
        Assert.DoesNotContain("cron_create", snapshot.GuestTools);
        Assert.DoesNotContain(snapshot.GuestTools, tool => tool.StartsWith("memory_add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachLoadReadsOnlyWhatTheLedgerGainedSinceTheLast()
    {
        await using var fixture = await OpsFixture.StartAsync(Telegram);
        var ledger = new DeliveryLedger(fixture.Runtime.Log);
        ledger.Queue(new DeliveryQueued("d-1", "telegram", "42", "first", "reply"));
        ledger.Queue(new DeliveryQueued("d-2", "telegram", "42", "second", "reply"));
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        var reads = new ReadRecorder(fixture.Runtime.Log);

        var off = new Corvid.Host.Composition.GatewayStatus(Corvid.Host.Composition.GatewayHosting.Off, "No gateway is running.");
        Assert.Equal(["d-1", "d-2"], service.Deliveries(reads, off).Select(delivery => delivery.Id));
        var through = service.LedgerThrough;
        Assert.True(through > 0);
        ledger.Resolve("d-1", DeliveryStatus.Delivered);
        ledger.Queue(new DeliveryQueued("d-3", "telegram", "42", "third", "reply"));

        Assert.Equal(["d-2", "d-3"], service.Deliveries(reads, off).Select(delivery => delivery.Id));
        // The first load read from the start; the second only past where the first stopped.
        Assert.Equal([0L, through], reads.After);
    }

    private sealed class ReadRecorder(IEventLog inner) : IEventLog
    {
        public List<long> After { get; } = [];

        public long GetHead(string sessionId, CancellationToken cancellationToken = default) => inner.GetHead(sessionId, cancellationToken);

        public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
            CancellationToken cancellationToken = default) => inner.Append(sessionId, expectedHead, events, cancellationToken);

        public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue, int limit = 256,
            CancellationToken cancellationToken = default)
        {
            After.Add(afterSequence);
            return inner.Read(sessionId, afterSequence, throughSequence, limit, cancellationToken);
        }
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
    public async Task RevokingKeepsTheLastOwnerAndAGuestGetsTheirOwnAllowlist()
    {
        await using var fixture = await OpsFixture.StartAsync(Telegram);
        var pairing = Pairing(fixture);
        pairing.Approve(pairing.Request("telegram", "7", "kestrel", "7").Request!.Code, SenderRole.Owner);
        pairing.Approve(pairing.Request("telegram", "8", "river", "8").Request!.Code, SenderRole.Guest);
        using var service = new EngineGatewayService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        Assert.Equal("That is the only owner; pair another owner before revoking this one.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevokeAsync("telegram:7", Token))).Message);
        // A guest starts with the adapter's tools; the page gives them their own (G-14), which the gateway reads at their next message.
        Assert.Equal(["list_notes"], (await service.LoadAsync(Token)).Users.Single(user => user.Id == "telegram:8").Allowlist);
        var widened = await service.SetAllowlistAsync("telegram:8", ["list_notes", "add_note"], Token);
        Assert.Equal(["add_note", "list_notes"], widened.Allowlist);
        Assert.Equal(["add_note", "list_notes"], pairing.GuestTools("telegram", "8"));
        Assert.Equal(["add_note", "list_notes"], (await service.LoadAsync(Token)).Users.Single(user => user.Id == "telegram:8").Allowlist);
        Assert.Empty((await service.SetAllowlistAsync("telegram:8", [], Token)).Allowlist);
        // Refused, and nothing changed: a tool that writes memory, cron_create, a name that is no tool, a hidden character.
        foreach (var refused in new[] { "memory_add", "cron_create", "no_such_tool", "list\u200B_notes" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAllowlistAsync("telegram:8", ["list_notes", refused], Token));
        Assert.Empty(pairing.GuestTools("telegram", "8")!);
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
