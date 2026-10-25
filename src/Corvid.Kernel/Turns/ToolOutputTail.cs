using System.Text;
using Corvid.Secrets;

namespace Corvid.Kernel.Turns;

/// <summary>
/// K-2: one running call's output as a bounded tail for surfaces. Text written to it (from the tool's own threads, in any
/// pieces) is kept to the last few thousand characters; at most once per interval the last lines, credentials scrubbed with
/// the log's egress rules, are published as the tail. Output that arrives between reports is published when the interval
/// ends, so a command that prints a line and then waits still shows it. Nothing is published after <see cref="Dispose"/>.
/// </summary>
internal sealed class ToolOutputTail : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly int _maxChars;
    private readonly int _maxLines;
    private readonly Action<string> _publish;
    private readonly StringBuilder _buffer = new();
    private ITimer? _timer;
    private long _lastPublished;
    private bool _published;
    private bool _dirty;
    private bool _closed;
    private bool _pendingReturn;

    public ToolOutputTail(TimeProvider time, TimeSpan interval, int maxChars, int maxLines, Action<string> publish)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        _time = time;
        _interval = interval;
        _maxChars = maxChars;
        _maxLines = maxLines;
        _publish = publish;
    }

    /// <summary>Adds output. Never throws for the tool: a tail that cannot be published is the surface's loss, not the call's.</summary>
    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        lock (_gate)
        {
            if (_closed)
                return;
            Append(text);
            _dirty = true;
            var elapsed = _published ? _time.GetElapsedTime(_lastPublished) : _interval;
            if (elapsed >= _interval)
            {
                PublishLocked();
                return;
            }
            // A report is due when the interval ends; output until then joins it.
            _timer ??= _time.CreateTimer(_ => Flush(), null, _interval - elapsed, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The tail as it would be published now: the last lines, scrubbed.</summary>
    public string Snapshot()
    {
        lock (_gate)
            return Render();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (!_closed && _dirty)
                PublishLocked();
        }
    }

    private void PublishLocked()
    {
        _dirty = false;
        _published = true;
        _lastPublished = _time.GetTimestamp();
        var tail = Render();
        try
        {
            _publish(tail);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A surface that fails to show the tail must not fail the tool.
        }
    }

    private void Append(string text)
    {
        foreach (var c in text)
        {
            // Line ends as the terminal shows them: \r\n and a lone \r (a progress line redrawn) both end a line.
            if (c == '\r')
            {
                _pendingReturn = true;
                continue;
            }
            if (_pendingReturn)
            {
                _pendingReturn = false;
                _buffer.Append('\n');
                if (c == '\n')
                    continue;
            }
            _buffer.Append(c);
        }
        // Twice the shown size is kept, so the scrub sees a credential whole even when the tail starts inside it.
        var keep = _maxChars * 2;
        if (_buffer.Length > keep)
            _buffer.Remove(0, _buffer.Length - keep);
    }

    private string Render()
    {
        var text = CredentialScrub.Scrub(_buffer.ToString(), CredentialDetector.Egress).Text;
        if (text.Length > _maxChars)
            text = text[^_maxChars..];
        if (text.Length > 0 && char.IsLowSurrogate(text[0]))
            text = text[1..];
        var lines = text.Split('\n');
        // A trailing newline ends the last line rather than starting an empty one.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var from = Math.Max(0, count - _maxLines);
        return string.Join('\n', lines[from..count]);
    }
}
