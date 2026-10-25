namespace Corvid.Cli.Chat;

/// <summary>Where the chat reads lines: the terminal, or a script in tests.</summary>
public interface IChatInput
{
    /// <summary>The terminal shows what is typed; otherwise (piped input, a script) the chat prints each line itself.</summary>
    bool Echoes { get; }

    /// <summary>Reads one line; null at end of input.</summary>
    /// <param name="fresh">
    /// Only a line typed after this call counts (an approval answer): lines typed ahead are kept for the next ordinary read,
    /// so a message typed during a turn can never answer a prompt it did not see.
    /// </param>
    ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// A5, while a turn runs: the next line typed that <paramref name="accepts"/> takes (a <c>/model</c> command, which the turn
    /// takes at its next step), or null at the end of input. A line it does not take is kept for the next ordinary read, as
    /// before; a <paramref name="fresh"/>-style read waiting at the same time (an approval prompt) gets every line first, so
    /// an approval answer is never taken for a command. Cancelled when the turn ends.
    /// </summary>
    ValueTask<string?> ReadDuringTurnAsync(Func<string, bool> accepts, CancellationToken cancellationToken);
}

/// <summary>
/// Standard input, read by one background loop and handed to whoever waits for it: an ordinary or fresh read (in the order
/// they began), else a turn's command watcher when it takes the line, else the typed-ahead queue. Reads can then be
/// cancelled (a deadline, Ctrl-C, a turn's end) without leaving a blocked <see cref="Console.ReadLine"/> that would swallow
/// the next line.
/// </summary>
internal sealed class ConsoleChatInput : IChatInput
{
    private readonly LineRouter _router = new();
    private readonly Lock _gate = new();
    private Task? _reader;

    public bool Echoes => !Console.IsInputRedirected;

    public ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        Start();
        return _router.ReadAsync(fresh, cancellationToken);
    }

    public ValueTask<string?> ReadDuringTurnAsync(Func<string, bool> accepts, CancellationToken cancellationToken)
    {
        Start();
        return _router.WatchAsync(accepts, cancellationToken);
    }

    private void Start()
    {
        lock (_gate)
            _reader ??= Task.Factory.StartNew(Read, TaskCreationOptions.LongRunning);
    }

    private void Read()
    {
        while (Console.ReadLine() is { } line)
            _router.Deliver(line);
        _router.Complete();
    }
}

/// <summary>
/// Hands each line read to one waiter (A5): reads first (ordinary and fresh, oldest first), then a turn's watcher when its
/// filter takes the line, else the typed-ahead queue an ordinary read takes from first. Thread-safe.
/// </summary>
internal sealed class LineRouter
{
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _typedAhead = new();
    private readonly LinkedList<TaskCompletionSource<string?>> _readers = new();
    private (Func<string, bool> Accepts, TaskCompletionSource<string?> Waiter)? _watcher;
    private bool _completed;

    /// <summary>Lines read and not yet taken, for tests.</summary>
    internal int TypedAhead
    {
        get
        {
            lock (_gate)
                return _typedAhead.Count;
        }
    }

    public ValueTask<string?> ReadAsync(bool fresh, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string?> waiter;
        LinkedListNode<TaskCompletionSource<string?>> node;
        lock (_gate)
        {
            if (!fresh && _typedAhead.First is { } queued)
            {
                _typedAhead.RemoveFirst();
                return ValueTask.FromResult<string?>(queued.Value);
            }
            if (_completed)
                return ValueTask.FromResult<string?>(null);
            waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _readers.AddLast(waiter);
        }
        return Wait(waiter, cancellationToken, () =>
        {
            if (node.List is not null)
                _readers.Remove(node);
        });
    }

