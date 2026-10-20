using System.Threading.Channels;

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
}

/// <summary>
/// Standard input, read by one background loop into a channel. Reads can then be cancelled (a deadline, Ctrl-C) without
/// leaving a blocked <see cref="Console.ReadLine"/> that would swallow the next line.
/// </summary>
internal sealed class ConsoleChatInput : IChatInput
{
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Queue<string?> _typedAhead = new();
    private readonly Lock _gate = new();
    private Task? _reader;

    public bool Echoes => !Console.IsInputRedirected;

    public async ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _reader ??= Task.Factory.StartNew(Read, TaskCreationOptions.LongRunning);
        if (fresh)
        {
            while (_lines.Reader.TryRead(out var early))
                lock (_gate)
                    _typedAhead.Enqueue(early);
        }
        else
        {
            lock (_gate)
                if (_typedAhead.TryDequeue(out var queued))
                    return queued;
        }
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private void Read()
    {
        while (Console.ReadLine() is { } line)
            _lines.Writer.TryWrite(line);
        _lines.Writer.TryComplete();
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
