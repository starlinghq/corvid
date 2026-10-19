namespace Corvid.Kernel.Scheduling;

/// <summary>Queue order on a device (plan §3.3). Lower values run first; within a priority, first in, first out.</summary>
public enum DevicePriority
{
    /// <summary>The generator answering the user.</summary>
    Interactive = 0,
    /// <summary>The decision fabric: decider, extractor and embedder calls at checkpoints.</summary>
    Fabric = 1,
    Subagent = 2,
    /// <summary>Consolidation, indexing and re-embedding (plan §9.5).</summary>
    Background = 3,
}

/// <summary>
/// One device's queue (a GPU, or a CPU pool) with dedicated threads (plan §3.3). Starling's model calls are synchronous native
/// work; they run here, never on a session actor or the thread pool, and the highest-priority waiting item runs next. Items
/// already running are never pre-empted.
/// </summary>
/// <remarks>
/// Work queued with <see cref="RunAsync{T}"/> completes its task with continuations forced off the device threads. Tasks
/// queued to <see cref="GetTaskScheduler"/> complete while this scheduler is current, which the runtime treats as no place
/// to inline an await continuation, so callers resume on the thread pool there too. Work must not wait synchronously for
/// other work on the same device: with one thread that deadlocks.
/// </remarks>
public sealed class DeviceScheduler : IDisposable
{
    private static readonly int PriorityCount = Enum.GetValues<DevicePriority>().Length;

    [ThreadStatic]
    private static DeviceScheduler? t_current;

    private readonly Queue<WorkItem>[] _queues;
    // Monitor, not System.Threading.Lock: workers wait on it with Monitor.Wait.
    private readonly object _gate = new();
    private readonly Thread[] _threads;
    private readonly DeviceTaskScheduler[] _taskSchedulers;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _closed;

    /// <param name="device">The device's name, for example <c>cpu</c> or <c>cuda:0</c>; it names the threads.</param>
    /// <param name="threads">Worker threads. One by default: Starling serializes native work on a device anyway.</param>
    public DeviceScheduler(string device, int threads = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        Device = device;
        _queues = [.. Enumerable.Range(0, PriorityCount).Select(_ => new Queue<WorkItem>())];
        _taskSchedulers = [.. Enumerable.Range(0, PriorityCount).Select(priority => new DeviceTaskScheduler(this, (DevicePriority)priority))];
        _threads = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"corvid-device {device} #{i}" };
            _threads[i].Start();
        }
    }

    public string Device { get; }

    public int ThreadCount => _threads.Length;

    /// <summary>True on one of this device's threads.</summary>
    public bool IsCurrentThread => ReferenceEquals(t_current, this);

    /// <summary>Items waiting at a priority, for <c>/status</c>.</summary>
    public int QueueDepth(DevicePriority priority)
    {
        lock (_gate)
            return _queues[Index(priority)].Count;
    }

    /// <summary>Runs synchronous work on a device thread. The token is checked before the work starts and passed to it.</summary>
    public Task<T> RunAsync<T>(DevicePriority priority, Func<CancellationToken, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new DelegateItem<T>(work, cancellationToken, _shutdown.Token);
        Enqueue(priority, item);
        return item.Task;
    }

    public Task RunAsync(DevicePriority priority, Action<CancellationToken> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<bool>(priority, token =>
        {
            work(token);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// A <see cref="TaskScheduler"/> that queues on this device at <paramref name="priority"/>, for adapters that take one
    /// (Starling's GLiNER2 extractor, C-010).
    /// </summary>
    public TaskScheduler GetTaskScheduler(DevicePriority priority) => _taskSchedulers[Index(priority)];

    /// <summary>Stops accepting work, cancels queued <see cref="RunAsync{T}"/> items, runs queued tasks, and joins the threads.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            // Cancelled before closing, so no queued item can start after a caller has seen the device refuse work.
            _shutdown.Cancel();
            _closed = true;
            Monitor.PulseAll(_gate);
        }
        foreach (var thread in _threads)
            if (thread != Thread.CurrentThread)
                thread.Join();
        _shutdown.Dispose();
    }

    internal void Enqueue(DevicePriority priority, WorkItem item)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _queues[Index(priority)].Enqueue(item);
            Monitor.Pulse(_gate);
        }
    }

    internal IEnumerable<Task> ScheduledTasks(DevicePriority priority)
    {
        lock (_gate)
            return [.. _queues[Index(priority)].OfType<TaskItem>().Select(item => item.Task)];
    }

    private void Work()
    {
        t_current = this;
        while (true)
        {
            WorkItem? item = null;
            lock (_gate)
            {
                while (item is null)
                {
                    foreach (var queue in _queues)
                        if (queue.TryDequeue(out item))
                            break;
                    if (item is null)
                    {
                        // Queued tasks still run after Dispose: a task given to a TaskScheduler must run or it never completes.
                        if (_closed)
                            return;
                        Monitor.Wait(_gate);
                    }
                }
            }
            item.Execute();
        }
    }

    private static int Index(DevicePriority priority) =>
        Enum.IsDefined(priority) ? (int)priority : throw new ArgumentOutOfRangeException(nameof(priority));

    internal abstract class WorkItem
    {
        public abstract void Execute();
    }

    private sealed class DelegateItem<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken, CancellationToken shutdown)
        : WorkItem
    {
        // Continuations run asynchronously, so a caller never resumes on the device thread.
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public override void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }
            if (shutdown.IsCancellationRequested)
            {
                _completion.TrySetCanceled(shutdown);
                return;
            }
            try
            {
                _completion.TrySetResult(work(cancellationToken));
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }

    internal sealed class TaskItem(DeviceTaskScheduler scheduler, Task task) : WorkItem
    {
        public Task Task { get; } = task;

        public override void Execute() => scheduler.Execute(Task);
    }
}

/// <summary>Queues tasks on a device at one priority. Inlining is allowed only on the device's own threads.</summary>
internal sealed class DeviceTaskScheduler(DeviceScheduler device, DevicePriority priority) : TaskScheduler
{
    public override int MaximumConcurrencyLevel => device.ThreadCount;

    protected override void QueueTask(Task task) => device.Enqueue(priority, new DeviceScheduler.TaskItem(this, task));

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        !taskWasPreviouslyQueued && device.IsCurrentThread && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => device.ScheduledTasks(priority);

    internal void Execute(Task task) => TryExecuteTask(task);
}
