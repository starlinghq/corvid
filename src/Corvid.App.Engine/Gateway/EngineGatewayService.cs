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
/// configuration, and each paired guest's own tool allowlist (G-14, kept in the pairing store, which the gateway reads for
/// each guest message wherever it runs). Pairing is approved here, on this machine, never from a chat: guest or owner through
/// <see cref="PairingStore.Approve"/>, Ignore through <see cref="PairingStore.Dismiss"/>, revoke through
/// <see cref="PairingStore.Revoke"/>, as <c>corvid gateway pair</c> does. A gateway in another process reads the store on
/// each message, so what is decided here applies there at once.
/// <para>
/// G-14: the engine can host the gateway (<see cref="AppEngine.GatewayAsync"/>, under the one-lock rule shared with
/// <c>corvid gateway</c>), only when the person asked (U-8: off by default), so the page shows where it runs from the host's
/// status API: here, with each adapter's live state and since when; in another process, named from its lock's note, whose
/// adapters' state is that process's (<see cref="AdapterState.Unknown"/>); or nowhere, with why. While asked for, a load
/// that finds the lock free again (the other process exited) takes the gateway over, as the engine's timer does. The gateway's notices (an adapter connecting or failing, a pairing request) raise
/// <see cref="Changed"/>.
/// </para>
/// </summary>
public sealed class EngineGatewayService : IGatewayService, IDisposable
{
    /// <summary>Who hosts the gateway when it runs here, as the page names it.</summary>
    public const string HereHolder = "this app";

    /// <summary>The most open deliveries listed.</summary>
    public const int MaxDeliveries = 50;

    private readonly AppEngine _engine;
    private readonly ChangeSignal _changed;
    private readonly Lock _gate = new();
    private readonly LedgerCursor _ledger = new();
    private readonly BackgroundWork _work = new();
    private PairingStore? _pairing;
    private Action? _unsubscribe;
    private bool _disposed;

