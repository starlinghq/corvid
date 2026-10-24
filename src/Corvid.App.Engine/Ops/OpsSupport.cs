using System.Collections.Concurrent;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Engine.Ops;

/// <summary>
/// A service's <c>Changed</c> event as the ops pages need it: raised on the UI thread through the dispatcher, from whatever
/// thread noticed the change (the scheduler's, a store's caller), and coalesced, so a burst of changes (the scheduler
/// replaying its completed runs to a new subscriber) costs the page one reload. Nothing is raised once disposed.
/// </summary>
internal sealed class ChangeSignal(IDispatcher dispatcher, Action raise) : IDisposable
{
    private int _posted;
    private volatile bool _disposed;

    public void Raise()
    {
        if (_disposed || Interlocked.Exchange(ref _posted, 1) != 0)
            return;
        if (!dispatcher.Post(() =>
        {
            Volatile.Write(ref _posted, 0);
            if (!_disposed)
                raise();
        }))
            Volatile.Write(ref _posted, 0);
    }

    public void Dispose() => _disposed = true;
}

/// <summary>
/// Background work a service started and nobody awaits (a subscription made once the runtime is up): kept until it ends,
/// and its failure written to the app's log ("corvid app: …" on standard error), never lost.
/// </summary>
internal sealed class BackgroundWork
{
    private readonly ConcurrentDictionary<Task, byte> _work = new();

    public void Track(Task task)
    {
        _work[task] = 0;
        _ = task.ContinueWith(done =>
        {
            _work.TryRemove(done, out _);
            if (done.Exception is { } failure)
                Log(failure.GetBaseException().Message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits for what is running now (tests).</summary>
    public Task IdleAsync() => Task.WhenAll(_work.Keys).ContinueWith(_ => { }, CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public static void Log(string text) => Console.Error.WriteLine("corvid app: " + text);
}
