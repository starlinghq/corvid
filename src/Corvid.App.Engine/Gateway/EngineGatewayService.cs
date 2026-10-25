using Corvid.Abstractions.Events;
using Corvid.App.Core;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Ops;
using Corvid.Gateway;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Tools;
using AdapterState = Corvid.App.Core.Gateway.AdapterState;
using CoreHost = Corvid.App.Core.Gateway.GatewayHost;
using CorePaired = Corvid.App.Core.Gateway.PairedUser;
using CorePending = Corvid.App.Core.Gateway.PendingDelivery;
using CoreRequest = Corvid.App.Core.Gateway.PairingRequest;

namespace Corvid.App.Engine.Gateway;

/// <summary>
/// The Gateway page over what the gateway's stores hold (C-020): the pairing store's waiting codes and paired users, the
/// delivery ledger's open deliveries in the event log, and the adapters, per-chat overrides and guest tools of the
/// configuration. Pairing is approved here, on this machine, never from a chat: guest or owner through
/// <see cref="PairingStore.Approve"/>, Ignore through <see cref="PairingStore.Dismiss"/>, revoke through
/// <see cref="PairingStore.Revoke"/>, as <c>corvid gateway pair</c> does. A gateway in another process reads the store on
/// each message, so what is decided here applies there at once.
/// <para>
/// What it does not do yet: host the gateway in this process (G-14). So the adapters' live state is not known here
/// (<see cref="AdapterState.Unknown"/>), and the deliveries wait for whichever gateway runs. The engine has no per-person
/// guest allowlist: a guest's tools are its adapter's <c>GuestTools</c>, shared by every guest on that adapter, so each
/// guest is shown with those, and <see cref="SetAllowlistAsync"/> refuses rather than widen every guest at once.
/// </para>
/// </summary>
public sealed class EngineGatewayService : IGatewayService, IDisposable
{
    /// <summary>Who hosts the gateway, as the page's "elsewhere" line names it.</summary>
    public const string Holder = "corvid gateway (this app does not host it yet, G-14)";

    /// <summary>The most open deliveries listed.</summary>
    public const int MaxDeliveries = 50;

    private readonly AppEngine _engine;
    private readonly ChangeSignal _changed;
    private readonly Lock _gate = new();
    private readonly LedgerCursor _ledger = new();
    private PairingStore? _pairing;

    public EngineGatewayService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? Changed;

