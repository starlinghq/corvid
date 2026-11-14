using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Flocks;

namespace Corvid.Host.Tests;

/// <summary>C-042: flocks in the host: the relay that hands members their messages, flock_send, the loop guard and /flock.</summary>
public sealed class FlockRelayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // The log's events for a session once the condition holds, or a failure after a while.
    private static async Task<IReadOnlyList<SessionEvent>> WaitForAsync(CorvidRuntime runtime, string sessionId, Func<IReadOnlyList<SessionEvent>, bool> done)
    {
        var until = DateTimeOffset.UtcNow + Patience;
        while (true)
        {
            var events = runtime.Log.Read(sessionId, 0, long.MaxValue, 1_000, Ct).Select(stored => stored.Payload).ToList();
            if (done(events))
                return events;
            if (DateTimeOffset.UtcNow > until)
                Assert.Fail($"{sessionId} never got there: {string.Join(", ", events.Select(item => item.GetType().Name))}");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_member_s_flock_send_starts_a_turn_in_the_other_member_quoted_as_a_peer_s()
    {
        var generator = new FakeGenerator(
            Reply.Call(FlockSendTool.ToolName, """{"text":"The token endpoint moved to /v2/token.","to":"web"}"""),
            Reply.Say("Told web."),
            Reply.Say("Updated the client."));
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var api = runtime.OpenSession();
        var web = runtime.OpenSession();
        var flock = runtime.Flocks.Create("team");
        runtime.JoinFlock(flock.Id, api.Id, "api");
        runtime.JoinFlock(flock.Id, web.Id, "web");

        var sent = await api.SendAsync("tell web the endpoint moved", Ct);

        Assert.Equal("Told web.", sent.Result?.Text);
        var events = await WaitForAsync(runtime, web.Id, events => events.OfType<AssistantMessage>().Any());
        var message = Assert.Single(events.OfType<UserMessage>());
        Assert.Equal(("The token endpoint moved to /v2/token.", "api", "team"), (message.Text, message.From, message.Flock));
        Assert.Equal("Updated the client.", events.OfType<AssistantMessage>().Last().Text);
        Assert.False(web.Tainted);
        flock = runtime.Flocks.Get(flock.Id)!;
        Assert.Empty(flock.Pending(web.Id));
        Assert.True(flock.WasDelivered(flock.Messages[0].Sequence, web.Id));
        // The sender's turn was offered flock_send without the shortlist ranking it.
        Assert.Contains(FlockSendTool.ToolName, generator.OfferedTools[0]);
    }

    [Fact]
    public async Task A_session_outside_a_flock_is_never_offered_flock_send()
    {
        var generator = new FakeGenerator(Reply.Say("Hello."));
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var alone = runtime.OpenSession();

        await alone.SendAsync("send a note to the other session", Ct);

        Assert.DoesNotContain(FlockSendTool.ToolName, generator.OfferedTools[0]);
    }

    [Fact]
    public async Task A_surface_s_inbox_takes_the_message_and_a_tainted_member_s_arrives_as_outside_content()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var api = runtime.OpenSession();
        var web = runtime.OpenSession();
        var flock = runtime.Flocks.Create("team");
        runtime.JoinFlock(flock.Id, api.Id, "api");
        runtime.JoinFlock(flock.Id, web.Id, "web");
        var taken = new TaskCompletionSource<FlockDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inbox = runtime.AttachFlockInbox(web.Id, delivery => taken.TrySetResult(delivery));

        runtime.Flocks.Say(flock.Id, api.Id, "read what that page said", [], tainted: true);
        var delivery = await taken.Task.WaitAsync(Patience, Ct);

        Assert.Equal(EnvelopeOrigin.External, delivery.Origin);
        Assert.Equal(new PeerSender("api", "team"), delivery.Peer);
        Assert.Equal("flock team · api: read what that page said", delivery.Notice);
        // The surface runs it; the relay only recorded the hand-over.
        await WaitForAsync(runtime, flock.Id, events => events.OfType<FlockDelivered>().Any());
        Assert.Equal(0, runtime.Log.GetHead(web.Id, Ct) - (runtime.WorkspaceOf(web.Id).IsDefault ? 0 : 1));
    }

    [Fact]
    public async Task The_loop_guard_pauses_the_flock_and_the_user_s_message_lets_it_go_on()
    {
        var generator = new FakeGenerator(Reply.Say("ok 1"), Reply.Say("ok 2"), Reply.Say("ok 3"));
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        runtime.MaxPeerTurns = 1;
        var notices = new List<string>();
        runtime.Notice += line => { lock (notices) notices.Add(line); };
        var api = runtime.OpenSession();
        var web = runtime.OpenSession();
        var flock = runtime.Flocks.Create("loop");
        runtime.JoinFlock(flock.Id, api.Id, "api");
        runtime.JoinFlock(flock.Id, web.Id, "web");

        runtime.Flocks.Say(flock.Id, api.Id, "first", [], false);
        await WaitForAsync(runtime, web.Id, events => events.OfType<AssistantMessage>().Count() == 1);
        runtime.Flocks.Say(flock.Id, api.Id, "second", [], false);
        await WaitForAsync(runtime, flock.Id, events => events.OfType<FlockPaused>().Any());

        flock = runtime.Flocks.Get(flock.Id)!;
        Assert.True(flock.Paused);
        Assert.Single(flock.Pending(web.Id));
        lock (notices)
            Assert.Contains(notices, line => line.Contains("The flock loop is paused", StringComparison.Ordinal));

        runtime.Flocks.Say(flock.Id, FlockSessions.User, "go on", [web.Id], false);
        var events = await WaitForAsync(runtime, web.Id, events => events.OfType<AssistantMessage>().Count() == 3);
        Assert.Equal(["first", "second", "(To everyone in the flock loop) go on"], events.OfType<UserMessage>().Select(message => message.Text));
        Assert.Null(events.OfType<UserMessage>().Last().From);
    }

    [Fact]
    public async Task The_flock_command_starts_joins_says_and_shows()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var commands = CoreCommands.Register(new SlashCommandRegistry());
        var api = runtime.OpenSession();
        var web = runtime.OpenSession();
        ValueTask<CommandOutput> Run(CorvidSession session, string line) =>
            commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct);

        var started = await Run(api, "/flock new auth fix as api");
        var joined = await Run(web, "/flock join auth-fix as web");
        var said = await Run(api, "/flock say @web check the schema");
        var shown = await Run(api, "/flock");
        var again = await Run(web, "/flock new other");

        Assert.Equal(OutputTone.Success, started.Tone);
        Assert.Contains("this session is web in the flock auth-fix", string.Join(" ", joined.Lines), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Sent to web.", Assert.Single(said.Lines));
        Assert.Equal("Flock auth-fix", shown.Title);
        Assert.Contains(shown.Lines, line => line.Contains("you → web: check the schema", StringComparison.Ordinal));
        Assert.Equal(OutputTone.Error, again.Tone);
        Assert.Equal(runtime.Flocks.FlockOf(api.Id)!.Id, runtime.Flocks.FlockOf(web.Id)!.Id);
    }
}
