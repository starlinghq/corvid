using System.Collections.Concurrent;
using System.Threading.Channels;
using Corvid.Abstractions.Sessions;

namespace Corvid.Kernel.Sessions;

public sealed record SessionHostOptions
{
    public static SessionHostOptions Default { get; } = new();

    /// <summary>Envelopes waiting to be routed before <see cref="SessionHost.PostAsync"/> waits.</summary>
    public int Capacity { get; init; } = 64;

    /// <summary>
    /// Told about an envelope that could not reach its actor (the actor factory threw, or the actor was disposed). The router
    /// keeps routing other envelopes either way.
    /// </summary>
    public Action<Envelope, Exception>? RoutingFailed { get; init; }
}

/// <summary>
/// Routes envelopes from every surface to their session's actor (plan §3.1, §3.3). Surfaces post into one bounded channel
/// and wait when it is full; the router hands each envelope to its actor, creating it on first use, and waits while that
/// actor's own queue is full. A session that stops draining therefore holds up routing for all, which is the backpressure
/// surfaces see; per-session limits are large enough that this means the agent, not one chat, is overloaded.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private readonly Func<string, SessionActor> _create;
    private readonly Channel<Envelope> _ingress;
    private readonly ConcurrentDictionary<string, SessionActor> _actors = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _router;
    private readonly Action<Envelope, Exception>? _routingFailed;

    /// <param name="createActor">Builds a session's actor from its id: its slots, turn services and delivery sink.</param>
    public SessionHost(Func<string, SessionActor> createActor, SessionHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(createActor);
        var settings = options ?? SessionHostOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Capacity);
        _create = createActor;
        _routingFailed = settings.RoutingFailed;
        _ingress = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(settings.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _router = Task.Run(RouteAsync);
    }

    /// <summary>Posts an envelope; waits while the channel is full.</summary>
    public ValueTask PostAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return _ingress.Writer.WriteAsync(envelope, cancellationToken);
    }

    /// <summary>The session's actor, creating it if no envelope has arrived yet (for <c>/model</c> before the first message).</summary>
    public SessionActor GetOrCreate(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        return _actors.GetOrAdd(sessionId, id =>
        {
            var actor = _create(id);
            return actor.SessionId == id ? actor : throw new InvalidOperationException($"The factory built an actor for '{actor.SessionId}', not '{id}'.");
        });
    }

    public bool TryGet(string sessionId, out SessionActor actor) => _actors.TryGetValue(sessionId, out actor!);

    /// <summary>Cancels the session's running turn (the CLI's Ctrl-C, a gateway's /stop).</summary>
    public bool CancelTurn(string sessionId)
    {
        if (!_actors.TryGetValue(sessionId, out var actor))
            return false;
        actor.CancelTurn();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _ingress.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _router.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var actor in _actors.Values)
            await actor.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RouteAsync()
    {
        await foreach (var envelope in _ingress.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            try
            {
                await GetOrCreate(envelope.SessionId).SubmitAsync(envelope, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!_lifetime.IsCancellationRequested)
            {
                // One session's failure must not stop routing for every other session.
                _routingFailed?.Invoke(envelope, exception);
            }
        }
    }
}
