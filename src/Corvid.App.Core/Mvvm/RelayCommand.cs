using System.Windows.Input;

namespace Corvid.App.Core.Mvvm;

/// <summary>
/// A command over a delegate, with an optional guard. The view model calls <see cref="NotifyCanExecuteChanged"/> when the
/// guard's inputs change, so a bound button enables and disables itself. <see cref="Execute"/> checks the guard too: a
/// keyboard accelerator or a test that calls it directly gets the same answer as the button.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            execute();
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A command with a typed parameter. A parameter of another type cannot execute, and neither can null where
/// <typeparamref name="T"/> is a value type, so a mis-bound <c>CommandParameter</c> disables the button instead of
/// throwing.
/// </summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => TryGet(parameter, out var value) && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (TryGet(parameter, out var value) && (canExecute?.Invoke(value) ?? true))
            execute(value);
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static bool TryGet(object? parameter, out T value)
    {
        switch (parameter)
        {
            case T typed:
                value = typed;
                return true;
            case null when default(T) is null:
                value = default!;
                return true;
            default:
                value = default!;
                return false;
        }
    }
}
