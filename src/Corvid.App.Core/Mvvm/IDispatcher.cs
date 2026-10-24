namespace Corvid.App.Core.Mvvm;

/// <summary>
/// The UI thread, as the core sees it. View models change only there; a service that hears from the engine on another
/// thread (a streamed token, a finished tool call) posts the change through this. The head implements it over Uno's
/// <c>DispatcherQueue</c>, so the core needs no Uno type; tests use <see cref="InlineDispatcher"/>.
/// </summary>
public interface IDispatcher
{
    /// <summary>Whether the caller is on the UI thread, where a change can be made directly.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues the action on the UI thread and returns at once; false when the queue has shut down.</summary>
    bool Post(Action action);
}

public static class DispatcherExtensions
{
    /// <summary>Runs the action on the UI thread, directly when already there; completes when it has run.</summary>
    public static Task InvokeAsync(this IDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return dispatcher.InvokeAsync<object?>(() =>
        {
            action();
            return null;
        });
    }

    /// <summary>Computes a value on the UI thread, directly when already there; completes with it, or with its exception.</summary>
    public static Task<T> InvokeAsync<T>(this IDispatcher dispatcher, Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(function);
        if (dispatcher.HasThreadAccess)
        {
            try
            {
                return Task.FromResult(function());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = dispatcher.Post(() =>
        {
            try
            {
                completion.SetResult(function());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        if (!posted)
            completion.TrySetException(new InvalidOperationException("The UI thread's queue has shut down."));
        return completion.Task;
    }
}

/// <summary>A dispatcher for tests and headless use: every action runs at once on the calling thread.</summary>
public sealed class InlineDispatcher : IDispatcher
{
    public bool HasThreadAccess => true;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return true;
    }
}
