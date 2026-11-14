using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Events;
using Corvid.Kernel.Flocks;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>C-042: flocks in the event log, and how a turn takes a peer session's message.</summary>
public sealed class FlockTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("corvid-flocks-").FullName;
    private readonly FlockStore _store;

    public FlockTests() => _store = new FlockStore(new SqliteEventLog(Path.Combine(_root, "events.db")));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_message_is_pending_for_every_other_member_until_it_is_delivered()
    {
        var flock = _store.Create("auth refactor");
        _store.Join(flock.Id, "cli-a", "api");
        _store.Join(flock.Id, "cli-b", "web");
        _store.Join(flock.Id, "cli-c", "docs");

        var sequence = _store.Say(flock.Id, "cli-a", "The token endpoint moved to /v2/token.", [], tainted: false);
        flock = _store.Get(flock.Id)!;

        Assert.Equal("auth-refactor", flock.Name);
        Assert.Empty(flock.Pending("cli-a"));
        Assert.Equal(sequence, Assert.Single(flock.Pending("cli-b")).Sequence);
        Assert.Single(flock.Pending("cli-c"));

        flock = _store.Delivered(flock.Id, sequence, "cli-b", started: true);
        flock = _store.Delivered(flock.Id, sequence, "cli-b", started: true);

        Assert.Empty(flock.Pending("cli-b"));
        Assert.Single(flock.Pending("cli-c"));
        Assert.Single(flock.Messages);
        Assert.Equal(1, flock.PeerTurns);
    }

    [Fact]
    public void A_message_to_one_member_is_pending_for_it_alone_and_a_later_member_gets_only_what_came_after()
    {
        var flock = _store.Create("pair");
        _store.Join(flock.Id, "cli-a", "api");
        _store.Join(flock.Id, "cli-b", "web");
        _store.Say(flock.Id, FlockSessions.User, "api: check the schema.", ["cli-a"], tainted: false);
        _store.Join(flock.Id, "cli-c", "docs");
        var later = _store.Say(flock.Id, FlockSessions.User, "Everyone: we ship today.", [], tainted: false);
        flock = _store.Get(flock.Id)!;

        Assert.Equal(2, flock.Pending("cli-a").Count);
        Assert.Equal(later, Assert.Single(flock.Pending("cli-b")).Sequence);
        Assert.Equal(later, Assert.Single(flock.Pending("cli-c")).Sequence);
        Assert.Equal("you", flock.SenderName(flock.Messages[0].Message));
    }

    [Fact]
    public void Names_are_cleaned_and_made_unique_and_a_session_is_in_one_open_flock_at_most()
    {
        var flock = _store.Create("  Billing  Fix ");
        _store.Join(flock.Id, "cli-a", "api server");
        flock = _store.Join(flock.Id, "cli-b", "API server");
        flock = _store.Join(flock.Id, "cli-b", "ignored: already a member");

        Assert.Equal("Billing-Fix", flock.Name);
        Assert.Equal(["api-server", "API-server-2"], flock.Members.Select(member => member.Name));
        Assert.Same(flock.Find("api-server-2"), flock.Find("cli-b"));
        Assert.Throws<FlockException>(() => _store.Create("billing-fix"));
        var other = _store.Create("other");
        Assert.Contains("leave it first", Assert.Throws<FlockException>(() => _store.Join(other.Id, "cli-a", "x")).Message);
        Assert.Equal(flock.Id, _store.FlockOf("cli-a")!.Id);
        Assert.Equal(flock.Id, _store.Find("BILLING-FIX")!.Id);
        Assert.Equal(flock.Id, _store.Find(flock.Id[FlockSessions.Prefix.Length..])!.Id);
    }

    [Fact]
    public void A_child_session_a_stranger_and_a_closed_flock_are_refused()
    {
        _store.Log.Append("child-1", 0, [EventData.Create(new ChildOf("cli-a", 1, "sum it", [], new ChildBudget(4, 8_000, 120)))], TestContext.Current.CancellationToken);
        var flock = _store.Create("team");
        _store.Join(flock.Id, "cli-a", "api");

        Assert.Throws<FlockException>(() => _store.Join(flock.Id, "child-1", "kid"));
        Assert.Throws<FlockException>(() => _store.Join(flock.Id, flock.Id, "itself"));
        Assert.Contains("not in the flock", Assert.Throws<FlockException>(() => _store.Say(flock.Id, "cli-z", "hi", [], false)).Message);
        Assert.Contains("Nobody else", Assert.Throws<FlockException>(() => _store.Say(flock.Id, "cli-a", "hi", [], false)).Message);
        Assert.Throws<FlockException>(() => _store.Say(flock.Id, FlockSessions.User, new string('x', FlockStore.MaxText + 1), [], false));

        _store.Close(flock.Id);

        Assert.Empty(_store.List());
        Assert.Single(_store.List(includeClosed: true));
        Assert.Null(_store.FlockOf("cli-a"));
        Assert.Throws<FlockException>(() => _store.Join(flock.Id, "cli-b", "web"));
        Assert.Empty(_store.Get(flock.Id)!.Pending("cli-a"));
    }

    [Fact]
    public void The_loop_guard_counts_turns_members_start_on_each_other_and_the_user_resets_it()
    {
        var flock = _store.Create("loop");
        _store.Join(flock.Id, "cli-a", "a");
        _store.Join(flock.Id, "cli-b", "b");
        for (var i = 0; i < 3; i++)
        {
            var from = i % 2 == 0 ? "cli-a" : "cli-b";
            var to = i % 2 == 0 ? "cli-b" : "cli-a";
            var sequence = _store.Say(flock.Id, from, $"ping {i}", [], false);
            _store.Delivered(flock.Id, sequence, to, started: true);
        }
        // A steer starts no turn, and the user's own message is not a member's.
        var steered = _store.Say(flock.Id, "cli-a", "also this", [], false);
        _store.Delivered(flock.Id, steered, "cli-b", started: false);

        Assert.Equal(3, _store.Get(flock.Id)!.PeerTurns);
        Assert.False(_store.Pause(flock.Id, maxPeerTurns: 4).Paused);
        flock = _store.Pause(flock.Id, maxPeerTurns: 3);
        Assert.True(flock.Paused);
        Assert.Same(flock, _store.Pause(flock.Id, 3));

        _store.Say(flock.Id, FlockSessions.User, "carry on", [], false);
        flock = _store.Get(flock.Id)!;
        Assert.False(flock.Paused);
        Assert.Equal(0, flock.PeerTurns);
        // A pause decided on the count before the user wrote is not written after it.
        Assert.False(_store.Pause(flock.Id, 3).Paused);

        _store.Pause(flock.Id, 0);
        flock = _store.Resume(flock.Id);
        Assert.False(flock.Paused);
    }

    [Fact]
    public void One_name_is_one_flock_for_every_process_and_a_closed_name_starts_afresh()
    {
        var first = _store.FindOrCreate("Auth");
        // Another process's store, on the same log, asking for the same new name.
        var other = new FlockStore(_store.Log);

        Assert.Equal(first.Id, other.FindOrCreate("auth").Id);
        Assert.Equal(FlockStore.IdFor("auth", 1), first.Id);
        Assert.Throws<FlockException>(() => other.Create("AUTH"));

        _store.Close(first.Id);
        var again = other.FindOrCreate("auth");

        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(FlockStore.IdFor("auth", 2), again.Id);
        Assert.Contains("reserved", Assert.Throws<FlockException>(() => _store.Join(again.Id, FlockSessions.User, "me")).Message);
    }

    [Fact]
    public void A_flock_message_is_scrubbed_before_the_log_keeps_it()
    {
        const string token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
        var flock = _store.Create("keys");
        _store.Join(flock.Id, "cli-a", "api");
        _store.Join(flock.Id, "cli-b", "web");

        _store.Say(flock.Id, "cli-a", $"use {token} for now", [], false);

        var text = Assert.Single(_store.Get(flock.Id)!.Messages).Message.Text;
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains("ghp_[REDACTED:github-token:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_that_left_keeps_its_name_on_its_messages_and_gets_nothing_more()
    {
        var flock = _store.Create("short");
        _store.Join(flock.Id, "cli-a", "api");
        _store.Join(flock.Id, "cli-b", "web");
        _store.Say(flock.Id, "cli-a", "done on my side", [], false);
        flock = _store.Leave(flock.Id, "cli-a");
        _store.Say(flock.Id, FlockSessions.User, "thanks", [], false);
        flock = _store.Get(flock.Id)!;

        Assert.Equal("api", flock.SenderName(flock.Messages[0].Message));
        Assert.Empty(flock.Pending("cli-a"));
        Assert.Throws<FlockException>(() => _store.Leave(flock.Id, "cli-a"));
    }

    [Fact]
    public void The_flock_events_have_their_documented_persisted_form()
    {
        Assert.Equal("""
            {"type":"flock_created","name":"auth"}
            """, EventJson.Serialize(new FlockCreated("auth")));
        Assert.Equal("""
            {"type":"flock_member_joined","session_id":"cli-a","name":"api"}
            """, EventJson.Serialize(new FlockMemberJoined("cli-a", "api")));
        Assert.Equal("""
            {"type":"flock_message","from":"cli-a","text":"moved","to":["cli-b"],"tainted":true}
            """, EventJson.Serialize(new FlockMessage("cli-a", "moved", ["cli-b"], Tainted: true)));
        Assert.Equal("""
            {"type":"flock_delivered","sequence":4,"session_id":"cli-b","started":false}
            """, EventJson.Serialize(new FlockDelivered(4, "cli-b", Started: false)));
        Assert.Equal("""
            {"type":"user_message","text":"moved","from":"api","flock":"auth"}
            """, EventJson.Serialize(new UserMessage("moved", From: "api", Flock: "auth")));
        Assert.Equal("""{"type":"user_message","text":"hi"}""", EventJson.Serialize(new UserMessage("hi")));
    }

    [Fact]
    public async Task A_peer_message_neither_taints_nor_clears_and_is_quoted_as_a_colleague_note()
    {
        var generator = new FakeGenerator(Reply.Say("Read."), Reply.Say("Noted."), Reply.Say("Still careful."));
        using var harness = new TurnHarness(generator);
        var runner = harness.Runner();
        var peer = new PeerSender("api", "auth");

        var clean = await runner.RunAsync(new Envelope(TurnHarness.Session, "cli", "The endpoint moved.", EnvelopeOrigin.Peer) { Peer = peer },
            TestContext.Current.CancellationToken);
        Assert.False(clean.Tainted);
        var logged = harness.Payloads().OfType<UserMessage>().Single();
        Assert.Equal(("api", "auth"), (logged.From, logged.Flock));
        var prompt = string.Join("\n", generator.Calls[^1].Messages.Select(message => message.Text));
        Assert.Contains("another of the owner's sessions working with you in the flock auth", prompt, StringComparison.Ordinal);
        Assert.Contains("> The endpoint moved.", prompt, StringComparison.Ordinal);

        await harness.RunAsync(runner, "summarise https://evil.example", EnvelopeOrigin.External);
        var stillTainted = await runner.RunAsync(new Envelope(TurnHarness.Session, "cli", "Go on.", EnvelopeOrigin.Peer) { Peer = peer },
            TestContext.Current.CancellationToken);
        Assert.True(stillTainted.Tainted);
    }

    [Fact]
    public async Task An_always_offered_tool_is_offered_though_the_filter_withholds_it_and_the_decider_picks_another()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":5}"""), Reply.Say("You have no notes."))
        {
            YesNo = FakeGenerator.YesTo("fully handled"),
        };
        var options = TurnOptions.Default with
        {
            ToolFilter = tool => tool.Name != "clear_notes",
            AlwaysOffered = session => session == TurnHarness.Session ? ["clear_notes"] : [],
        };
        using var harness = new TurnHarness(generator, options: options);

        await harness.RunAsync("list my notes");

        Assert.Equal(["clear_notes", "list_notes"], generator.Calls[0].ToolNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Flock_send_writes_to_the_flock_with_the_turn_s_taint_and_names_who_is_in_it()
    {
        var flock = _store.Create("team");
        _store.Join(flock.Id, "cli-a", "api");
        _store.Join(flock.Id, "cli-b", "web");
        var tool = new FlockSendTool(() => _store);

        var roster = await Invoke(tool, "cli-a", tainted: false, """{}""");
        var sent = await Invoke(tool, "cli-a", tainted: true, """{"text":"Schema changed.","to":"@web"}""");
        var outside = await Invoke(tool, "cli-z", tainted: false, """{"text":"hi"}""");

        Assert.Equal("You are api in the flock team. The others: web.", roster);
        Assert.StartsWith("Sent to web.", sent, StringComparison.Ordinal);
        Assert.Contains("not in a flock", outside, StringComparison.Ordinal);
        var message = Assert.Single(_store.Get(flock.Id)!.Messages).Message;
        Assert.Equal(("cli-a", true), (message.From, message.Tainted));
        Assert.Equal(["cli-b"], message.To);
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(tool, "cli-a", false, """{"text":"x","to":"nobody"}"""));
        // A name that comes to nothing is a mistake, never everyone.
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(tool, "cli-a", false, """{"text":"x","to":"@"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(tool, "cli-a", false, """{"text":"x","to":["  "]}"""));
        Assert.Single(_store.Get(flock.Id)!.Messages);
        Assert.Equal(SideEffects.None, tool.Descriptor.Risk.SideEffects);
    }

    private static async Task<string> Invoke(FlockSendTool tool, string session, bool tainted, string json)
    {
        var arguments = new AIFunctionArguments(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);
        arguments = new ToolCallContext(session, "call-1") { Tainted = tainted }.AttachTo(arguments);
        return (string)(await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken))!;
    }
}
