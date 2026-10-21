using System.Text.Json.Nodes;
using Corvid.Gateway.Slack;
using Corvid.Models.Remote.Tests;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Gateway.Tests;

public sealed class SlackAdapterTests
{
    private static readonly SlackOptions Options = new(new SecretRef("slack-bot"), new SecretRef("slack-app"));

    private static ForwardResponse Api(ForwardRequest request) => request.Url switch
    {
        "https://slack.com/api/auth.test" => PlatformBehindVault.Json("""{"ok":true,"user_id":"UBOT"}"""),
        "https://slack.com/api/apps.connections.open" => PlatformBehindVault.Json("""{"ok":true,"url":"wss://wss-primary.slack.com/link/?ticket=t1"}"""),
        "https://slack.com/api/chat.postMessage" => PlatformBehindVault.Body(request)!["channel"]!.GetValue<string>() switch
        {
            "CGONE" => PlatformBehindVault.Json("""{"ok":false,"error":"channel_not_found"}"""),
            "CBUSY" => PlatformBehindVault.Json("""{"ok":false,"error":"ratelimited"}""", 429, new WireHeader("Retry-After", "3")),
            _ => PlatformBehindVault.Json("""{"ok":true,"ts":"1700000000.000200"}"""),
        },
        _ => PlatformBehindVault.Json("""{"ok":false,"error":"unknown_method"}""", 404),
    };

    private static string Event(string id, string json) =>
        "{\"envelope_id\":\"" + id + "\",\"type\":\"events_api\",\"payload\":{\"event\":" + json + "}}";

    [Fact]
    public async Task OpensSocketModeWithTheAppTokenAndAcknowledgesEveryEnvelope()
    {
        await using var platform = new PlatformBehindVault(Api);
        var sockets = new FakeSockets();
        await using var adapter = SlackAdapter.Create(Options, platform.Gateway, sockets);
        await adapter.ConnectAsync(new MemoryContext(), TestContext.Current.CancellationToken);

        // Each token is its own Vault secret, sent as a Bearer placeholder; the WebSocket URL is opened directly.
        var requests = platform.Requests;
        Assert.StartsWith("Bearer vsec_slack-bot_", FakeVault.Header(requests[0], "Authorization"), StringComparison.Ordinal);
        Assert.StartsWith("Bearer vsec_slack-app_", FakeVault.Header(requests[1], "Authorization"), StringComparison.Ordinal);
        Assert.All(requests, request => Assert.Equal(GatewayHttp.Toolset, request.Toolset));
        Assert.Equal(new Uri("wss://wss-primary.slack.com/link/?ticket=t1"), Assert.Single(sockets.Opened));

        var received = new List<InboundMessage>();
        var loop = adapter.ReceiveAsync((message, _) =>
        {
            lock (received)
                received.Add(message);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        var socket = sockets.Current!;
        socket.Push("""{"type":"hello"}""");
        socket.Push(Event("e1", """{"type":"message","channel_type":"im","channel":"D1","user":"U1","text":"hi &amp; &lt;there&gt;","ts":"1.1"}"""));
        socket.Push(Event("e2", """{"type":"app_mention","channel":"C1","user":"U2","text":"<@UBOT> status?","ts":"2.1","thread_ts":"2.0"}"""));
        // A channel message that does not mention the bot, a bot's own message and an edit are not for the agent.
        socket.Push(Event("e3", """{"type":"message","channel_type":"channel","channel":"C1","user":"U2","text":"chatting","ts":"3.1"}"""));
        socket.Push(Event("e4", """{"type":"message","channel_type":"im","channel":"D1","bot_id":"B1","user":"UBOT","text":"echo","ts":"4.1"}"""));
        socket.Push(Event("e5", """{"type":"message","subtype":"message_changed","channel_type":"im","channel":"D1","user":"U1","ts":"5.1"}"""));
        socket.Push("""{"type":"disconnect","reason":"refresh_requested"}""");

        var closed = await Assert.ThrowsAsync<IOException>(() => loop);
        Assert.Contains("refresh_requested", closed.Message, StringComparison.Ordinal);
        Assert.Equal(["e1", "e2", "e3", "e4", "e5"], socket.Sent.Select(frame => JsonNode.Parse(frame)!["envelope_id"]!.GetValue<string>()));
        Assert.Equal(2, received.Count);
        Assert.Equal(("D1", "U1", "hi & <there>", true, "1.1"), (received[0].ChatId, received[0].SenderId, received[0].Text, received[0].Direct,
            received[0].MessageId));
        Assert.Equal(("C1", "status?", false, true, "2.0"), (received[1].ChatId, received[1].Text, received[1].Direct, received[1].Addressed,
            received[1].ThreadId));
    }

    [Fact]
    public async Task PostsWithTheBotTokenInTheThreadAndClassifiesRefusals()
    {
        await using var platform = new PlatformBehindVault(Api);
        await using var adapter = SlackAdapter.Create(Options, platform.Gateway, new FakeSockets());

        var sent = await adapter.SendAsync(new OutboundMessage("C1", "the answer", ReplyTo: "2.1", ThreadId: "2.0", Tainted: true),
            TestContext.Current.CancellationToken);
        var gone = await Assert.ThrowsAsync<GatewaySendException>(() => adapter.SendAsync(new OutboundMessage("CGONE", "x"), TestContext.Current.CancellationToken));
        var busy = await Assert.ThrowsAsync<GatewaySendException>(() => adapter.SendAsync(new OutboundMessage("CBUSY", "x"), TestContext.Current.CancellationToken));

        Assert.Equal("1700000000.000200", sent.MessageId);
        var post = platform.Requests[0];
        Assert.StartsWith("Bearer vsec_slack-bot_", FakeVault.Header(post, "Authorization"), StringComparison.Ordinal);
        Assert.True(post.Tainted);
        var body = PlatformBehindVault.Body(post)!;
        Assert.Equal(("C1", "the answer", "2.0"), (body["channel"]!.GetValue<string>(), body["text"]!.GetValue<string>(), body["thread_ts"]!.GetValue<string>()));
        Assert.True(gone.Permanent);
        Assert.False(busy.Permanent);
        Assert.Equal(TimeSpan.FromSeconds(3), busy.RetryAfter);
    }

    [Fact]
    public async Task TextAModelWroteCannotPingAChannelOrAPerson()
    {
        await using var platform = new PlatformBehindVault(Api);
        await using var adapter = SlackAdapter.Create(Options, platform.Gateway, new FakeSockets());

        await adapter.SendAsync(new OutboundMessage("C1", "<!channel> look: a < b & <@U1>"), TestContext.Current.CancellationToken);

        Assert.Equal("&lt;!channel&gt; look: a &lt; b &amp; &lt;@U1&gt;",
            PlatformBehindVault.Body(Assert.Single(platform.Requests))!["text"]!.GetValue<string>());
        // Escaping can make a piece five times as long; the longest still fits under Slack's 40,000 characters.
        Assert.True(SlackAdapter.Escape(new string('&', SlackAdapter.MaxMessageLength)).Length <= 40_000);
    }
}
