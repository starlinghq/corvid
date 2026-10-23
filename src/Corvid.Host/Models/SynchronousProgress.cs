namespace Corvid.Host.Models;

/// <summary>
/// An <see cref="IProgress{T}"/> that runs its handler on the reporting thread, one report at a time (C-039).
/// <see cref="Progress{T}"/> without a synchronization context posts each report to the thread pool, so a handler
/// writing to a console races whatever the caller writes after the operation returns. Here every report has been
/// handled when <see cref="Report"/> returns, and reports from parallel fetches never overlap.
/// </summary>
/// <remarks>The handler runs under a lock: a slow handler slows the reporter, and a throwing one throws from <see cref="Report"/>.</remarks>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate)
            _handler(value);
    }
}