    public EngineGatewayService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
        _work.Track(SubscribeAsync());
        _engine.Started += OnEngineStarted;
    }

    /// <summary>
    /// A runtime that could not start at first (models were missing) started later: its gateway is a new one, so the page
    /// hears it from now on and shows it.
    /// </summary>
    private void OnEngineStarted(CorvidRuntime runtime)
    {
        Action? unsubscribe;
        lock (_gate)
        {
            if (_disposed)
                return;
            (unsubscribe, _unsubscribe) = (_unsubscribe, null);
        }
        unsubscribe?.Invoke();
        _work.Track(SubscribeAsync());
        _changed.Raise();
    }

    /// <summary>Waits for the subscription to the hosted gateway (tests).</summary>
    internal Task IdleAsync() => _work.IdleAsync();

    /// <summary>Once the engine's gateway exists: hear its notices and its start and stop until disposed.</summary>
    private async Task SubscribeAsync()
    {
        HostedGateway? gateway;
        try
        {
            gateway = await _engine.GatewayAsync().ConfigureAwait(false);
        }
        catch (Corvid.App.Core.Composition.ServiceUnavailableException)
        {
            // The page says why when it loads.
            return;
        }
        if (gateway is null)
            return;
        void OnChanged() => _changed.Raise();
        lock (_gate)
        {
            if (_disposed)
                return;
            gateway.Changed += OnChanged;
            _unsubscribe = () => gateway.Changed -= OnChanged;
        }
    }

    public event EventHandler? Changed;

    public async Task<GatewaySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var gateway = await _engine.GatewayAsync().ConfigureAwait(false);
        // Asked to host it (U-8) and another process ran it and has exited: this app takes it over now (the one-lock rule
        // decides, as at start), under the engine's hosting lock so a Stop meanwhile wins. Never when the app is not asked to.
        await _engine.TakeOverGatewayAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var options = runtime.Options;
            var status = Status(gateway, options, _engine.HostsGateway);
            var store = Store(runtime);
            var requests = store?.Pending().Select(Request).ToList() ?? [];
            var users = Users(options, store);
            return new GatewaySnapshot(Host(status), Adapters(options, status), requests, users, Deliveries(runtime.Log, status),
                Overrides(options), GuestTools(options, runtime.Tools.List()));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the gateway runs: here, with the live state; else in the process whose lock the probe finds (whether or not this
    /// app is asked to host it); else nowhere, with why (this app's refusal when asked, or that it is not asked).
    /// </summary>
    internal static GatewayStatus Status(HostedGateway? gateway, CorvidOptions options, bool hosting)
    {
        if (gateway is { Status: { Hosting: GatewayHosting.Here } here })
            return here;
        var probe = GatewayStatus.Probe(options);
        if (probe.Hosting == GatewayHosting.Elsewhere)
            return probe;
        return hosting && gateway is not null
            ? gateway.Status
            : new GatewayStatus(GatewayHosting.Off, "This app does not host it (Corvid:App:HostGateway is off).");
    }

    /// <summary>Where the gateway runs, for the page's line: this app since it took the lock, the process the note names, or nobody and why.</summary>
    internal static CoreHost Host(GatewayStatus status) => status.Hosting switch
    {
        GatewayHosting.Here => new CoreHost(true, HereHolder, status.Holder?.Since),
        GatewayHosting.Elsewhere => new CoreHost(false, status.Holder?.ToString() ?? "another corvid process", status.Holder?.Since),
        _ => new CoreHost(false, "", null) { Reason = status.Detail },
    };

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
            return User(paired, runtime.Options, store);
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
        if (tools.Any(SafeText.HasHidden))
            throw new InvalidOperationException("A tool name holds hidden characters; nothing was changed.");
        // The tools a guest may be given, checked as corvid gateway pair tools and an adapter's GuestTools are.
        var registered = runtime.Tools.List();
        if (CorvidGateway.GuestAllowlistProblems(tools, runtime.Options, registered) is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(" ", problems) + " Nothing was changed.");
        var changed = await Task.Run(() =>
        {
            var (adapter, id) = Split(userId);
            if (Store(runtime) is not { } store || !store.SetGuestTools(adapter, id, tools))
                throw new InvalidOperationException($"{SafeText.Reveal(userId)} is no longer a paired guest; nothing was changed.");
            return Users(runtime.Options, store).First(candidate => candidate.Id == userId);
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return changed;
    }

    public void Dispose()
    {
        Action? unsubscribe;
        lock (_gate)
        {
            _disposed = true;
            (unsubscribe, _unsubscribe) = (_unsubscribe, null);
        }
        _engine.Started -= OnEngineStarted;
        unsubscribe?.Invoke();
        _changed.Dispose();
    }

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

    /// <summary>
    /// Every adapter Corvid has, then any other configured: live when the gateway runs here (connected, connecting, retrying
    /// with why, since when), otherwise its configuration with where its state is kept.
    /// </summary>
    internal static List<GatewayAdapter> Adapters(CorvidOptions options, GatewayStatus status)
    {
        var adapters = new List<GatewayAdapter>();
        var configured = options.Gateway.Adapters;
        foreach (var name in CorvidGateway.Platforms.Order(StringComparer.Ordinal).Concat(
                     configured.Keys.Select(key => key.ToLowerInvariant()).Where(key => !CorvidGateway.Platforms.Contains(key)).Order(StringComparer.Ordinal)))
        {
            var settings = configured.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (settings is not { Enabled: true })
            {
                adapters.Add(new GatewayAdapter(name, AdapterState.Off)
                {
                    Detail = settings is null ? $"not configured (Corvid:Gateway:Adapters:{name})" : $"turned off (Corvid:Gateway:Adapters:{name}:Enabled is false)",
                });
                continue;
            }
            if (status.Adapters.FirstOrDefault(live => live.Name == name) is { } live)
            {
                adapters.Add(new GatewayAdapter(name, live.State switch
                {
                    Corvid.Gateway.AdapterState.Connected => AdapterState.Up,
                    Corvid.Gateway.AdapterState.Starting or Corvid.Gateway.AdapterState.Connecting => AdapterState.Connecting,
                    _ => AdapterState.Down,
                })
                {
                    Since = live.Since,
                    Detail = live.State switch
                    {
                        Corvid.Gateway.AdapterState.Retrying => "retrying" + (live.Detail is { Length: > 0 } why ? ": " + why : ""),
                        Corvid.Gateway.AdapterState.Stopped => "stopped",
                        _ => live.Detail ?? "",
                    },
                });
                continue;
            }
            var token = $"its token the Vault secret {settings.Secret ?? "(none named)"}";
            adapters.Add(new GatewayAdapter(name, AdapterState.Unknown)
            {
                Detail = status.Hosting switch
                {
                    GatewayHosting.Elsewhere => $"enabled, {token}; run by {status.Holder?.ToString() ?? "another corvid process"}, so whether it is connected is known there",
                    GatewayHosting.Here => $"enabled, {token}; not started by this app's gateway",
                    _ => $"enabled, {token}; no process runs the gateway: {status.Detail}",
                },
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
        store?.Users().Select(user => User(user, options, store)).ToList() ?? [];

    /// <summary>A paired user; a guest with their own allowlist (G-14), or else their adapter's <c>GuestTools</c>.</summary>
    private static CorePaired User(Corvid.Gateway.PairedUser user, CorvidOptions options, PairingStore store) =>
        new($"{user.Adapter}:{user.UserId}", user.Adapter, Handle(user.Name, user.UserId), user.Role == SenderRole.Owner ? PairingRole.Owner : PairingRole.Guest,
            user.PairedAt)
        {
            Allowlist = user.Role == SenderRole.Guest
                ? [.. store.GuestTools(user.Adapter, user.UserId)
                    ?? options.Gateway.Adapters.FirstOrDefault(pair => pair.Key.Equals(user.Adapter, StringComparison.OrdinalIgnoreCase)).Value?.GuestTools
                    ?? []]
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
    internal List<CorePending> Deliveries(IEventLog log, GatewayStatus status)
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
                $"{GatewayText.Channel(item.Queued.Adapter)} chat {item.Queued.ChatId}", item.At, WhyWaiting(item.Queued.Adapter, status)))];
        }
    }

    /// <summary>Why a delivery is still open, from where the gateway runs and its adapter's state.</summary>
    private static string WhyWaiting(string adapter, GatewayStatus status) => status.Hosting switch
    {
        GatewayHosting.Here => status.Adapters.FirstOrDefault(live => live.Name == adapter) switch
        {
            null => "its adapter is not enabled in this app's gateway",
            { State: Corvid.Gateway.AdapterState.Connected } => "being sent by this app's gateway",
            { State: Corvid.Gateway.AdapterState.Retrying } retrying => "sent once its adapter reconnects" + (retrying.Detail is { Length: > 0 } why ? $" ({why})" : ""),
            _ => "sent once its adapter connects",
        },
        GatewayHosting.Elsewhere => $"waiting for {status.Holder?.ToString() ?? "another corvid process"} to send it",
        _ => $"waiting: no process runs the gateway ({status.Detail})",
    };

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
    /// <remarks>Checked as a guest's allowlist is (<see cref="CorvidGateway.GuestAllowlistProblems"/>): no cron_create, whose job runs with every tool.</remarks>
    private static List<string> GuestTools(CorvidOptions options, IEnumerable<ToolDescriptor> registered)
    {
        var tools = registered as IReadOnlyCollection<ToolDescriptor> ?? [.. registered];
        return [.. tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Where(name => CorvidGateway.GuestAllowlistProblems([name], options, tools).Count == 0).Order(StringComparer.Ordinal)];
    }
}
