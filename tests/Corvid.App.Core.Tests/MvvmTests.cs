using System.ComponentModel;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Tests;

public sealed class MvvmTests
{
    private sealed class Probe : ObservableObject
    {
        private string _name = "";

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }

    [Fact]
    public void AChangedValueRaisesItsNameOnceAndAnEqualOneNothing()
    {
        var probe = new Probe();
        var raised = new List<string?>();
        probe.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        probe.Name = "corvid";
        probe.Name = "corvid";

        Assert.Equal(["Name"], raised);
    }

    [Fact]
    public void ARelayCommandRunsOnlyWhileItsGuardAllows()
    {
        var allowed = false;
        var runs = 0;
        var command = new RelayCommand(() => runs++, () => allowed);
        var changes = 0;
        command.CanExecuteChanged += (_, _) => changes++;

        command.Execute(null);
        allowed = true;
        command.NotifyCanExecuteChanged();
        command.Execute(null);

        Assert.Equal(1, runs);
        Assert.Equal(1, changes);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void ATypedCommandRefusesAParameterOfAnotherType()
    {
        var seen = new List<int>();
        var command = new RelayCommand<int>(seen.Add, value => value > 0);

        Assert.False(command.CanExecute("3"));
        Assert.False(command.CanExecute(null));
        Assert.False(command.CanExecute(-1));
        command.Execute("3");
        command.Execute(null);
        command.Execute(-1);
        command.Execute(3);

        Assert.Equal([3], seen);
    }

    [Fact]
    public void ATypedCommandOverAReferenceTypeTakesNull()
    {
        var seen = new List<string?>();
        var command = new RelayCommand<string?>(seen.Add);

        command.Execute(null);

        Assert.Equal([null], seen);
    }

    [Fact]
    public async Task AnAsyncCommandCannotStartAgainWhileItRuns()
    {
        var release = new TaskCompletionSource();
        var starts = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            starts++;
            await release.Task;
        });
        var running = new List<bool>();
        command.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning))
                running.Add(command.IsRunning);
        };

        var first = command.ExecuteAsync();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        release.SetResult();
        await first;

        Assert.Equal(1, starts);
        Assert.Equal([true, false], running);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AFailedRunKeepsItsErrorAndAButtonDoesNotThrow()
    {
        var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("engine refused")));

        await Assert.ThrowsAsync<InvalidOperationException>(command.ExecuteAsync);
        Assert.Equal("engine refused", command.Error?.Message);

        // Through ICommand, as a button calls it: no exception escapes, the error is still there for the view.
        ((System.Windows.Input.ICommand)command).Execute(null);
        await WaitUntil(() => !command.IsRunning);
        Assert.Equal("engine refused", command.Error?.Message);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task ACancelledRunIsNotAnError()
    {
        var command = new AsyncRelayCommand(async token => await Task.Delay(Timeout.Infinite, token));

        var run = command.ExecuteAsync();
        command.Cancel();
        await run;

        Assert.Null(command.Error);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task InvokeRunsAtOnceOnTheUiThreadAndAfterThePostOtherwise()
    {
        Assert.Equal(4, await new InlineDispatcher().InvokeAsync(() => 4));

        var queued = new QueuedDispatcher();
        var task = queued.InvokeAsync(() => 5);
        Assert.False(task.IsCompleted);
        queued.RunAll();
        Assert.Equal(5, await task);

        var failing = queued.InvokeAsync(new Action(() => throw new InvalidOperationException("view gone")));
        queued.RunAll();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);

        queued.ShutDown = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued.InvokeAsync(() => 6));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }

    /// <summary>A UI thread that is elsewhere: posts queue until the test runs them.</summary>
    private sealed class QueuedDispatcher : IDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool ShutDown { get; set; }

        public bool HasThreadAccess => false;

        public bool Post(Action action)
        {
            if (ShutDown)
                return false;
            _queue.Enqueue(action);
            return true;
        }

        public void RunAll()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }
    }
}
