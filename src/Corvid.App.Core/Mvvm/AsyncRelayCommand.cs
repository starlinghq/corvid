using System.Windows.Input;

namespace Corvid.App.Core.Mvvm;

/// <summary>
/// A command over an asynchronous delegate. It cannot start again while a run is in flight (a second click on "Download"
/// must not start a second download), exposes <see cref="IsRunning"/> for a spinner, and cancels the run it started.
/// A run that fails keeps its exception in <see cref="Error"/> for the view to show; <see cref="ExecuteAsync"/> also
/// rethrows it to its caller, while a button's <see cref="ICommand.Execute"/> does not, so a failed action never takes the
/// window down. Call it on the UI thread: it changes observable state.
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;
    private Exception? _error;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(IgnoringToken(execute), canExecute)
    {
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>True from the start of a run until it completes, fails or is cancelled.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                NotifyCanExecuteChanged();
        }
    }

    /// <summary>The last run's failure; cleared when a new run starts. A cancelled run is not a failure.</summary>
    public Exception? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync();
        }
        catch (Exception)
        {
            // Kept in Error, where the view shows it; an exception escaping an async void would end the process.
        }
    }

    /// <summary>Runs the delegate unless a run is in flight or the guard refuses; completes when the run does.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Error = null;
        IsRunning = true;
        try
        {
            await _execute(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = exception;
            throw;
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>Asks the run in flight to stop; its delegate decides when, through the token it was given.</summary>
    public void Cancel() => _cancellation?.Cancel();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static Func<CancellationToken, Task> IgnoringToken(Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return _ => execute();
    }
}
