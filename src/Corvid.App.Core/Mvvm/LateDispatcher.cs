namespace Corvid.App.Core.Mvvm;

/// <summary>
/// A dispatcher whose UI thread comes later (G-1: an attached window connects before its window exists, and the connection
/// already posts the engine's changes). Posts made before <see cref="Bind"/> are kept in order and run on the bound
/// dispatcher, before anything posted after.
/// </summary>
public sealed class LateDispatcher : IDispatcher
{
    private readonly Lock _gate = new();
    private readonly List<Action> _early = [];
    private IDispatcher? _target;

    public bool HasThreadAccess => Volatile.Read(ref _target)?.HasThreadAccess ?? false;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_target is null)
            {
                _early.Add(action);
                return true;
            }
            return _target.Post(action);
        }
    }

    /// <summary>The UI thread: what was posted so far runs there, in order, then every later post.</summary>
    /// <exception cref="InvalidOperationException">It was bound already.</exception>
    public void Bind(IDispatcher target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (_target is not null)
                throw new InvalidOperationException("The dispatcher is bound already.");
            foreach (var action in _early)
                target.Post(action);
            _early.Clear();
            Volatile.Write(ref _target, target);
        }
    }
}
