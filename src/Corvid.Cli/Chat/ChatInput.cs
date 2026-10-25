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
    /// G-15: whether <see cref="ReadDuringTurnAsync"/> hears lines typed while a turn runs. An input that does not (a
    /// script) keeps every such line for after the turn, as before.
    /// </summary>
    bool ReadsDuringTurn => false;

    /// <summary>
    /// G-15: the next line typed while a turn runs, for the few commands that run beside it (<c>/fork</c>). A line typed
    /// while an approval prompt waits goes to the prompt, never here; lines typed before this call stay where they are. The
    /// chat gives back (<see cref="PutBack"/>) a line that is not such a command, which the next ordinary read then returns,
    /// so typing ahead works as before. Null at end of input.
    /// </summary>
    ValueTask<string?> ReadDuringTurnAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    /// <summary>G-15: keeps a line read during a turn for the next ordinary read, after any typed before it.</summary>
    void PutBack(string line)
    {
    }
}

/// <summary>
/// Standard input, read by one background loop. Each line goes to whoever waits for it, an approval prompt (a fresh read)
/// first, then an ordinary read, then the running turn's reader (G-15); with nobody waiting it is kept, typed ahead, for
/// the next ordinary read. Reads can be cancelled (a deadline, Ctrl-C) without leaving a blocked
/// <see cref="Console.ReadLine"/> that would swallow the next line.
/// </summary>
internal sealed class ConsoleChatInput(Func<string?>? readLine = null, bool? echoes = null) : IChatInput
{
    private readonly Func<string?> _readLine = readLine ?? Console.ReadLine;
    private readonly Queue<string> _typedAhead = new();
    private readonly Lock _gate = new();
    private Task? _reader;
    private bool _ended;
    private TaskCompletionSource<string?>? _fresh;
    private TaskCompletionSource<string?>? _ordinary;
    private TaskCompletionSource<string?>? _duringTurn;

    public bool Echoes => echoes ?? !Console.IsInputRedirected;

    public bool ReadsDuringTurn => true;

    public ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _reader ??= Task.Factory.StartNew(Read, TaskCreationOptions.LongRunning);
            // A fresh read skips what was typed ahead; it stays for the next ordinary read.
            if (!fresh && _typedAhead.TryDequeue(out var queued))
                return ValueTask.FromResult<string?>(queued);
            if (_ended)
                return ValueTask.FromResult<string?>(null);
            return new ValueTask<string?>(fresh ? Wait(ref _fresh, cancellationToken) : Wait(ref _ordinary, cancellationToken));
        }
    }

    public ValueTask<string?> ReadDuringTurnAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _reader ??= Task.Factory.StartNew(Read, TaskCreationOptions.LongRunning);
            if (_ended)
                return ValueTask.FromResult<string?>(null);
            return new ValueTask<string?>(Wait(ref _duringTurn, cancellationToken));
        }
    }

    public void PutBack(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
            _typedAhead.Enqueue(line);
    }

    // Called with the lock held: registers the one waiter of its kind, which a cancel takes back if no line reached it.
    private Task<string?> Wait(ref TaskCompletionSource<string?>? slot, CancellationToken cancellationToken)
    {
        if (slot is not null)
            throw new InvalidOperationException("Another read of this kind is already waiting.");
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        slot = waiter;
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_fresh, waiter))
                        _fresh = null;
                    else if (ReferenceEquals(_ordinary, waiter))
                        _ordinary = null;
                    else if (ReferenceEquals(_duringTurn, waiter))
                        _duringTurn = null;
                    else
                        return;
                }
                waiter.TrySetCanceled(cancellationToken);
            });
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return waiter.Task;
    }

    private void Read()
    {
        while (_readLine() is { } line)
            Deliver(line);
        TaskCompletionSource<string?>?[] waiting;
        lock (_gate)
        {
            _ended = true;
            waiting = [_fresh, _ordinary, _duringTurn];
            _fresh = _ordinary = _duringTurn = null;
        }
        foreach (var waiter in waiting)
            waiter?.TrySetResult(null);
    }

    private void Deliver(string line)
    {
        TaskCompletionSource<string?>? waiter;
        lock (_gate)
        {
            if (_fresh is not null)
                (waiter, _fresh) = (_fresh, null);
            else if (_ordinary is not null)
                (waiter, _ordinary) = (_ordinary, null);
            else if (_duringTurn is not null)
                (waiter, _duringTurn) = (_duringTurn, null);
            else
            {
                _typedAhead.Enqueue(line);
                return;
            }
        }
        waiter.TrySetResult(line);
    }
}

/// <summary>Lines from a list, for tests and piped scripts. A <c>null</c> entry, or the end of the list, ends the input.</summary>
public sealed class ScriptedChatInput(IEnumerable<string?> lines) : IChatInput
{
    private readonly Queue<string?> _lines = new(lines);

    public List<string?> Read { get; } = [];

    public bool Echoes => false;

    public ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = _lines.TryDequeue(out var next) ? next : null;
        Read.Add(line);
        return ValueTask.FromResult(line);
    }
}
