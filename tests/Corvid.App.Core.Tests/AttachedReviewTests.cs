using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Attached mode after A6's review: requests run in the order they came with long ones beside them, either end gives a
/// request up, a request the other end does not answer fails in time, an unexpected failure says nothing of its insides, a
/// slow or flooding client is dropped, a join and a close never interleave, a history too large fails alone, two windows
/// answer one approval once, a dead client is found by its pings, and the owner token goes nowhere but its own user's socket.
/// </summary>
public sealed class AttachedReviewTests
{
    private const string OwnerToken = "owner-token-0123456789abcdefghijklmnop";
    private const string Main = DemoSessionService.MainSessionId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two connected peers: the server answers with <paramref name="handler"/>, the client calls.</summary>
    private static (JsonRpcPeer Client, JsonRpcPeer Server) Pair(JsonRpcPeer.RequestHandler handler, Func<string, bool>? runsLong = null,
        TimeProvider? clientTime = null, TimeSpan? timeout = null, Action<string, Exception>? unexpected = null, Action<string>? notified = null)
    {
        var (client, server) = InMemoryMessageChannel.CreatePair();
        var serving = new JsonRpcPeer(server, handler, (method, _) => notified?.Invoke(method)) { RunsLong = runsLong, OnUnexpected = unexpected };
        var calling = new JsonRpcPeer(client, null, null)
        {
            RunsLong = runsLong,
            Time = clientTime ?? TimeProvider.System,
            RequestTimeout = timeout ?? Timeout.InfiniteTimeSpan,
        };
        serving.Start();
        calling.Start();
        return (calling, serving);
    }