    public ValueTask<string?> WatchAsync(Func<string, bool> accepts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        TaskCompletionSource<string?> waiter;
        lock (_gate)
        {
            if (_completed)
                return ValueTask.FromResult<string?>(null);
            if (_watcher is not null)
                throw new InvalidOperationException("A turn's input is already watched.");
            waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _watcher = (accepts, waiter);
        }
        return Wait(waiter, cancellationToken, () =>
        {
            if (_watcher?.Waiter == waiter)
                _watcher = null;
        });
    }

    public void Deliver(string line)
    {
        TaskCompletionSource<string?>? taker = null;
        lock (_gate)
        {
            if (_readers.First is { } first)
            {
                _readers.RemoveFirst();
                taker = first.Value;
            }
            else if (_watcher is { } watcher && Accepts(watcher.Accepts, line))
            {
                _watcher = null;
                taker = watcher.Waiter;
            }
            else
            {
                _typedAhead.AddLast(line);
            }
        }
        taker?.TrySetResult(line);
    }

    public void Complete()
    {
        List<TaskCompletionSource<string?>> waiting;
        lock (_gate)
        {
            _completed = true;
            waiting = [.. _readers];
            _readers.Clear();
            if (_watcher is { } watcher)
                waiting.Add(watcher.Waiter);
            _watcher = null;
        }
        foreach (var waiter in waiting)
            waiter.TrySetResult(null);
    }

    private static bool Accepts(Func<string, bool> accepts, string line)
    {
        try
        {
            return accepts(line);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    // Waits for the line; a cancelled wait stops waiting under the lock, and a line handed over just as it was cancelled goes
    // back to the head of the typed-ahead queue, so no line is lost.
    private async ValueTask<string?> Wait(TaskCompletionSource<string?> waiter, CancellationToken cancellationToken, Action forget)
    {
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                forget();
                if (waiter.Task.IsCompletedSuccessfully && waiter.Task.Result is { } line)
                    _typedAhead.AddFirst(line);
            }
            throw;
        }
    }
}

/// <summary>Lines from a list, for tests and piped scripts. A <c>null</c> entry, or the end of the list, ends the input.</summary>
public sealed class ScriptedChatInput(IEnumerable<string?> lines) : IChatInput
{
    private readonly Queue<string?> _lines = new(lines);
    private readonly Queue<(Task After, string Line)> _duringTurn = new();

    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string?> Read { get; } = [];

    public bool Echoes => false;

    /// <summary>
    /// Completes when the chat asks for a line during a turn and none is scripted any more: every line typed during the turn
    /// has been taken and its command has answered (the chat asks again only after that).
    /// </summary>
    public Task DuringTurnDrained => _drained.Task;

    /// <summary>
    /// A5: a line "typed" while a turn runs, once <paramref name="after"/> completes (a test's gate inside the turn); the
    /// chat's mid-turn reader takes it when its filter accepts it. Lines are given out in the order added.
    /// </summary>
    public ScriptedChatInput TypeDuringTurn(string line, Task after)
    {
        ArgumentNullException.ThrowIfNull(after);
        lock (_duringTurn)
            _duringTurn.Enqueue((after, line));
        return this;
    }

    public ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? line;
        lock (_lines)
        {
            line = _lines.TryDequeue(out var next) ? next : null;
            Read.Add(line);
        }
        return ValueTask.FromResult(line);
    }

    public async ValueTask<string?> ReadDuringTurnAsync(Func<string, bool> accepts, CancellationToken cancellationToken)
    {
        bool scripted;
        (Task After, string Line) next;
        lock (_duringTurn)
            scripted = _duringTurn.TryPeek(out next);
        if (!scripted)
        {
            _drained.TrySetResult();
            // Nothing scripted for this turn: wait for its end.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }
        await next.After.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_duringTurn)
            _duringTurn.Dequeue();
        lock (_lines)
            Read.Add(next.Line);
        if (accepts(next.Line))
            return next.Line;
        // Not taken: it is read after the turn, as a line typed ahead is.
        lock (_lines)
            _lines.Enqueue(next.Line);
        return await ReadDuringTurnAsync(accepts, cancellationToken).ConfigureAwait(false);
    }
}
