using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;

namespace Corvid.Gateway;

public enum AdapterState
{
    Starting,
    Connecting,
    Connected,
    /// <summary>The last connect or receive failed (or timed out); the adapter is retried after a backoff.</summary>
    Retrying,
    Stopped,
}

/// <param name="Detail">Why it is retrying, or what it is connected as.</param>
public sealed record AdapterStatus(string Name, AdapterState State, string? Detail, DateTimeOffset Since);

/// <summary>
/// The gateway (plan §12): one host for every enabled adapter. Each adapter is created only when the host starts, in its own
/// scope (instance, cancellation, task), connected under <see cref="GatewayOptions.ConnectTimeout"/> and retried with a
/// backoff when it fails, so a hanging connect never stalls the host or another adapter (#67498, #72589 in Hermes).
/// <para>
/// Inbound: an unaddressed group message is dropped; an unpaired sender gets only the pairing flow (a code the owner approves
/// on the machine), and nothing they write reaches a session or a model. A paired sender's message goes to the chat's own
/// session (<see cref="GatewaySessions.For"/>): the owner's as the user's, anyone else's as outside content, which taints the
/// session (§10.3) and whose turn is offered only the adapter's <see cref="AdapterOptions.GuestTools"/> (none by default) and
/// none of the owner's skills or memory; the owner's own turns keep the whole toolset. Slash commands from the shared registry, and <c>/approve</c> and <c>/deny</c> for an approval waiting
/// in that chat, are the owner's alone.
/// </para>
/// <para>
/// Outbound: every answer, notice and job result goes through the <see cref="DeliveryLedger"/>, and each adapter's delivery
/// loop sends what is open for it in order per chat, at least once. <c>deliver</c> routes an answer: reply, reply in a
/// thread, the adapter's home channel (never from a tainted turn: that would be a new recipient), or nothing.
/// </para>
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    /// <summary>The gateway store key holding the ledger's low-water mark.</summary>
    public const string LowWaterKey = "ledger:low_water";

    private readonly ImmutableArray<GatewayAdapterRegistration> _registrations;
    private readonly IGatewayAgent _agent;
    private readonly PairingStore _pairing;
    private readonly DeliveryLedger _ledger;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Supervisor> _supervisors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _opened = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _prompters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly CancellationTokenSource _receiving = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _ledgerSignal = new(0);
    private FileStream? _lock;
    private Task? _ledgerLoop;
    private long _storedLowWater = -1;
    private bool _started;

    public GatewayHost(IEnumerable<GatewayAdapterRegistration> adapters, IGatewayAgent agent, PairingStore pairing, DeliveryLedger ledger,
        GatewayOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(ledger);
        _registrations = [.. adapters];
        if (_registrations.Select(registration => registration.Name).Distinct(StringComparer.Ordinal).Count() != _registrations.Length)
            throw new ArgumentException("Two adapters have the same name.", nameof(adapters));
        _agent = agent;
        _pairing = pairing;
        _ledger = ledger;
        _options = options ?? GatewayOptions.Default;
        _time = time ?? TimeProvider.System;
        foreach (var registration in _registrations)
            _supervisors[registration.Name] = new Supervisor(this, registration);
    }

    /// <summary>Told about connects, failures and deliveries given up, for a surface to print.</summary>
    public Action<string>? Notice { get; init; }

    public IReadOnlyList<AdapterStatus> Adapters => [.. _supervisors.Values.Select(supervisor => supervisor.Status)];

    /// <summary>Open deliveries, for <c>corvid gateway status</c> and tests.</summary>
    public IReadOnlyList<PendingDelivery> PendingDeliveries => _ledger.Pending;

    /// <summary>
    /// Starts every adapter on its own task and returns without waiting for any of them to connect. Only one gateway may
    /// run against a data directory: a lock beside <c>gateway.db</c> decides, as the scheduler's does beside <c>jobs.db</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another gateway holds the lock.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            throw new InvalidOperationException("The gateway is already started.");
        try
        {
            _lock = new FileStream(_pairing.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another corvid gateway is running against this data directory.", exception);
        }
        _started = true;
        var lowWater = long.TryParse(_pairing.GetState(LowWaterKey), NumberStyles.None, CultureInfo.InvariantCulture, out var stored) ? stored : 0;
        _storedLowWater = lowWater;
        _ledger.Catch(lowWater);
        foreach (var supervisor in _supervisors.Values)
            supervisor.Start();
        _ledgerLoop = Task.Run(LedgerLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops receiving, gives answers still being written <see cref="GatewayOptions.StopTimeout"/> to reach the ledger (and
    /// be sent), then stops delivering and the adapters. Whatever is still open is sent after the next start.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_started || _lifetime.IsCancellationRequested)
            return;
        await _receiving.CancelAsync().ConfigureAwait(false);
        var replies = Task.WhenAll(_inFlight.Keys);
        await replies.WaitAsync(_options.StopTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // Answers that reached the ledger in that time are sent now, while the adapters are still connected.
        Signal();
        await DrainAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var approval in _approvals.Values)
            approval.Answer.TrySetResult(ApprovalAnswer.Deny());
        var stopping = _supervisors.Values.Select(supervisor => supervisor.StopAsync()).ToList();
        if (_ledgerLoop is not null)
            stopping.Add(_ledgerLoop);
        await Task.WhenAll(stopping).WaitAsync(_options.StopTimeout + _options.StopTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var prompter in _prompters.Values)
            prompter.Dispose();
        _prompters.Clear();
        try
        {
            // Our own resolutions, so the mark moves past them.
            _ledger.Catch();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
        SaveLowWater();
        _lock?.Dispose();
        _lock = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _receiving.Dispose();
        _lifetime.Dispose();
        _ledgerSignal.Dispose();
    }

    /// <summary>Waits until no delivery for a connected adapter is open, or the stop timeout passes.</summary>
    private async Task DrainAsync()
    {
        var until = _time.GetUtcNow() + _options.StopTimeout;
        while (_time.GetUtcNow() < until)
        {
            var open = _ledger.Pending.Any(pending => _supervisors.TryGetValue(pending.Delivery.Adapter, out var supervisor)
                && supervisor.Current is not null && !supervisor.Waiting(pending.Delivery.DeliveryId));
            if (!open)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), _time).ConfigureAwait(false);
        }
    }

    // ---- inbound ----

    private async Task ReceiveAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        var adapter = _options.For(message.Adapter);
        var text = message.Text.Trim();
        if (text.Length == 0 || !message.Direct && !message.Addressed)
            return;

        var role = _pairing.RoleOf(adapter, message.SenderId);
        if (role == SenderRole.None)
        {
            // Unpaired: the pairing flow and nothing else. Their text is not stored and reaches no session or model.
            var ticket = _pairing.Request(message.Adapter, message.SenderId, message.SenderName, message.ChatId);
            if (ticket is { Notify: true, Request: { } request })
            {
                Queue(message, PairingNotice(request), "notice");
                Notice?.Invoke($"{message.Adapter}: {message.SenderName ?? message.SenderId} ({message.SenderId}) asked to pair; " +
                    $"approve with `corvid gateway pair approve {PairingStore.Display(request.Code)}`.");
            }
            return;
        }

        var sessionId = GatewaySessions.For(message.Adapter, message.ChatId);
        await OpenAsync(sessionId, message, adapter, cancellationToken).ConfigureAwait(false);

        if (IsApprovalAnswer(text) || _agent.IsCommand(text))
        {
            // Commands reach /undo, /rollback, /model and approvals: the owner's alone (C-014 left this to pairing).
            if (role != SenderRole.Owner)
            {
                Queue(message, "Commands are for the owner of this agent.", "notice");
                return;
            }
            if (IsApprovalAnswer(text))
            {
                Answer(message, sessionId, text);
                return;
            }
            // Off the receive loop: /undo waits for a running turn, which may itself wait for an /approve still to come.
            Track(RunCommandAsync(message, sessionId, text));
            return;
        }

        // Anyone but the owner gets the adapter's guest tools only (none by default): taint asks before network and sends, but
        // would not stop a read-only file tool showing the owner's workspace to a guest.
        // A paired guest's own allowlist (G-14) replaces the adapter's for them.
        var reply = await _agent.PostAsync(sessionId, text, role == SenderRole.Owner ? null
                : new GuestScope(_pairing.GuestTools(message.Adapter, message.SenderId) ?? (IEnumerable<string>)adapter.GuestTools), cancellationToken)
            .ConfigureAwait(false);
        Track(DeliverAsync(message, sessionId, adapter, reply));
    }

    private static string PairingNotice(PairingRequest request) =>
        $"This is a private agent. To talk to it, its owner must approve you: on their machine, " +
        $"`corvid gateway pair approve {PairingStore.Display(request.Code)}`. The code expires at " +
        $"{request.ExpiresAt.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC.";

    private async Task OpenAsync(string sessionId, InboundMessage message, AdapterOptions adapter, CancellationToken cancellationToken)
    {
        var opening = _opened.GetOrAdd(sessionId, _ => OpenCoreAsync(sessionId, message, adapter));
        try
        {
            await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (opening.IsFaulted || opening.IsCanceled)
        {
            // The next message tries again. Removed here rather than inside the open: an open that fails before its first
            // await would remove itself before GetOrAdd stored it, and the chat would fail for the gateway's lifetime.
            _opened.TryRemove(KeyValuePair.Create(sessionId, opening));
            throw;
        }
    }

    private async Task OpenCoreAsync(string sessionId, InboundMessage message, AdapterOptions adapter)
    {
        await _agent.OpenAsync(sessionId, message.Adapter, adapter.SettingsFor(message.ChatId), _lifetime.Token).ConfigureAwait(false);
        _prompters[sessionId] = _agent.AttachPrompter(sessionId, new ChatPrompter(this, sessionId, message.Adapter, message.ChatId));
    }

    private async Task RunCommandAsync(InboundMessage message, string sessionId, string line)
    {
        string output;
        try
        {
            output = await _agent.RunCommandAsync(sessionId, line, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            output = "That command failed: " + exception.Message;
        }
        if (output.Length > 0)
            Queue(message, output, "notice");
    }

    private async Task DeliverAsync(InboundMessage message, string sessionId, AdapterOptions adapter, Task<AgentReply> pending)
    {
        AgentReply reply;
        try
        {
            reply = await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            reply = new AgentReply("Something went wrong answering that; the details are in the session's log.", "reply", false, 0, Failed: true);
        }
        if (reply.Text is not { Length: > 0 } text)
            return;

        var (chat, thread, replyTo, kind) = (message.ChatId, message.ThreadId, message.MessageId, "reply");
        switch (reply.Deliver)
        {
            case "silent" when !reply.Failed:
                return;
            // A tainted answer goes back where the conversation is, never to another channel (§10.3: no new recipients).
            case "home_channel" when !reply.Tainted && !reply.Failed && adapter.HomeChat is { Length: > 0 } home:
                (chat, thread, replyTo, kind) = (home, null, null, "home");
                break;
            case "reply_in_thread":
                (thread, kind) = (message.ThreadId ?? message.MessageId, "thread");
                break;
        }
        _ledger.Queue(new DeliveryQueued(NewId(), message.Adapter, chat, text, kind, thread, replyTo, sessionId,
            reply.Sequence > 0 ? reply.Sequence : null, reply.Tainted));
        Signal();
    }

    private void Queue(InboundMessage message, string text, string kind)
    {
        _ledger.Queue(new DeliveryQueued(NewId(), message.Adapter, message.ChatId, text, kind, message.ThreadId, message.MessageId));
        Signal();
    }

    private void Track(Task task)
    {
        _inFlight.TryAdd(task, 0);
        _ = task.ContinueWith(done => _inFlight.TryRemove(done, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string NewId() => "dlv-" + Guid.NewGuid().ToString("N");

    // ---- approvals in a chat ----

    private static bool IsApprovalAnswer(string text)
    {
        var word = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return word is "/approve" or "/deny";
    }

    private void Answer(InboundMessage message, string sessionId, string text)
    {
        if (!_approvals.TryGetValue(sessionId, out var pending) || pending.Answer.Task.IsCompleted)
        {
            Queue(message, "Nothing here is waiting for approval.", "notice");
            return;
        }
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        ApprovalAnswer answer;
        if (words[0].Equals("/deny", StringComparison.OrdinalIgnoreCase))
            answer = ApprovalAnswer.Deny();
        else
        {
            var scope = words.Length < 2 ? ApprovalScope.Once : words[1].ToLowerInvariant() switch
            {
                "once" => ApprovalScope.Once,
                "session" => ApprovalScope.Session,
                "always" => ApprovalScope.Always,
                _ => (ApprovalScope?)null,
            };
            if (scope is not { } chosen || !pending.Prompt.Offered.Contains(chosen))
            {
                Queue(message, "Answer /approve" + string.Concat(pending.Prompt.Offered.Where(offered => offered != ApprovalScope.Once)
                    .Select(offered => ", /approve " + offered.ToString().ToLowerInvariant())) + " or /deny.", "notice");
                return;
            }
            answer = ApprovalAnswer.Approve(chosen);
        }
        // The confirmation is queued before the turn resumes, so the chat reads it before the answer the approval lets through.
        Queue(message, answer.Approved ? $"Approved ({answer.Scope.ToString().ToLowerInvariant()})." : "Denied.", "notice");
        if (!pending.Answer.TrySetResult(answer))
            Queue(message, "Too late: that approval had already timed out and was denied.", "notice");
    }

    private async ValueTask<ApprovalAnswer> AskAsync(string sessionId, string adapter, string chatId, ApprovalPrompt prompt,
        CancellationToken cancellationToken)
    {
        var wait = prompt.Deadline - _time.GetUtcNow();
        if (wait > _options.ApprovalTimeout)
            wait = _options.ApprovalTimeout;
        if (wait <= TimeSpan.Zero || _lifetime.IsCancellationRequested)
            return ApprovalAnswer.Deny();
        var pending = new PendingApproval(prompt);
        if (!_approvals.TryAdd(sessionId, pending))
            // A session asks one approval at a time; a second at once is denied rather than shown ambiguously.
            return ApprovalAnswer.Deny();
        try
        {
            _ledger.Queue(new DeliveryQueued(NewId(), adapter, chatId, Describe(prompt, _time.GetUtcNow() + wait), "notice"));
            Signal();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(wait);
            try
            {
                return await pending.Answer.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApprovalAnswer.Deny();
            }
        }
        finally
        {
            _approvals.TryRemove(KeyValuePair.Create(sessionId, pending));
        }
    }

    private static string Describe(ApprovalPrompt prompt, DateTimeOffset until)
    {
        var lines = new List<string>
        {
            $"Approval needed: {prompt.Analysis.Kind} {prompt.Analysis.Text}",
        };
        if (!prompt.Reasons.IsEmpty)
            lines.Add("Why: " + string.Join("; ", prompt.Reasons.Select(reason => reason.Detail)));
        lines.Add("Reply /approve" + string.Concat(prompt.Offered.Where(offered => offered != ApprovalScope.Once)
            .Select(offered => ", /approve " + offered.ToString().ToLowerInvariant())) +
            $" or /deny before {until.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC; no answer denies it.");
        return string.Join("\n", lines);
    }

    private sealed record PendingApproval(ApprovalPrompt Prompt)
    {
        public TaskCompletionSource<ApprovalAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ChatPrompter(GatewayHost host, string sessionId, string adapter, string chatId) : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) =>
            host.AskAsync(sessionId, adapter, chatId, prompt, cancellationToken);
    }

    // ---- the ledger ----

    private void Signal()
    {
        if (_ledgerSignal.CurrentCount == 0)
            _ledgerSignal.Release();
        foreach (var supervisor in _supervisors.Values)
            supervisor.Signal();
    }

    // Reads what other processes queued, gives up deliveries for adapters this gateway does not run, and keeps the
    // low-water mark, so the next start reads only the open end of the ledger.
    private async Task LedgerLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                _ledger.Catch();
                foreach (var pending in _ledger.Pending.Where(pending => !_supervisors.ContainsKey(pending.Delivery.Adapter)))
                {
                    _ledger.Resolve(pending.Delivery.DeliveryId, DeliveryStatus.Failed,
                        detail: $"no adapter named '{pending.Delivery.Adapter}' is enabled");
                    Notice?.Invoke($"Gave up a delivery to {pending.Delivery.Adapter}: that adapter is not enabled.");
                }
                foreach (var supervisor in _supervisors.Values)
                    supervisor.Signal();
                SaveLowWater();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Notice?.Invoke("The delivery ledger could not be read: " + exception.Message);
            }
            try
            {
                await _ledgerSignal.WaitAsync(_options.LedgerPoll, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SaveLowWater()
    {
        var lowWater = _ledger.LowWater;
        if (lowWater == Interlocked.Read(ref _storedLowWater))
            return;
        try
        {
            _pairing.SetState(LowWaterKey, lowWater.ToString(CultureInfo.InvariantCulture));
            Interlocked.Exchange(ref _storedLowWater, lowWater);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Only an optimisation: the next start reads more of the ledger.
        }
    }

    private static TimeSpan Backoff(TimeSpan first, TimeSpan max, int attempt)
    {
        var delay = first * Math.Pow(2, Math.Min(attempt - 1, 16));
        return delay > max ? max : delay;
    }

    /// <summary>One adapter: its connection loop and its delivery loop, on their own tasks.</summary>
    private sealed class Supervisor(GatewayHost host, GatewayAdapterRegistration registration) : IAdapterContext
    {
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Dictionary<string, (int Attempts, DateTimeOffset NextAt)> _retries = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private Task? _connection;
        private Task? _delivery;
        private volatile IGatewayAdapter? _current;
        private AdapterStatus _status = new(registration.Name, AdapterState.Starting, null, host._time.GetUtcNow());

        public AdapterStatus Status
        {
            get
            {
                lock (_gate)
                    return _status;
            }
        }

        public IGatewayAdapter? Current => _current;

        public void Start()
        {
            _connection = Task.Run(ConnectionLoopAsync, CancellationToken.None);
            _delivery = Task.Run(DeliveryLoopAsync, CancellationToken.None);
        }

        public void Signal()
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }

        /// <summary>A delivery waiting out a retry delay, so draining does not wait for it.</summary>
        public bool Waiting(string deliveryId)
        {
            lock (_gate)
                return _retries.ContainsKey(deliveryId);
        }

        public async Task StopAsync()
        {
            var tasks = new[] { _connection, _delivery }.Where(task => task is not null).Cast<Task>().ToArray();
            await Task.WhenAll(tasks).WaitAsync(host._options.StopTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_current is { } adapter)
            {
                _current = null;
                await DisposeQuietlyAsync(adapter).ConfigureAwait(false);
            }
            Set(AdapterState.Stopped, null);
        }

        public IReadOnlyList<string> KnownUsers()
        {
            var options = host._options.For(registration.Name);
            return [.. options.Owners.Concat(options.Guests).Concat(host._pairing.Users(registration.Name).Select(user => user.UserId))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        public string? GetCursor(string key) => host._pairing.GetState($"cursor:{registration.Name}:{key}");

        public void SetCursor(string key, string value) => host._pairing.SetState($"cursor:{registration.Name}:{key}", value);

        private void Set(AdapterState state, string? detail)
        {
            lock (_gate)
            {
                if (_status.State == state && _status.Detail == detail)
                    return;
                _status = new AdapterStatus(registration.Name, state, detail, host._time.GetUtcNow());
            }
            if (state is AdapterState.Connected or AdapterState.Retrying)
                host.Notice?.Invoke($"{registration.Name}: {state.ToString().ToLowerInvariant()}" + (detail is null ? "" : $" ({detail})"));
        }

        private async Task ConnectionLoopAsync()
        {
            var stopping = host._receiving.Token;
            var failures = 0;
            while (!stopping.IsCancellationRequested)
            {
                var created = new StrongBox<IGatewayAdapter?>();
                using var scope = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                var token = scope.Token;
                IGatewayAdapter adapter;
                Task<IGatewayAdapter>? connect = null;
                try
                {
                    Set(AdapterState.Connecting, null);
                    // Created and connected off this loop under one timeout: a constructor or connect that hangs (and ignores
                    // cancellation) is left behind, never waited for.
                    connect = Task.Run(async () =>
                    {
                        var fresh = registration.Create();
                        created.Value = fresh;
                        await fresh.ConnectAsync(this, token).ConfigureAwait(false);
                        return fresh;
                    }, CancellationToken.None);
                    adapter = await connect.WaitAsync(host._options.ConnectTimeout, stopping).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await scope.CancelAsync().ConfigureAwait(false);
                    Abandon(created, connect);
                    if (stopping.IsCancellationRequested)
                        return;
                    failures++;
                    Set(AdapterState.Retrying, exception is TimeoutException
                        ? $"connect timed out after {host._options.ConnectTimeout.TotalSeconds:0} s"
                        : "connect failed: " + exception.Message);
                    if (!await DelayAsync(Backoff(host._options.ReconnectDelay, host._options.MaxReconnectDelay, failures), stopping).ConfigureAwait(false))
                        return;
                    continue;
                }

                failures = 0;
                _current = adapter;
                Set(AdapterState.Connected, null);
                Signal();
                try
                {
                    await adapter.ReceiveAsync(Inbox, token).ConfigureAwait(false);
                    if (!stopping.IsCancellationRequested)
                        throw new InvalidOperationException("the connection closed");
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    // Stopping: the adapter is disposed by StopAsync once deliveries have drained.
                    return;
                }
                catch (Exception exception)
                {
                    _current = null;
                    await scope.CancelAsync().ConfigureAwait(false);
                    Abandon(created, null);
                    failures++;
                    Set(AdapterState.Retrying, "receive failed: " + exception.Message);
                    if (!await DelayAsync(Backoff(host._options.ReconnectDelay, host._options.MaxReconnectDelay, failures), stopping).ConfigureAwait(false))
                        return;
                }
            }
        }

        private async Task Inbox(InboundMessage message, CancellationToken cancellationToken)
        {
            if (message.Adapter != registration.Name)
                message = message with { Adapter = registration.Name };
            try
            {
                await host.ReceiveAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One message that cannot be handled must not end the connection.
                host.Notice?.Invoke($"{registration.Name}: a message from {message.SenderId} could not be handled: {exception.Message}");
            }
        }

        private async Task DeliveryLoopAsync()
        {
            var stopping = host._lifetime.Token;
            while (!stopping.IsCancellationRequested)
            {
                TimeSpan? soonest = null;
                if (_current is { } adapter)
                    soonest = await DeliverAsync(adapter, stopping).ConfigureAwait(false);
                try
                {
                    var wait = soonest is { } due && due < host._options.LedgerPoll ? due : host._options.LedgerPoll;
                    await _signal.WaitAsync(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Sends what is open for this adapter, in ledger order; a chat whose earlier message waits keeps its later ones back.</summary>
        private async Task<TimeSpan?> DeliverAsync(IGatewayAdapter adapter, CancellationToken stopping)
        {
            var held = new HashSet<string>(StringComparer.Ordinal);
            TimeSpan? soonest = null;
            foreach (var (_, delivery) in host._ledger.Pending.Where(pending => pending.Delivery.Adapter == registration.Name))
            {
                if (stopping.IsCancellationRequested || _current != adapter)
                    break;
                if (held.Contains(delivery.ChatId))
                    continue;
                var now = host._time.GetUtcNow();
                lock (_gate)
                {
                    if (_retries.TryGetValue(delivery.DeliveryId, out var retry) && retry.NextAt > now)
                    {
                        held.Add(delivery.ChatId);
                        var due = retry.NextAt - now;
                        soonest = soonest is null || due < soonest ? due : soonest;
                        continue;
                    }
                }
                try
                {
                    var sent = await adapter.SendAsync(new OutboundMessage(delivery.ChatId, delivery.Text, delivery.ReplyTo, delivery.ThreadId,
                        delivery.Tainted), stopping).WaitAsync(host._options.ConnectTimeout, stopping).ConfigureAwait(false);
                    host._ledger.Resolve(delivery.DeliveryId, DeliveryStatus.Delivered, sent.MessageId);
                    lock (_gate)
                        _retries.Remove(delivery.DeliveryId);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    int attempts;
                    lock (_gate)
                        attempts = (_retries.TryGetValue(delivery.DeliveryId, out var retry) ? retry.Attempts : 0) + 1;
                    if (exception is GatewaySendException { Permanent: true } || attempts >= host._options.MaxDeliveryAttempts)
                    {
                        host._ledger.Resolve(delivery.DeliveryId, DeliveryStatus.Failed, detail: exception.Message);
                        host.Notice?.Invoke($"{registration.Name}: gave up a delivery to {delivery.ChatId}: {exception.Message}");
                        lock (_gate)
                            _retries.Remove(delivery.DeliveryId);
                        continue;
                    }
                    var delay = exception is GatewaySendException { RetryAfter: { } after }
                        ? after
                        : Backoff(host._options.RetryDelay, host._options.MaxRetryDelay, attempts);
                    lock (_gate)
                        _retries[delivery.DeliveryId] = (attempts, host._time.GetUtcNow() + delay);
                    held.Add(delivery.ChatId);
                    soonest = soonest is null || delay < soonest ? delay : soonest;
                }
            }
            host.SaveLowWater();
            return soonest;
        }

        private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stopping)
        {
            try
            {
                await Task.Delay(delay, host._time, stopping).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // Disposal of a failed or abandoned adapter runs on its own, so one that hangs there holds nothing up either. A connect
        // still running (it timed out) disposes its adapter whenever it finally ends, or now if the adapter already exists.
        private void Abandon(StrongBox<IGatewayAdapter?> created, Task? connect)
        {
            if (created.Value is { } adapter)
                _ = DisposeQuietlyAsync(adapter);
            else
                connect?.ContinueWith(_ =>
                {
                    if (created.Value is { } late)
                        _ = DisposeQuietlyAsync(late);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task DisposeQuietlyAsync(IGatewayAdapter adapter)
        {
            try
            {
                await adapter.DisposeAsync().AsTask().WaitAsync(host._options.StopTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Abandoned: nothing waits for it.
            }
        }
    }
}
