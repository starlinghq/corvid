namespace Corvid.Host.Composition;

/// <summary>
/// An event that remembers its most recent raises (C-039): a handler added later is replayed them, oldest first,
/// synchronously on the adding thread, then hears later raises live on the raising thread. Recording a raise and reading
/// the handlers happen under one lock, as do reading the buffer and adding a handler, so a handler added while a raise is
/// in flight either replays it or hears it live, never both and never neither. Handlers are invoked outside the lock.
/// </summary>
internal sealed class ReplayingEvent<T1, T2>
{
    private readonly Lock _gate = new();
    private readonly Queue<(T1 First, T2 Second)> _recent = new();
    private readonly int _capacity;
    private Action<T1, T2>? _handlers;

    public ReplayingEvent(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public void Add(Action<T1, T2>? handler)
    {
        if (handler is null)
            return;
        (T1 First, T2 Second)[] replay;
        lock (_gate)
        {
            replay = [.. _recent];
            _handlers += handler;
        }
        foreach (var (first, second) in replay)
            handler(first, second);
    }

    public void Remove(Action<T1, T2>? handler)
    {
        lock (_gate)
            _handlers -= handler;
    }

    public void Raise(T1 first, T2 second)
    {
        Action<T1, T2>? handlers;
        lock (_gate)
        {
            _recent.Enqueue((first, second));
            if (_recent.Count > _capacity)
                _recent.Dequeue();
            handlers = _handlers;
        }
        handlers?.Invoke(first, second);
    }
}
