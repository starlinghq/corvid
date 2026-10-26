using System.Collections.Concurrent;

namespace Corvid.App.Core.Mvvm;

/// <summary>
/// A dispatcher with no UI (G-1: <c>corvid serve --attach</c> hosting the engine's session service): one thread of its own
/// runs every posted action in the order posted, as a UI thread would, so an engine that posts its updates through it
/// delivers them in order. An action that throws is reported to <paramref name="onError"/> and the thread goes on.
/// Disposing stops it after the actions already posted; a post after that returns false.
/// </summary>
public sealed class SerialDispatcher : IDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private readonly Action<Exception>? _onError;

    public SerialDispatcher(string name = "corvid dispatcher", Action<Exception>? onError = null)
    {
        _onError = onError;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool HasThreadAccess => Thread.CurrentThread == _thread;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return _queue.TryAdd(action);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!HasThreadAccess)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _onError?.Invoke(exception);
            }
        }
    }
}
