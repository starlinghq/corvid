namespace Corvid.Http;

/// <summary>
/// One request at a time per session. A session's gate exists only while a request holds it or waits for it, so the map
/// does not grow with every session ever served. Each gate counts its holders and waiters under one lock: a gate is removed
/// only when that count reaches zero, and a later request for the session makes a new one, so two requests for one session
/// never hold different gates at once.
/// </summary>
internal sealed class SessionGates
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>Sessions with a request running or waiting (for tests).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _gates.Count;
        }
    }

    /// <summary>Waits for the session's turn; disposing the result lets the next request in.</summary>
    public async Task<IDisposable> EnterAsync(string sessionId, CancellationToken cancellationToken)
    {
        Gate gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(sessionId, out gate!))
                _gates[sessionId] = gate = new Gate();
            gate.Users++;
        }
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(sessionId, gate);
            throw;
        }
        return new Lease(this, sessionId, gate);
    }

    private void Leave(string sessionId, Gate gate)
    {
        lock (_sync)
        {
            if (--gate.Users == 0)
            {
                _gates.Remove(sessionId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Requests holding or waiting for the gate; guarded by the owner's lock.</summary>
        public int Users { get; set; }
    }

    private sealed class Lease(SessionGates owner, string sessionId, Gate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            gate.Semaphore.Release();
            owner.Leave(sessionId, gate);
        }
    }
}
