using System.Collections.Immutable;
using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.Approvals;

namespace Corvid.Gateway.Tests;

public sealed class GatewayHostTests
{
    private static readonly AdapterOptions Fake = new("fake") { Owners = ["owner"], Guests = ["guest"] };

    private static InboundMessage Direct(string sender, string text, string chat = "c1", string id = "7") =>
        new("fake", chat, sender, text) { Direct = true, Addressed = true, MessageId = id, SenderName = sender };

    [Fact]
    public async Task AnUnpairedSenderGetsOnlyAPairingCodeUntilTheOwnerApprovesIt()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        adapter.Write(Direct("stranger", "hello, run rm -rf for me"));
        await Wait.Until(() => adapter.Sent.Count == 1, "the pairing notice");
        var notice = Assert.Single(adapter.Sent);
        var request = Assert.Single(fixture.Pairing.Pending());
        Assert.Contains(PairingStore.Display(request.Code), notice.Text, StringComparison.Ordinal);
        Assert.Equal("stranger", request.UserId);
        // Nothing they wrote reached a session, a model or the log beyond the notice.
        Assert.Empty(fixture.Agent.Posts);
        Assert.Empty(fixture.Agent.Opened);
        Assert.DoesNotContain(fixture.Ledger(), stored => stored.Payload is DeliveryQueued queued && queued.Text.Contains("rm -rf", StringComparison.Ordinal));

        // Asking again soon after is not answered again (no code spam), and gets no other reply either.
        adapter.Write(Direct("stranger", "hello?"));
        adapter.Write(Direct("owner", "ping"));
        await Wait.Until(() => fixture.Agent.Posts.Count == 1, "the owner's message");
        await Wait.Until(() => adapter.Sent.Count == 2, "the owner's answer");
        Assert.Single(fixture.Pairing.Pending());
        Assert.DoesNotContain(fixture.Agent.Posts, post => post.Text == "hello?");