    // A6 review M1: each request ran on its own Task.Run, so one client's set_mode then prompt, or queue_move then
    // queue_remove, could reach the engine the other way round.
    [Fact]
    public async Task RequestsRunOneAtATimeInTheOrderTheyCame()
    {
        var events = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, server) = Pair(async (method, _, _) =>
        {
            events.Enqueue(method + " started");
            if (method == "set_mode")
            {
                firstStarted.TrySetResult();
                await release.Task;
            }
            events.Enqueue(method + " done");
            return null;
        }, notified: method => barrier.TrySetResult());
        await using (client)
        await using (server)
        {
            var first = client.RequestAsync("set_mode", null, Ct);
            var second = client.RequestAsync("prompt", null, Ct);
            // Read after both requests: by now the server has both, and the second has not started.
            client.Notify("barrier", null);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.DoesNotContain("prompt started", events);

            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(["set_mode started", "set_mode done", "prompt started", "prompt done"], events);
        }
    }

    // A6 review M1: a long request (a new session waiting on a worktree's card) is started in its turn but never holds up
    // the next, which may be the very answer it waits for.
    [Fact]
    public async Task ALongRequestDoesNotHoldUpTheNext()
    {
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, server) = Pair((method, _, _) => method == AttachedProtocol.New ? release.Task : Task.FromResult<JsonNode?>("answered"),
            AttachedProtocol.RunsLong);
        await using (client)
        await using (server)
        {
            var created = client.RequestAsync(AttachedProtocol.New, null, Ct);
            Assert.Equal("answered", (string?)await client.RequestAsync(AttachedProtocol.AnswerApproval, null, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.False(created.IsCompleted);
            release.SetResult("s-1");
            Assert.Equal("s-1", (string?)await created.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
    }

    // A6 review L4: $/cancelRequest. A caller that gives up tells the other end, which cancels the handler's token.
    [Fact]
    public async Task ARequestTheCallerGivesUpIsCancelledOnTheOtherEnd()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, server) = Pair(async (_, _, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return null;
        });
        await using (client)
        await using (server)
        {
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var request = client.RequestAsync(AttachedProtocol.Compact, null, giveUp.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await giveUp.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            // The connection goes on.
            Assert.False(client.IsClosed);
        }
    }

    // A6 review M2: a request the engine never answers fails after RequestTimeout (and is cancelled there); one that may
    // take as long as a person does is not timed.
    [Fact]
    public async Task ARequestTheOtherEndNeverAnswersFailsInTime()
    {
        var time = ManualTimeProvider.Demo();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<string>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, server) = Pair(async (method, _, token) =>
        {
            started.Enqueue(method);
            if (started.Count == 2)
                bothStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException) when (method == AttachedProtocol.List)
            {
                cancelled.TrySetResult();
                throw;
            }
            return null;
        }, AttachedProtocol.RunsLong, time, TimeSpan.FromSeconds(30));
        await using (client)
        await using (server)
        {
            var recap = client.RequestAsync(AttachedProtocol.Recap, null, Ct);
            var list = client.RequestAsync(AttachedProtocol.List, null, Ct);
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            time.Advance(TimeSpan.FromSeconds(31));
            var error = await Assert.ThrowsAsync<IOException>(() => list.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Contains("No answer", error.Message, StringComparison.Ordinal);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.False(recap.IsCompleted);
        }
    }

    // A6 review L3: an exception nothing expected went to the client with its message (paths, internals); now the client
    // hears that the request failed and the server's log hears the rest.
    [Fact]
    public async Task AnUnexpectedFailureTellsTheClientNothingOfItsInsides()
    {
        var logged = new ConcurrentQueue<string>();
        var (client, server) = Pair((_, _, _) => throw new NullReferenceException("at /home/someone/secret/path"),
            unexpected: (method, exception) => logged.Enqueue(method + ": " + exception.Message));
        await using (client)
        await using (server)
        {
            var error = await Assert.ThrowsAsync<JsonRpcException>(() => client.RequestAsync(AttachedProtocol.List, null, Ct));
            Assert.Equal(AttachedProtocol.Errors.Internal, error.Code);
            Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
            Assert.Equal([$"{AttachedProtocol.List}: at /home/someone/secret/path"], logged);
        }
    }

    // A6 review (tests asked for): MaxConcurrentRequests. Requests queued and running count together; past the limit the
    // next is refused as busy while the others wait their turn.
    [Fact]
    public async Task PastMaxConcurrentRequestsTheNextIsRefusedAsBusy()
    {
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, server) = Pair((_, _, _) => release.Task);
        await using (client)
        await using (server)
        {
            var held = Enumerable.Range(0, AttachedProtocol.MaxConcurrentRequests).Select(_ => client.RequestAsync(AttachedProtocol.List, null, Ct)).ToList();
            var error = await Assert.ThrowsAsync<JsonRpcException>(() => client.RequestAsync(AttachedProtocol.List, null, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Equal(AttachedProtocol.Errors.Busy, error.Code);

            release.SetResult(null);
            await Task.WhenAll(held).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    /// <summary>A client that never reads: every send waits until the connection is closed.</summary>
    private sealed class StuckChannel : IMessageChannel
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask SendAsync(string message, CancellationToken cancellationToken) =>
            await _closed.Task.WaitAsync(cancellationToken);

        public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return null;
        }

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
        {
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => CloseAsync("disposed", CancellationToken.None);
    }

    // A6 review (tests asked for): MaxQueuedBytes. The engine's updates for a client that stopped reading are not held
    // without bound: past the limit the connection is dropped.
    [Fact]
    public async Task AClientThatDoesNotReadIsDroppedPastMaxQueuedBytes()
    {
        var peer = new JsonRpcPeer(new StuckChannel(), null, null) { MaxQueuedBytes = 64 * 1024 };
        peer.Start();
        var update = new JsonObject { ["text"] = new string('x', 8 * 1024) };
        for (var i = 0; i < 16 && !peer.IsClosed; i++)
            peer.Notify(AttachedProtocol.SessionUpdate, (JsonObject)update.DeepClone());

        Assert.Equal("too much unread", await peer.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    private sealed class Authenticator : IAttachedAuthenticator
    {
        public ValueTask<AttachedIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken) =>
            ValueTask.FromResult(token == OwnerToken ? new AttachedIdentity(AttachedRole.Owner, null) : null);

        public ValueTask<(string Token, AttachedIdentity Identity)?> PairAsync(string code, string device, CancellationToken cancellationToken) =>
            ValueTask.FromResult<(string, AttachedIdentity)?>(null);
    }

    // A6 review L3: connections that have not authenticated are capped; one that authenticates makes room.
    [Fact]
    public async Task ConnectionsWaitingToAuthenticateAreCapped()
    {
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        var server = new AttachedServer(engine, new Authenticator(), ManualTimeProvider.Demo()) { MaxUnauthenticated = 2 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        InMemoryMessageChannel Open(out Task served)
        {
            var (client, end) = InMemoryMessageChannel.CreatePair();
            served = server.ServeAsync(end, "test", stop.Token);
            return client;
        }

        var first = Open(out _);
        Open(out _);
        var third = Open(out var thirdServed);
        await thirdServed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("too many connections waiting to authenticate", third.ClosedBecause);

        await using (var owner = await RemoteSessionService.ConnectAsync(first, OwnerToken, "test", new InlineDispatcher(), Ct))
        {
            await using var next = await RemoteSessionService.ConnectAsync(Open(out _), OwnerToken, "test", new InlineDispatcher(), Ct);
            Assert.Equal(2, server.Clients);
        }
        await stop.CancelAsync();
    }

    /// <summary>The demo's session service, with the SessionChanged handlers subscribed to it kept count of.</summary>
    public class GatedSessions : DispatchProxy
    {
        public ISessionService Inner { get; set; } = null!;

        /// <summary>The SessionChanged handlers subscribed now (a removal of one not subscribed changes nothing, as for an event).</summary>
        public HashSet<object> Subscribed { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "add_SessionChanged")
            {
                lock (Subscribed)
                    Subscribed.Add(args![0]!);
            }
            else if (targetMethod.Name == "remove_SessionChanged")
            {
                lock (Subscribed)
                    Subscribed.Remove(args![0]!);
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                throw inner;
            }
        }
    }

    // A6 review L1: Joined ran outside the connection's gate, so a close while a client joined could run Left first: the
    // join's handlers then stayed subscribed for good and NoClientsLeft fired while the join went on.
    [Fact]
    public async Task AConnectionClosedWhileItJoinsLeavesNothingBehind()
    {
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        var proxy = DispatchProxy.Create<ISessionService, GatedSessions>();
        var gated = (GatedSessions)(object)proxy;
        gated.Inner = engine;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var closed = new ManualResetEventSlim();
        var noneLeft = 0;
        // Once the connection has authenticated and let go of its gate, the server stops, and the authentication goes on
        // only once the connection's close has finished. Before the fix the join came after this point, so the close's
        // Left ran first; now the join is done under the gate before it.
        var server = new AttachedServer(proxy, new Authenticator(), ManualTimeProvider.Demo())
        {
            NoClientsLeft = () => Interlocked.Increment(ref noneLeft),
            Authenticated = () =>
            {
                stop.Cancel();
                Assert.True(closed.Wait(TimeSpan.FromSeconds(10)));
            },
            ClosedConnection = closed.Set,
        };
        var (client, end) = InMemoryMessageChannel.CreatePair();
        var served = server.ServeAsync(end, "test", stop.Token);
        var peer = new JsonRpcPeer(client, null, null);
        await using (peer)
        {
            peer.Start();
            _ = peer.RequestAsync(AttachedProtocol.Initialize, new JsonObject { ["protocolVersion"] = AttachedProtocol.Version, ["token"] = OwnerToken },
                CancellationToken.None);
            await served.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        lock (gated.Subscribed)
            Assert.Empty(gated.Subscribed);
        Assert.Equal(0, server.Clients);
        Assert.Equal(1, Volatile.Read(ref noneLeft));
    }

    // A6 review L2: a history past the client's MaxResponseBytes closed the whole connection (the client refuses to read
    // it); now that one request fails and the connection goes on.
    [Fact]
    public async Task AHistoryTooLargeToSendFailsAloneAndTheConnectionGoesOn()
    {
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        var server = new AttachedServer(engine, new Authenticator(), ManualTimeProvider.Demo()) { MaxResponseBytes = 8 * 1024 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (client, end) = InMemoryMessageChannel.CreatePair();
        _ = server.ServeAsync(end, "test", stop.Token);
        await using var remote = await RemoteSessionService.ConnectAsync(client, OwnerToken, "test", new InlineDispatcher(), Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => remote.OpenAsync(Main, _ => { }, Ct));
        Assert.Contains("too large", error.Message, StringComparison.Ordinal);
        Assert.True(remote.IsConnected);
        Assert.NotEmpty(await remote.ListAsync(Ct));
        await stop.CancelAsync();
    }

    // A6 review (tests asked for): two windows answer one approval. The first answer is the one the engine takes; the second,
    // to a prompt that has closed, changes nothing, and both windows see the one resolution.
    [Fact]
    public async Task TwoClientsAnsweringOneApprovalResolveItOnce()
    {
        var time = ManualTimeProvider.Demo();
        var engine = new DemoSessionService(new InlineDispatcher(), time, DemoScene.Approval);
        var server = new AttachedServer(engine, new Authenticator(), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        async Task<RemoteSessionService> Connect()
        {
            var (client, end) = InMemoryMessageChannel.CreatePair();
            _ = server.ServeAsync(end, "test", stop.Token);
            return await RemoteSessionService.ConnectAsync(client, OwnerToken, "test", new InlineDispatcher(), Ct);
        }

        await using var laptop = await Connect();
        await using var phone = await Connect();
        var onLaptop = new AttachedSessionTests.UpdateWaiter();
        var onPhone = new AttachedSessionTests.UpdateWaiter();
        using var laptopView = await laptop.OpenAsync(Main, onLaptop.Add, Ct);
        using var phoneView = await phone.OpenAsync(Main, onPhone.Add, Ct);

        await laptop.AnswerApprovalAsync(Main, "a-32", new ApprovalAnswer(ApprovalChoice.Session), Ct);
        await phone.AnswerApprovalAsync(Main, "a-32", new ApprovalAnswer(ApprovalChoice.Deny), Ct);
        time.Advance(TimeSpan.FromSeconds(5));

        foreach (var waiter in new[] { onLaptop, onPhone })
        {
            var resolved = await waiter.WaitAsync<ApprovalResolved>(update => update.ApprovalId == "a-32");
            Assert.Equal(ApprovalOutcome.Approved, resolved.Outcome);
            await waiter.WaitAsync<ToolCallUpdate>(update => update.CallId == "call-5" && update.Status == ToolCallStatus.Completed);
            Assert.Single(waiter.Updates.OfType<ApprovalResolved>(), update => update.ApprovalId == "a-32");
        }
        await stop.CancelAsync();
    }

    // A6 review M2: a client that stops answering pings is dropped, so the engine no longer counts it as one that answers
    // (AnswererPresent) and NoClientsLeft puts aside the questions its turns wait on. The intervals here are short; the
    // protocol's own are AttachedProtocol.KeepAliveInterval and KeepAliveTimeout, set on both ends.
    [Fact]
    public async Task AClientThatStopsAnsweringPingsIsDropped()
    {
        Assert.True(AttachedProtocol.KeepAliveTimeout > AttachedProtocol.KeepAliveInterval);
        var directory = Directory.CreateTempSubdirectory("corvid-keepalive-").FullName;
        try
        {
            var path = Path.Combine(directory, "s.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var dialing = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await dialing.ConnectAsync(new UnixDomainSocketEndPoint(path), Ct);
            using var accepted = await listener.AcceptAsync(Ct);

            var none = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
            var server = new AttachedServer(engine, new Authenticator()) { NoClientsLeft = () => none.TrySetResult() };
            // The stream owns its socket, as Kestrel's does: the keep-alive's abort then ends the pending read.
            using var serverSocket = WebSocket.CreateFromStream(new NetworkStream(accepted, ownsSocket: true), new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromMilliseconds(100),
                KeepAliveTimeout = TimeSpan.FromMilliseconds(200),
            });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var served = server.ServeAsync(new WebSocketMessageChannel(serverSocket, AttachedProtocol.MaxRequestBytes), "test", stop.Token);

            // The client authenticates, reads the answer, and then never reads again: it answers no ping.
            using var clientSocket = WebSocket.CreateFromStream(new NetworkStream(dialing), new WebSocketCreationOptions { KeepAliveInterval = TimeSpan.Zero });
            var initialize = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = AttachedProtocol.Initialize,
                ["params"] = new JsonObject { ["protocolVersion"] = AttachedProtocol.Version, ["token"] = OwnerToken },
            };
            await clientSocket.SendAsync(Encoding.UTF8.GetBytes(initialize.ToJsonString()), WebSocketMessageType.Text, true, Ct);
            var buffer = new byte[64 * 1024];
            WebSocketReceiveResult answer;
            do
                answer = await clientSocket.ReceiveAsync(buffer, Ct);
            while (!answer.EndOfMessage);
            Assert.Equal(1, server.Clients);

            await none.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.Equal(0, server.Clients);
            await served.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A6 review M3: the owner token went to any loopback ws:// address, where any local user could have taken the port.
    // Now it goes only over the Unix socket; a server that only listens records what it is sent, and it is sent nothing.
    [Fact]
    public async Task TheOwnerTokenIsNeverSentToALoopbackAddress()
    {
        var directory = Directory.CreateTempSubdirectory("corvid-squat-").FullName;
        try
        {
            var tokenFile = Path.Combine(directory, "owner.token");
            PrivateFiles.Write(tokenFile, OwnerToken + "\n");
            await using var squatter = FakeWebSocketServer.Loopback();
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            var attaching = AttachedStartup.ConnectAsync($"ws://127.0.0.1:{squatter.Port}", null, "test", new LateDispatcher(), new NoTokens(),
                tokenFile, giveUp.Token);
            // Before the fix the window connected and sent its initialize; after it, it never connects.
            await Task.WhenAny(attaching, squatter.FirstMessage.Task).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await giveUp.CancelAsync();
            var attached = await attaching.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.False(squatter.Saw(OwnerToken));
            Assert.Equal(0, squatter.Connections);
            Assert.Null(attached.Connection);
            Assert.Contains("only over the Unix socket", attached.Report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A6 review M3: a Unix socket whose server is not this user's (SO_PEERCRED) is refused before a byte is sent. A real
    // other user's socket cannot be made here, so the test asks for another user than the one serving it.
    [Fact]
    public async Task AUnixSocketServedByAnotherUserIsSentNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "SO_PEERCRED is Linux's.");
        var directory = Directory.CreateTempSubdirectory("corvid-peer-").FullName;
        try
        {
            var path = Path.Combine(directory, "attach.sock");
            await using var squatter = FakeWebSocketServer.Unix(path);
            Assert.True(AttachedAddress.TryParse("unix:" + path, out var address, out _));
            var me = UnixPeers.CurrentUser!.Value;

            var refused = await Assert.ThrowsAsync<PeerRefusedException>(() => AttachedClient.OpenAsync(address, me + 1, Ct));
            Assert.Contains($"user {me}", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, squatter.BytesReceived);

            // This user's own server is spoken to.
            await using var channel = await AttachedClient.OpenAsync(address, Ct);
            Assert.True(squatter.BytesReceived > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A6 review M3 (REVIEW): a host that accepts the connection and then never answers initialize opened no window at all;
    // now the attach gives up when its time is over (the desktop's is AttachedStartup.ConnectTimeout) and the window opens
    // saying so. The owner token goes over this user's own socket, as it should.
    [Fact]
    public async Task AHostThatNeverAnswersOpensTheWindowRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The owner token is sent on its own only where the socket's user can be told.");
        var directory = Directory.CreateTempSubdirectory("corvid-silent-").FullName;
        try
        {
            var tokenFile = Path.Combine(directory, "owner.token");
            PrivateFiles.Write(tokenFile, OwnerToken + "\n");
            var path = Path.Combine(directory, "attach.sock");
            await using var silent = FakeWebSocketServer.Unix(path);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            var attaching = AttachedStartup.ConnectAsync("unix:" + path, null, "test", new LateDispatcher(), new NoTokens(), tokenFile, timeout.Token);
            var initialize = await silent.FirstMessage.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Contains(AttachedProtocol.Initialize, initialize, StringComparison.Ordinal);
            Assert.False(attaching.IsCompleted);
            await timeout.CancelAsync();

            var attached = await attaching.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Null(attached.Connection);
            Assert.Contains("did not answer in time", attached.Report, StringComparison.Ordinal);
            Assert.IsType<Composition.UnavailableAppServices>(attached.Services);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoTokens : IDeviceTokenStore
    {
        public string? Get(string address) => null;

        public void Set(string address, string token) => throw new InvalidOperationException("nothing was paired");
    }
}
