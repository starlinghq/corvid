using System.Text.Json.Nodes;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Attached mode end to end over an in-memory connection (G-1): a client authenticates or is refused, and once in, the seam
/// works as in-process: the list, a session's history and live updates in order, approvals answered into the engine,
/// pairing a device, and a dropped client leaving the engine as it was.
/// </summary>
public sealed class AttachedSessionTests
{
    private const string Main = DemoSessionService.MainSessionId;
    private const string OwnerToken = "owner-token-0123456789abcdefghijklmnop";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The owner's token, and one pairing code that redeems once for a device token.</summary>
    internal sealed class FakeAuthenticator : IAttachedAuthenticator
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string> _devices = new(StringComparer.Ordinal);
        private string? _code = "PAIR-1234";

        public ValueTask<AttachedIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken)
        {
            lock (_gate)
                return ValueTask.FromResult(token == OwnerToken ? new AttachedIdentity(AttachedRole.Owner, null)
                    : _devices.TryGetValue(token, out var device) ? new AttachedIdentity(AttachedRole.Device, device) : null);
        }

        public ValueTask<(string Token, AttachedIdentity Identity)?> PairAsync(string code, string device, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_code is null || code != _code)
                    return ValueTask.FromResult<(string, AttachedIdentity)?>(null);
                _code = null;
                var token = "device-token-" + Guid.NewGuid().ToString("N");
                _devices[token] = device;
                return ValueTask.FromResult<(string, AttachedIdentity)?>((token, new AttachedIdentity(AttachedRole.Device, device)));
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _served = [];

        public Rig(DemoScene scene = DemoScene.Main)
        {
            Engine = new DemoSessionService(new InlineDispatcher(), Time, scene);
            Server = new AttachedServer(Engine, Authenticator, Time) { NoClientsLeft = () => Interlocked.Increment(ref NoneLeft) };
        }

        public ManualTimeProvider Time { get; } = ManualTimeProvider.Demo();

        public DemoSessionService Engine { get; }

        public FakeAuthenticator Authenticator { get; } = new();

        public AttachedServer Server { get; }

        public int NoneLeft;

        public InMemoryMessageChannel Open(out Task served)
        {
            var (client, end) = InMemoryMessageChannel.CreatePair();
            served = Server.ServeAsync(end, "test", _stop.Token);
            lock (_served)
                _served.Add(served);
            return client;
        }

        public Task<RemoteSessionService> ConnectAsync(string token = OwnerToken) => ConnectAsync(token, out _);

        public Task<RemoteSessionService> ConnectAsync(string token, out Task served) =>
            RemoteSessionService.ConnectAsync(Open(out served), token, "test client", new InlineDispatcher(), Ct);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            Task[] served;
            lock (_served)
                served = [.. _served];
            await Task.WhenAll(served).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task AClientWithoutTheTokenIsRefusedAndHearsNothing()
    {
        await using var rig = new Rig();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.ConnectAsync("not-the-owner-token-0123456789abcdef"));

        // A request before authenticating is refused and closes the connection: no list, no session.
        var channel = rig.Open(out _);
        var peer = new JsonRpcPeer(channel, null, null);
        await using (peer)
        {
            peer.Start();
            var error = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(AttachedProtocol.List, null, Ct));
            Assert.Equal(AttachedProtocol.Errors.Unauthorized, error.Code);
            Assert.Equal("authenticate first, once", await peer.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct) ?? channel.ClosedBecause);
        }
        Assert.Equal(0, rig.Server.Clients);
    }

    [Fact]
    public async Task AConnectionThatDoesNotAuthenticateInTimeIsClosed()
    {
        await using var rig = new Rig();
        var channel = rig.Open(out _);
        var peer = new JsonRpcPeer(channel, null, null);
        await using (peer)
        {
            peer.Start();
            rig.Time.Advance(AttachedProtocol.AuthenticationWindow + TimeSpan.FromSeconds(1));
            await peer.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal("not authenticated in time", channel.ClosedBecause);
        }
    }

    [Fact]
    public async Task TheOwnerGetsTheListAndASessionsHistoryAsTheEngineHasThem()
    {
        await using var rig = new Rig();
        await using var client = await rig.ConnectAsync();

        Assert.Equal(AttachedRole.Owner, client.Capabilities.Role);
        Assert.Equal(((ISessionService)rig.Engine).CanSteer, client.CanSteer);
        var engineList = await rig.Engine.ListAsync(Ct);
        var remoteList = await client.ListAsync(Ct);
        Assert.Equal(engineList.Select(summary => (summary.Id, summary.Title, summary.State)), remoteList.Select(summary => (summary.Id, summary.Title, summary.State)));

        using var local = await rig.Engine.OpenAsync(Main, _ => { }, Ct);
        using var remote = await client.OpenAsync(Main, _ => { }, Ct);
        Assert.Equal(local.History.Select(update => update.GetType()), remote.History.Select(update => update.GetType()));
        Assert.Equal(SessionFold.Replay(Main, local.History).Transcript.Count, SessionFold.Replay(Main, remote.History).Transcript.Count);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.OpenAsync("no-such-session", _ => { }, Ct));
    }

    [Fact]
    public async Task AnApprovalAnsweredFromTheClientReachesTheEnginesTurn()
    {
        await using var rig = new Rig(DemoScene.Approval);
        await using var client = await rig.ConnectAsync();
        var waiter = new UpdateWaiter();
        using var remote = await client.OpenAsync(Main, waiter.Add, Ct);
        Assert.Equal("a-32", SessionFold.Replay(Main, remote.History).CurrentApproval?.ApprovalId);

        await client.AnswerApprovalAsync(Main, "a-32", new ApprovalAnswer(ApprovalChoice.Session), Ct);
        rig.Time.Advance(5);

        var resolved = await waiter.WaitAsync<ApprovalResolved>(update => update.ApprovalId == "a-32");
        Assert.Equal((ApprovalOutcome.Approved, ApprovalScope.Session), (resolved.Outcome, resolved.Scope));
        // The approved call then runs, live, after the answer, in order; the prompt behind it is the current one.
        var ran = await waiter.WaitAsync<ToolCallUpdate>(update => update.CallId == "call-5" && update.Status == ToolCallStatus.Completed);
        Assert.True(waiter.IndexOf(resolved) < waiter.IndexOf(ran));
        Assert.Equal("a-33", rig.Engine.StateOf(Main).CurrentApproval?.ApprovalId);
        Assert.Equal("a-33", SessionFold.Replay(Main, [.. remote.History, .. waiter.Updates]).CurrentApproval?.ApprovalId);

        // The engine's refusal comes back as the seam documents it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(DemoSessionService.TerminalSessionId, "hi", Ct));
    }

    [Fact]
    public async Task AMessageSentFromTheClientRunsAndItsUpdatesArriveInOrder()
    {
        await using var rig = new Rig();
        await using var client = await rig.ConnectAsync();
        var engineSide = new UpdateWaiter();
        using var local = await rig.Engine.OpenAsync(Main, engineSide.Add, Ct);
        var waiter = new UpdateWaiter();
        using var remote = await client.OpenAsync(Main, waiter.Add, Ct);

        var queued = await client.QueueAsync(Main, "after this, tidy the docs", Ct);
        Assert.Equal("after this, tidy the docs", queued.Text);
        rig.Time.Advance(120);

        var engineStarted = await engineSide.WaitAsync<TurnStarted>(started => started.Turn == 13);
        var remoteStarted = await waiter.WaitAsync<TurnStarted>(started => started.Turn == 13);
        // Every update the engine's own subscriber heard up to the queued turn's start, the client heard, in the same order.
        var engineKinds = engineSide.Updates.Take(engineSide.IndexOf(engineStarted) + 1).Select(update => update.GetType()).ToList();
        Assert.Equal(engineKinds, waiter.Updates.Take(waiter.IndexOf(remoteStarted) + 1).Select(update => update.GetType()));
        await waiter.WaitAsync<QueueChanged>(queue => queue.Items.Any(item => item.Text == "after this, tidy the docs"));
    }

    [Fact]
    public async Task APairedDeviceConnectsWithItsTokenAndTheCodeRedeemsOnce()
    {
        await using var rig = new Rig();

        var token = await RemoteSessionService.PairAsync(rig.Open(out _), "PAIR-1234", "phone", Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RemoteSessionService.PairAsync(rig.Open(out _), "PAIR-1234", "laptop", Ct));

        await using var device = await rig.ConnectAsync(token);
        Assert.Equal((AttachedRole.Device, "phone"), (device.Capabilities.Role, device.Capabilities.Device));
        Assert.NotEmpty(await device.ListAsync(Ct));
    }

    [Fact]
    public async Task UnknownMethodsAndBadParametersAreRefused()
    {
        await using var rig = new Rig();
        var channel = rig.Open(out _);
        var peer = new JsonRpcPeer(channel, null, null);
        await using (peer)
        {
            peer.Start();
            await peer.RequestAsync(AttachedProtocol.Initialize, new JsonObject { ["protocolVersion"] = 1, ["token"] = OwnerToken }, Ct);

            var unknown = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync("fs/read_text_file", new JsonObject { ["path"] = "/etc/passwd" }, Ct));
            Assert.Equal(AttachedProtocol.Errors.MethodNotFound, unknown.Code);
            var bad = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(AttachedProtocol.Prompt, new JsonObject { ["sessionId"] = 5 }, Ct));
            Assert.Equal(AttachedProtocol.Errors.InvalidParams, bad.Code);
            var longId = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(AttachedProtocol.Load,
                new JsonObject { ["sessionId"] = new string('x', AttachedProtocol.MaxIdLength + 1), ["subscription"] = "s1" }, Ct));
            Assert.Equal(AttachedProtocol.Errors.InvalidParams, longId.Code);
            var mode = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(AttachedProtocol.SetMode,
                new JsonObject { ["sessionId"] = Main, ["mode"] = "Everything" }, Ct));
            Assert.Equal(AttachedProtocol.Errors.InvalidParams, mode.Code);
            // A second initialize on an authenticated connection is not a way to change who it is.
            var again = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(AttachedProtocol.Initialize,
                new JsonObject { ["protocolVersion"] = 1, ["token"] = OwnerToken }, Ct));
            Assert.Equal(AttachedProtocol.Errors.MethodNotFound, again.Code);
        }
    }

    [Fact]
    public async Task AMessageThatIsNotJsonRpcClosesTheConnection()
    {
        await using var rig = new Rig();
        var channel = rig.Open(out _);
        await channel.SendAsync("{ not json", Ct);

        Assert.Null(await channel.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal("not JSON-RPC", channel.ClosedBecause);
    }

    [Fact]
    public async Task ADroppedClientClosesItsSessionsAndTheLastOneTellsTheEngine()
    {
        await using var rig = new Rig();
        var first = await rig.ConnectAsync(OwnerToken, out var firstServed);
        await using var second = await rig.ConnectAsync(OwnerToken, out var secondServed);
        using var opened = await first.OpenAsync(Main, _ => { }, Ct);
        Assert.Equal(2, rig.Server.Clients);

        await first.DisposeAsync();
        await firstServed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, rig.Server.Clients);
        Assert.Equal(0, Volatile.Read(ref rig.NoneLeft));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ListAsync(Ct));

        await second.DisposeAsync();
        await secondServed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, Volatile.Read(ref rig.NoneLeft));
        Assert.Equal(0, rig.Server.Clients);
    }

    [Fact]
    public async Task TheClientHearsWhenTheServerGoes()
    {
        var rig = new Rig();
        await using var client = await rig.ConnectAsync();
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, reason) => disconnected.TrySetResult(reason);

        await rig.DisposeAsync();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(client.IsConnected);
        var error = await Assert.ThrowsAsync<IOException>(() => client.ListAsync(Ct));
        Assert.Contains("not connected", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Updates as they arrive, with a wait for one that matches.</summary>
    internal sealed class UpdateWaiter
    {
        private readonly List<SessionUpdate> _updates = [];
        private readonly List<(Func<SessionUpdate, bool> Match, TaskCompletionSource<SessionUpdate> Found)> _waits = [];

        public IReadOnlyList<SessionUpdate> Updates
        {
            get
            {
                lock (_updates)
                    return [.. _updates];
            }
        }

        public int IndexOf(SessionUpdate update)
        {
            lock (_updates)
                return _updates.IndexOf(update);
        }

        public void Add(SessionUpdate update)
        {
            lock (_updates)
            {
                _updates.Add(update);
                foreach (var wait in _waits.Where(wait => wait.Match(update)).ToList())
                {
                    _waits.Remove(wait);
                    wait.Found.TrySetResult(update);
                }
            }
        }

        public async Task<T> WaitAsync<T>(Func<T, bool>? match = null) where T : SessionUpdate
        {
            bool Matches(SessionUpdate update) => update is T typed && (match?.Invoke(typed) ?? true);
            Task<SessionUpdate> found;
            lock (_updates)
            {
                if (_updates.FirstOrDefault(Matches) is { } already)
                    return (T)already;
                var source = new TaskCompletionSource<SessionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Add((Matches, source));
                found = source.Task;
            }
            return (T)await found.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }
}