    public async Task<GatewaySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var options = runtime.Options;
            var store = Store(runtime);
            var requests = store?.Pending().Select(Request).ToList() ?? [];
            var users = Users(options, store);
            return new GatewaySnapshot(new CoreHost(false, Holder, null), Adapters(options), requests, users, Deliveries(runtime.Log),
                Overrides(options), GuestTools(runtime.Tools.List()));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CorePaired> PairAsync(string requestId, PairingRole role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var user = await Task.Run(() =>
        {
            var store = Store(runtime) ?? throw Answered();
            var request = store.Pending().FirstOrDefault(pending => pending.Code == requestId) ?? throw Answered();
            // A handle that hides a character may not be who it reads as: never paired, from here or from the tray.
            if (SafeText.HasHidden(Handle(request.Name, request.UserId)))
                throw new InvalidOperationException("The handle holds hidden characters; it cannot be paired. Ignore the request.");
            var paired = store.Approve(request.Code, role == PairingRole.Owner ? SenderRole.Owner : SenderRole.Guest) ?? throw Answered();
            return User(paired, runtime.Options);
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return user;
    }

    public async Task IgnoreAsync(string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (Store(runtime) is not { } store || !store.Dismiss(requestId))
                throw Answered();
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
    }

    public async Task RevokeAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        await Task.Run(() =>
        {
            var options = runtime.Options;
            var store = Store(runtime);
            var users = Users(options, store);
            var user = users.FirstOrDefault(candidate => candidate.Id == userId)
                ?? throw new InvalidOperationException($"There is no paired user {SafeText.Reveal(userId)}.");
            if (user.Role == PairingRole.Owner && users.Count(candidate => candidate.Role == PairingRole.Owner) == 1
                && options.Gateway.Adapters.Values.All(settings => settings is null || settings.Owners.Count == 0))
                throw new InvalidOperationException("That is the only owner; pair another owner before revoking this one.");
            var (adapter, id) = Split(userId);
            if (store is null || !store.Revoke(adapter, id))
                throw new InvalidOperationException($"There is no paired user {SafeText.Reveal(userId)}.");
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
    }

    public async Task<CorePaired> SetAllowlistAsync(string userId, IReadOnlyList<string> tools, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(tools);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var users = await Task.Run(() => Users(runtime.Options, Store(runtime)), cancellationToken).ConfigureAwait(false);
        var user = users.FirstOrDefault(candidate => candidate.Id == userId)
            ?? throw new InvalidOperationException($"There is no paired user {SafeText.Reveal(userId)}.");
        if (user.Role != PairingRole.Guest)
            throw new InvalidOperationException("An owner has every tool; the allowlist is for guests.");
        throw new InvalidOperationException(
            $"The engine keeps one guest allowlist per adapter, not per person: Corvid:Gateway:Adapters:{user.Adapter}:GuestTools gives its tools to every guest on {GatewayText.Channel(user.Adapter)}, so it is not changed from here for one guest. Editing a guest's own tools comes with G-14.");
    }

    public void Dispose() => _changed.Dispose();

    private static InvalidOperationException Answered() => new("That pairing request was already answered, or its code expired.");

    /// <summary>The pairing store beside the event log; null while it does not exist (no gateway has run, so nothing is waiting).</summary>
    private PairingStore? Store(CorvidRuntime runtime)
    {
        lock (_gate)
        {
            if (_pairing is not null)
                return _pairing;
            var path = CorvidPaths.GatewayDatabase(runtime.Options);
            if (!File.Exists(path))
                return null;
            return _pairing = new PairingStore(path);
        }
    }

    private static List<GatewayAdapter> Adapters(CorvidOptions options)
    {
        var adapters = new List<GatewayAdapter>();
        var configured = options.Gateway.Adapters;
        foreach (var name in CorvidGateway.Platforms.Order(StringComparer.Ordinal).Concat(
                     configured.Keys.Select(key => key.ToLowerInvariant()).Where(key => !CorvidGateway.Platforms.Contains(key)).Order(StringComparer.Ordinal)))
        {
            var settings = configured.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            adapters.Add(settings is { Enabled: true }
                ? new GatewayAdapter(name, AdapterState.Unknown)
                {
                    Detail = $"enabled, its token the Vault secret {settings.Secret ?? "(none named)"}; only corvid gateway runs adapters until this app hosts the gateway (G-14), so whether it is connected is not known here",
                }
                : new GatewayAdapter(name, AdapterState.Off)
                {
                    Detail = settings is null ? $"not configured (Corvid:Gateway:Adapters:{name})" : $"turned off (Corvid:Gateway:Adapters:{name}:Enabled is false)",
                });
        }
        return adapters;
    }

    private static CoreRequest Request(Corvid.Gateway.PairingRequest request) =>
        new(request.Code, request.Adapter, Handle(request.Name, request.UserId), PairingStore.Display(request.Code), request.CreatedAt)
        {
            Chat = request.ChatId == request.UserId ? "direct message" : "chat " + request.ChatId,
        };

    private static string Handle(string? name, string userId) => name is { Length: > 0 } ? $"{name} ({userId})" : userId;

    /// <summary>
    /// The paired users. Those corvid.json allows without pairing (an adapter's <c>Owners</c> and <c>Guests</c>) are not
    /// listed: they have no pairing time, and only the file changes them.
    /// </summary>
    private static List<CorePaired> Users(CorvidOptions options, PairingStore? store) =>
        store?.Users().Select(user => User(user, options)).ToList() ?? [];
    private static CorePaired User(Corvid.Gateway.PairedUser user, CorvidOptions options) =>
        new($"{user.Adapter}:{user.UserId}", user.Adapter, Handle(user.Name, user.UserId), user.Role == SenderRole.Owner ? PairingRole.Owner : PairingRole.Guest,
            user.PairedAt)
        {
            Allowlist = user.Role == SenderRole.Guest
                ? [.. options.Gateway.Adapters.FirstOrDefault(pair => pair.Key.Equals(user.Adapter, StringComparison.OrdinalIgnoreCase)).Value?.GuestTools ?? []]
                : [],
        };

    private static (string Adapter, string User) Split(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? (id, "") : (id[..colon], id[(colon + 1)..]);
    }

    /// <summary>
    /// The ledger's open deliveries, oldest first, with when each was queued: read from the ledger's session as the gateway
    /// reads it (a queued entry stays open until its resolution is appended). Each load reads only what was appended since
    /// the last one, from the cursor this service keeps.
    /// </summary>
    internal List<CorePending> Deliveries(IEventLog log)
    {
        lock (_ledger)
        {
            while (true)
            {
                var page = log.Read(GatewaySessions.Ledger, _ledger.Through, limit: 512);
                foreach (var stored in page)
                {
                    if (stored.Payload is DeliveryQueued queued && _ledger.ById.TryAdd(queued.DeliveryId, stored.Sequence))
                        _ledger.Open[stored.Sequence] = (queued, stored.RecordedAt);
                    else if (stored.Payload is DeliveryResolved resolved && _ledger.ById.Remove(resolved.DeliveryId, out var sequence))
                        _ledger.Open.Remove(sequence);
                    _ledger.Through = stored.Sequence;
                }
                if (page.Count < 512)
                    break;
            }
            return [.. _ledger.Open.Values.Take(MaxDeliveries).Select(item => new CorePending(item.Queued.DeliveryId,
                item.Queued.Kind + (item.Queued.SourceSession is { } source ? " from " + source : ""),
                $"{GatewayText.Channel(item.Queued.Adapter)} chat {item.Queued.ChatId}", item.At,
                "waiting for a gateway to send it; none runs in this app (G-14)"))];
        }
    }

    /// <summary>How far the ledger has been read.</summary>
    internal long LedgerThrough
    {
        get
        {
            lock (_ledger)
                return _ledger.Through;
        }
    }

    // The ledger as read so far: the open deliveries by sequence, their ids, and the last sequence read.
    private sealed class LedgerCursor
    {
        public long Through;

        public SortedDictionary<long, (DeliveryQueued Queued, DateTimeOffset At)> Open { get; } = [];

        public Dictionary<string, long> ById { get; } = new(StringComparer.Ordinal);
    }

    private static List<ChatOverride> Overrides(CorvidOptions options) =>
    [
        .. options.Gateway.Adapters.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => pair.Value.Chats.Where(chat => chat.Value is not null).OrderBy(chat => chat.Key, StringComparer.Ordinal)
                .Select(chat => new ChatOverride(pair.Key.ToLowerInvariant(), chat.Key, chat.Value.Persona is { Length: > 0 } persona ? persona : "default",
                    chat.Value.Slots.Count == 0 ? "—" : string.Join(", ", chat.Value.Slots.OrderBy(slot => slot.Key, StringComparer.Ordinal)
                        .Select(slot => $"{slot.Key}: {slot.Value}"))))),
    ];

    /// <summary>The tools a guest could be given: the registry's, less those that write memory (which a guest never gets).</summary>
    private static List<string> GuestTools(IEnumerable<ToolDescriptor> registered) =>
        [.. registered.Where(tool => !tool.Risk.SideEffects.HasFlag(SideEffects.Memory)).Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
