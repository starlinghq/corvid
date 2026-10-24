using Corvid.App.Core.Mvvm;
using Microsoft.UI.Dispatching;

namespace Corvid.App.Infrastructure;

/// <summary>The core's <see cref="IDispatcher"/> over the window's <see cref="DispatcherQueue"/>.</summary>
internal sealed class UiDispatcher(DispatcherQueue queue) : IDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return queue.TryEnqueue(() => action());
    }
}
