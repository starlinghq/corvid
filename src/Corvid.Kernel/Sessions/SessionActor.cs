using System.Threading.Channels;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models;

namespace Corvid.Kernel.Sessions;

/// <summary>A finished turn, or the reason it could not run, for the surface that posted the envelope.</summary>
/// <param name="Result">Null when the turn threw; <paramref name="Error"/> says why.</param>
public sealed record Delivery(Envelope Request, TurnResult? Result, Exception? Error = null);

/// <summary>Where a session's answers go: the surface, or the gateway's delivery ledger (C-020).</summary>
public interface IDeliverySink
{
    ValueTask DeliverAsync(Delivery delivery, CancellationToken cancellationToken);
}

public sealed record SessionActorOptions
{
    public static SessionActorOptions Default { get; } = new();

    /// <summary>Messages queued or running per session before <see cref="SessionActor.SubmitAsync"/> waits (backpressure).</summary>
    public int MaxPendingMessages { get; init; } = 16;

    /// <summary>Started when <c>embedder.text</c> switches (plan §4.3). Memory is process-wide, so sessions share one.</summary>
    public EmbedderSwapJob? EmbedderSwap { get; init; }
}

/// <summary>
/// One session's actor (plan §3.3): a single-reader <see cref="Channel{T}"/> of commands. The loop owns the queue of
/// messages and the running turn; it starts each turn as a task and keeps reading, so a cancel or a <c>/model</c> switch is
/// handled while a turn waits on a model or an approval prompt. Turns run one at a time, in arrival order.
/// </summary>
public sealed class SessionActor : IAsyncDisposable
{
    private readonly TurnRunner _runner;
    private readonly SessionModelSlots _slots;
    private readonly IDeliverySink _sink;
    private readonly SessionActorOptions _options;
    private readonly Channel<SessionCommand> _commands = Channel.CreateUnbounded<SessionCommand>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly SemaphoreSlim _capacity;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;

    // Loop state: touched only by the loop.
    private readonly Queue<Envelope> _pending = new();
    private CancellationTokenSource? _turnCancellation;
    private Task? _turn;

    public SessionActor(TurnRunner runner, SessionModelSlots slots, IDeliverySink sink, SessionActorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(sink);
        if (slots.SessionId != runner.SessionId)
            throw new ArgumentException("The slots belong to another session.", nameof(slots));
        _runner = runner;
        _slots = slots;
        _sink = sink;
        _options = options ?? SessionActorOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxPendingMessages);
        _capacity = new SemaphoreSlim(_options.MaxPendingMessages, _options.MaxPendingMessages);
        _loop = Task.Run(LoopAsync);
    }

    public string SessionId => _runner.SessionId;

    /// <summary>Queues a message. Waits while the session already holds <see cref="SessionActorOptions.MaxPendingMessages"/>.</summary>
    public async ValueTask SubmitAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SessionId != SessionId)
            throw new ArgumentException("The envelope is for another session.", nameof(envelope));
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_commands.Writer.TryWrite(new SubmitMessage(envelope)))
        {
            _capacity.Release();
            throw new ObjectDisposedException(nameof(SessionActor));
        }
    }

    /// <summary>Cancels the running turn, if any. Queued messages still run.</summary>
    public void CancelTurn() => _commands.Writer.TryWrite(new CancelRunning());

    /// <summary>
    /// A <c>/model</c> switch (plan §4.2). It runs beside a turn: it waits for running tool calls, not for calls parked at an
    /// approval prompt. A switch of <c>embedder.text</c> starts memory's re-embed job.
    /// </summary>
    public Task<SwitchResult> SwitchModelAsync(string slot, ModelBinding target, bool pin = false, CancellationToken cancellationToken = default)
    {
        var command = new SwitchModel(slot, target, pin, cancellationToken);
        if (!_commands.Writer.TryWrite(command))
            throw new ObjectDisposedException(nameof(SessionActor));
        return command.Completion.Task;
    }

    /// <summary>Completes when every queued message has been answered.</summary>
    public Task IdleAsync(CancellationToken cancellationToken = default)
    {
        var command = new WhenIdle();
        if (!_commands.Writer.TryWrite(command))
            return Task.CompletedTask;
        return command.Completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _lifetime.Dispose();
        _capacity.Dispose();
    }

    private async Task LoopAsync()
    {
        var idle = new List<TaskCompletionSource>();
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                switch (command)
                {
                    case SubmitMessage submit:
                        _pending.Enqueue(submit.Envelope);
                        break;
                    case CancelRunning:
                        _turnCancellation?.Cancel();
                        break;
                    case SwitchModel switching:
                        _ = SwitchAsync(switching);
                        break;
                    case TurnFinished:
                        _turn = null;
                        _turnCancellation?.Dispose();
                        _turnCancellation = null;
                        _capacity.Release();
                        break;
                    case WhenIdle waiting:
                        idle.Add(waiting.Completion);
                        break;
                }
                if (_turn is null && _pending.TryDequeue(out var next))
                {
                    _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _turn = RunTurnAsync(next, _turnCancellation.Token);
                }
                if (_turn is null && _pending.Count == 0)
                {
                    foreach (var waiter in idle)
                        waiter.TrySetResult();
                    idle.Clear();
                }
            }
        }
        finally
        {
            _turnCancellation?.Cancel();
            if (_turn is not null)
                await _turn.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var waiter in idle)
                waiter.TrySetCanceled();
        }
    }

    private async Task RunTurnAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // Off the loop: the turn's synchronous prefix (the log append) must not delay the next command.
        await Task.Yield();
        Delivery delivery;
        try
        {
            delivery = new Delivery(envelope, await _runner.RunAsync(envelope, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            delivery = new Delivery(envelope, null, exception);
        }
        catch (OperationCanceledException)
        {
            _commands.Writer.TryWrite(new TurnFinished());
            return;
        }
        try
        {
            await _sink.DeliverAsync(delivery, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A surface that fails to deliver must not stop the session; the answer is in the log.
        }
        finally
        {
            _commands.Writer.TryWrite(new TurnFinished());
        }
    }

    private async Task SwitchAsync(SwitchModel command)
    {
        try
        {
            var result = await _slots.SwitchAsync(command.Slot, command.Target, SwitchReason.User, command.Pin, command.Cancellation)
                .ConfigureAwait(false);
            if (result.Status == SwitchStatus.Switched && command.Slot == ModelSlots.TextEmbedder && _options.EmbedderSwap is { } job)
                job.Start(result.To!.Id);
            command.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            command.Completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            command.Completion.TrySetException(exception);
        }
    }

    private abstract record SessionCommand;

    private sealed record SubmitMessage(Envelope Envelope) : SessionCommand;

    private sealed record CancelRunning : SessionCommand;

    private sealed record SwitchModel(string Slot, ModelBinding Target, bool Pin, CancellationToken Cancellation) : SessionCommand
    {
        public TaskCompletionSource<SwitchResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record TurnFinished : SessionCommand;

    private sealed record WhenIdle : SessionCommand
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