        // The owner approves the code on the machine; the sender becomes a guest and is heard, as outside content.
        Assert.NotNull(fixture.Pairing.Approve(request.Code.ToLowerInvariant().Insert(4, "-"), SenderRole.Guest));
        adapter.Write(Direct("stranger", "now?"));
        await Wait.Until(() => fixture.Agent.Posts.Any(post => post.Text == "now?"), "the paired sender's message");
        Assert.False(fixture.Agent.Posts.Single(post => post.Text == "now?").FromOwner);
    }

    [Fact]
    public async Task OnlyTheOwnersTurnsGetTheWholeToolsetAndGuestsGetTheAdaptersGuestToolsOrNone()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        var other = new FakeAdapter("other");
        await fixture.StartAsync(GatewayFixture.Options(Fake with { GuestTools = ["list_notes"] },
            new AdapterOptions("other") { Owners = ["owner"], Guests = ["guest"] }), adapter, other);

        adapter.Write(Direct("owner", "owner asks"));
        adapter.Write(Direct("guest", "guest asks"));
        other.Write(Direct("guest", "guest elsewhere") with { Adapter = "other" });
        await Wait.Until(() => fixture.Agent.Posts.Count == 3, "three messages");

        Assert.Null(fixture.Agent.Scopes["owner asks"]);
        Assert.Equal("list_notes", Assert.Single(fixture.Agent.Scopes["guest asks"]!.Tools));
        // Without GuestTools a guest's turn gets no tools at all.
        Assert.Empty(fixture.Agent.Scopes["guest elsewhere"]!.Tools);
    }

    [Fact]
    public async Task APairedGuestsOwnAllowlistReplacesTheAdaptersForThemAlone()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake with { GuestTools = ["list_notes"] }), adapter);
        fixture.Pairing.Approve(fixture.Pairing.Request("fake", "ada", "ada", "ada").Request!.Code, SenderRole.Guest);
        fixture.Pairing.Approve(fixture.Pairing.Request("fake", "bo", "bo", "bo").Request!.Code, SenderRole.Guest);
        // G-14: set while the gateway runs (the app's Gateway page, corvid gateway pair tools); read at the next message.
        fixture.Pairing.SetGuestTools("fake", "ada", ["web_fetch"]);

        adapter.Write(Direct("ada", "ada asks"));
        adapter.Write(Direct("bo", "bo asks"));
        await Wait.Until(() => fixture.Agent.Posts.Count == 2, "two messages");

        Assert.Equal("web_fetch", Assert.Single(fixture.Agent.Scopes["ada asks"]!.Tools));
        Assert.Equal("list_notes", Assert.Single(fixture.Agent.Scopes["bo asks"]!.Tools));
    }

    [Fact]
    public async Task GroupMessagesThatDoNotAddressTheBotAreIgnoredAndStrangersInGroupsGetNoCode()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        adapter.Write(new InboundMessage("fake", "group", "owner", "chatting among ourselves") { Direct = false, Addressed = false });
        adapter.Write(new InboundMessage("fake", "group", "stranger", "anyone here?") { Direct = false, Addressed = false });
        adapter.Write(new InboundMessage("fake", "group", "owner", "bot, what time is it") { Direct = false, Addressed = true, MessageId = "9" });

        await Wait.Until(() => fixture.Agent.Posts.Count == 1, "the addressed message");
        Assert.Equal("bot, what time is it", fixture.Agent.Posts.Single().Text);
        Assert.Empty(fixture.Pairing.Pending());
    }

    [Fact]
    public async Task TheOwnersMessagesAreTheUsersAndEveryoneElsesAreOutsideContentInTheChatsSession()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        var options = GatewayFixture.Options(Fake with
        {
            Chats = ImmutableDictionary<string, ChatSettings>.Empty.Add("c1", new ChatSettings("You are terse.",
                new Dictionary<string, string> { ["generator"] = "big" })),
        });
        await fixture.StartAsync(options, adapter);

        adapter.Write(Direct("owner", "from the owner"));
        adapter.Write(Direct("guest", "from a guest"));
        await Wait.Until(() => fixture.Agent.Posts.Count == 2, "both messages");

        var posts = fixture.Agent.Posts.ToArray();
        Assert.Equal(("gateway:fake:c1", "from the owner", true), posts[0]);
        Assert.Equal(("gateway:fake:c1", "from a guest", false), posts[1]);
        // The chat's session is opened once, with its channel settings, and its approvals routed to the chat.
        var opened = Assert.Single(fixture.Agent.Opened);
        Assert.Equal("You are terse.", opened.Settings.Persona);
        Assert.Equal("big", opened.Settings.Slots["generator"]);
        Assert.True(fixture.Agent.Prompters.ContainsKey("gateway:fake:c1"));
        Assert.True(GatewaySessions.TryParse("gateway:fake:c1", out var name, out var chat) && name == "fake" && chat == "c1");
    }

    [Fact]
    public async Task AChatWhoseSessionFailedToOpenIsOpenedAgainOnItsNextMessage()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        fixture.Agent.OpenFailures.Enqueue(new InvalidOperationException("the chat's model is not ready"));
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        adapter.Write(Direct("owner", "first", id: "1"));
        await Wait.Until(() => fixture.Notices.Any(notice => notice.Contains("not ready", StringComparison.Ordinal)), "the failed open");
        adapter.Write(Direct("owner", "second", id: "2"));
        await Wait.Until(() => adapter.Sent.Count == 1, "the answer once the session opens");

        Assert.Equal(("echo: second", "2"), (adapter.Sent.Single().Text, adapter.Sent.Single().ReplyTo));
        Assert.Single(fixture.Agent.Opened);
    }

    [Fact]
    public async Task CommandsAreTheOwnersAlone()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        adapter.Write(Direct("guest", "/undo", id: "1"));
        adapter.Write(Direct("owner", "/status", id: "2"));
        await Wait.Until(() => adapter.Sent.Count == 2, "both answers");

        Assert.Equal([("gateway:fake:c1", "/status")], fixture.Agent.Commands.ToArray());
        Assert.Contains(adapter.Sent, sent => sent.ReplyTo == "1" && sent.Text.Contains("owner", StringComparison.Ordinal));
        Assert.Contains(adapter.Sent, sent => sent.ReplyTo == "2" && sent.Text == "ran /status");
        Assert.Empty(fixture.Agent.Posts);
    }

    [Fact]
    public async Task AnApprovalAskedInAChatIsAnsweredOnlyByTheOwner()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);
        adapter.Write(Direct("owner", "write the file"));
        await Wait.Until(() => fixture.Agent.Prompters.ContainsKey("gateway:fake:c1"), "the session to open");

        var prompt = new ApprovalPrompt("a-1", "gateway:fake:c1", new ActionAnalysis("tool", "write_file notes.md", [], null),
            [new ApprovalReason("side_effect", "writes a file")], [ApprovalScope.Once, ApprovalScope.Session], DateTimeOffset.UtcNow.AddMinutes(1));
        var answer = fixture.Agent.Prompters["gateway:fake:c1"].AskAsync(prompt, TestContext.Current.CancellationToken).AsTask();
        await Wait.Until(() => adapter.Sent.Any(sent => sent.Text.StartsWith("Approval needed", StringComparison.Ordinal)), "the prompt");
        // The prompt itself: the echo of "write the file" is sent concurrently and can land after it.
        Assert.Contains("write_file notes.md", adapter.Sent.Last(sent => sent.Text.StartsWith("Approval needed", StringComparison.Ordinal)).Text,
            StringComparison.Ordinal);

        adapter.Write(Direct("guest", "/approve", id: "20"));
        await Wait.Until(() => adapter.Sent.Any(sent => sent.ReplyTo == "20"), "the refusal");
        Assert.False(answer.IsCompleted);

        adapter.Write(Direct("owner", "/approve always", id: "21"));
        await Wait.Until(() => adapter.Sent.Any(sent => sent.ReplyTo == "21"), "the scope refusal");
        Assert.False(answer.IsCompleted);

        adapter.Write(Direct("owner", "/approve session", id: "22"));
        var approved = await answer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalAnswer.Approve(ApprovalScope.Session), approved);
        Assert.Empty(fixture.Agent.Commands);
    }

    [Fact]
    public async Task AnUnansweredApprovalIsDenied()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake) with { ApprovalTimeout = TimeSpan.FromMilliseconds(100) }, adapter);
        adapter.Write(Direct("owner", "hi"));
        await Wait.Until(() => fixture.Agent.Prompters.ContainsKey("gateway:fake:c1"), "the session to open");

        var prompt = new ApprovalPrompt("a-2", "gateway:fake:c1", new ActionAnalysis("tool", "x", [], null), [], [ApprovalScope.Once],
            DateTimeOffset.UtcNow.AddMinutes(5));
        var answer = await fixture.Agent.Prompters["gateway:fake:c1"].AskAsync(prompt, TestContext.Current.CancellationToken);

        Assert.False(answer.Approved);
    }

    [Theory]
    [InlineData("reply", false, "c1", null, "7")]
    [InlineData("reply_in_thread", false, "c1", "7", "7")]
    [InlineData("home_channel", false, "home", null, null)]
    // A tainted answer never goes to another channel: a new recipient (§10.3).
    [InlineData("home_channel", true, "c1", null, "7")]
    public async Task DeliverRoutesTheAnswer(string deliver, bool tainted, string chat, string? thread, string? replyTo)
    {
        await using var fixture = new GatewayFixture();
        fixture.Agent.Answer = (_, _, _) => new AgentReply("the answer", deliver, tainted, 5);
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake with { HomeChat = "home" }), adapter);

        adapter.Write(Direct("owner", "question"));
        await Wait.Until(() => adapter.Sent.Count == 1, "the answer");

        var sent = Assert.Single(adapter.Sent);
        Assert.Equal((chat, thread, replyTo, tainted), (sent.ChatId, sent.ThreadId, sent.ReplyTo, sent.Tainted));
        var queued = fixture.Ledger().Select(stored => stored.Payload).OfType<DeliveryQueued>().Single();
        Assert.Equal(("gateway:fake:c1", 5L), (queued.SourceSession, queued.SourceSequence!.Value));
    }

    [Fact]
    public async Task ASilentAnswerSendsNothing()
    {
        await using var fixture = new GatewayFixture();
        fixture.Agent.Answer = (_, _, _) => new AgentReply("kept to myself", "silent", false, 5);
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        adapter.Write(Direct("owner", "note this", id: "1"));
        adapter.Write(Direct("owner", "/status", id: "2"));
        await Wait.Until(() => adapter.Sent.Count == 1, "the command's answer");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal("2", Assert.Single(adapter.Sent).ReplyTo);
    }

    [Fact]
    public async Task OpenDeliveriesAreSentAgainAfterARestartExactlyOnceTheySucceed()
    {
        await using var fixture = new GatewayFixture();
        var failing = new FakeAdapter { AlwaysFail = new HttpRequestException("network down") };
        var options = GatewayFixture.Options(Fake) with { MaxDeliveryAttempts = 1000 };
        var first = await fixture.StartAsync(options, failing);

        failing.Write(Direct("owner", "remember the milk"));
        await Wait.Until(() => first.PendingDeliveries.Count == 1, "the answer in the ledger");
        await first.StopAsync();
        Assert.Empty(failing.Sent);
        var queued = fixture.Ledger().Select(stored => stored.Payload).OfType<DeliveryQueued>().Single();
        Assert.DoesNotContain(fixture.Ledger(), stored => stored.Payload is DeliveryResolved);

        // A new process: same event log and gateway store, the platform reachable again.
        var working = new FakeAdapter();
        var second = await fixture.StartAsync(options, working);
        await Wait.Until(() => second.PendingDeliveries.Count == 0, "the redelivery");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var sent = Assert.Single(working.Sent);
        Assert.Equal(("echo: remember the milk", "7"), (sent.Text, sent.ReplyTo));
        var resolved = fixture.Ledger().Select(stored => stored.Payload).OfType<DeliveryResolved>().Single();
        Assert.Equal((queued.DeliveryId, DeliveryStatus.Delivered, "m1"), (resolved.DeliveryId, resolved.Status, resolved.PlatformMessageId));
        await second.StopAsync();

        // The low-water mark moved past the resolved entry: a third start has nothing to read again or send.
        Assert.Equal(fixture.Log.GetHead(GatewaySessions.Ledger, TestContext.Current.CancellationToken).ToString(System.Globalization.CultureInfo.InvariantCulture),
            fixture.Pairing.GetState(GatewayHost.LowWaterKey));
        var third = new FakeAdapter();
        var host = await fixture.StartAsync(options, third);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(third.Sent);
        Assert.Empty(host.PendingDeliveries);
    }

    [Fact]
    public async Task ADeliveryQueuedByAnotherProcessIsSent()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        // What a CLI holding the scheduler does with a job's result: append to the shared log's ledger.
        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("job-1", "fake", "c9", "Your report is ready.", "job"));

        await Wait.Until(() => adapter.Sent.Count == 1, "the job's result");
        Assert.Equal(("c9", "Your report is ready."), (adapter.Sent.Single().ChatId, adapter.Sent.Single().Text));
    }

    [Fact]
    public async Task TransientFailuresAreRetriedInOrderAndPermanentOnesAreGivenUp()
    {
        await using var fixture = new GatewayFixture();
        var adapter = new FakeAdapter();
        adapter.SendFailures.Enqueue(new HttpRequestException("blip"));
        await fixture.StartAsync(GatewayFixture.Options(Fake), adapter);

        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("d-1", "fake", "c1", "first", "notice"));
        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("d-2", "fake", "c1", "second", "notice"));
        await Wait.Until(() => adapter.Sent.Count == 2, "both, after a retry");
        Assert.Equal(["first", "second"], adapter.Sent.Select(sent => sent.Text));

        adapter.SendFailures.Enqueue(new GatewaySendException("chat not found", permanent: true));
        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("d-3", "fake", "gone", "lost", "notice"));
        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("d-4", "nowhere", "c1", "no such adapter", "notice"));
        await Wait.Until(() => fixture.Ledger().Count(stored => stored.Payload is DeliveryResolved) == 4, "all resolved");

        var resolved = fixture.Ledger().Select(stored => stored.Payload).OfType<DeliveryResolved>()
            .ToDictionary(item => item.DeliveryId);
        Assert.Equal(DeliveryStatus.Failed, resolved["d-3"].Status);
        Assert.Equal(DeliveryStatus.Failed, resolved["d-4"].Status);
        Assert.Equal(2, adapter.Sent.Count);
    }

    [Fact]
    public async Task AHangingAdapterNeverStallsTheHostOrAnotherAdapter()
    {
        await using var fixture = new GatewayFixture();
        using var release = new ManualResetEventSlim();
        var hanging = new HangingAdapter("stuck", release);
        var working = new FakeAdapter();
        var options = GatewayFixture.Options(Fake, new AdapterOptions("stuck") { Owners = ["owner"] });
        var clock = Stopwatch.StartNew();
        try
        {
            var host = await fixture.StartAsync(options, hanging, working);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "starting must not wait for any adapter to connect");

            working.Write(Direct("owner", "still there?"));
            await Wait.Until(() => working.Sent.Count == 1, "the working adapter to answer");
            await Wait.Until(() => host.Adapters.Single(status => status.Name == "stuck").State == AdapterState.Retrying, "the timeout");
            Assert.Contains("timed out", host.Adapters.Single(status => status.Name == "stuck").Detail, StringComparison.Ordinal);
            Assert.Equal(AdapterState.Connected, host.Adapters.Single(status => status.Name == "fake").State);
            // A delivery for the hanging adapter waits; it holds nothing else up.
            DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("d-stuck", "stuck", "c1", "later", "notice"));
            working.Write(Direct("owner", "and now?"));
            await Wait.Until(() => working.Sent.Count == 2, "the working adapter again");

            clock.Restart();
            await host.StopAsync();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "stopping must not wait forever for the hanging adapter");
            Assert.Equal(AdapterState.Stopped, host.Adapters.Single(status => status.Name == "stuck").State);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AnAdapterThatHangsWhileBeingCreatedIsLeftBehind()
    {
        await using var fixture = new GatewayFixture();
        using var release = new ManualResetEventSlim();
        var working = new FakeAdapter();
        var registrations = new[]
        {
            new GatewayAdapterRegistration("stuck", () =>
            {
                release.Wait();
                return new FakeAdapter("stuck");
            }),
            new GatewayAdapterRegistration("fake", () => working),
        };
        try
        {
            var host = await fixture.StartAsync(GatewayFixture.Options(Fake, new AdapterOptions("stuck")), registrations);
            working.Write(Direct("owner", "hello"));
            await Wait.Until(() => working.Sent.Count == 1, "the working adapter");
            await Wait.Until(() => host.Adapters.Single(status => status.Name == "stuck").State == AdapterState.Retrying, "the timeout");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AReceiveFailureReconnectsWithAFreshAdapter()
    {
        await using var fixture = new GatewayFixture();
        var adapters = new Queue<IGatewayAdapter>([new BrokenAdapter(), new FakeAdapter()]);
        var created = 0;
        var host = await fixture.StartAsync(GatewayFixture.Options(Fake), [new GatewayAdapterRegistration("fake", () =>
        {
            created++;
            return adapters.Count > 0 ? adapters.Dequeue() : new FakeAdapter();
        })]);

        await Wait.Until(() => host.Adapters.Single().State == AdapterState.Connected && created >= 2, "the second adapter");
        Assert.True(created >= 2);
    }

    [Fact]
    public async Task OnlyOneGatewayRunsAgainstADataDirectory()
    {
        await using var fixture = new GatewayFixture();
        await fixture.StartAsync(GatewayFixture.Options(Fake), new FakeAdapter());

        var second = new GatewayHost([], fixture.Agent, fixture.Pairing, new DeliveryLedger(fixture.Log));
        await Assert.ThrowsAsync<GatewayLockHeldException>(() => second.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStartThatMeetsAStatusProbeTriesTheLockAgain()
    {
        // L4: `corvid gateway status` (or an app's Gateway page) holds the lock for an instant; a start that meets it waits
        // it out rather than reporting another gateway.
        await using var fixture = new GatewayFixture();
        var probe = new FileStream(fixture.Pairing.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var tries = new List<int>();
        var host = new GatewayHost([], fixture.Agent, fixture.Pairing, new DeliveryLedger(fixture.Log))
        {
            LockRetryDelay = (attempt, _) =>
            {
                tries.Add(attempt);
                // The probe lets go before the second try.
                probe.Dispose();
                return Task.CompletedTask;
            },
        };
        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal([1], tries);
        }
        finally
        {
            probe.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ALockHeldThroughEveryTryIsAnotherGateway()
    {
        await using var fixture = new GatewayFixture();
        using var other = new FileStream(fixture.Pairing.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var tries = 0;
        var host = new GatewayHost([], fixture.Agent, fixture.Pairing, new DeliveryLedger(fixture.Log))
        {
            LockRetryDelay = (_, _) =>
            {
                tries++;
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAsync<GatewayLockHeldException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GatewayHost.LockAttempts - 1, tries);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task ALockFileThatCannotBeOpenedIsNotAnotherGateway()
    {
        // L5: a lock the file system refuses (here a directory where the file goes) is an error, not a holder.
        await using var fixture = new GatewayFixture();
        Directory.CreateDirectory(fixture.Pairing.Path + ".lock");
        var host = new GatewayHost([], fixture.Agent, fixture.Pairing, new DeliveryLedger(fixture.Log));

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.True(refused is IOException or UnauthorizedAccessException, refused.GetType().Name);
        await host.DisposeAsync();
    }

    private sealed class BrokenAdapter : IGatewayAdapter
    {
        public string Name => "fake";

        public Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken) =>
            throw new IOException("connection reset");

        public Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken) => throw new IOException("reset");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
