using System.Collections.Concurrent;
using Corvid.Gateway.Discord;
using Corvid.Models.Remote.Tests;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Gateway.Tests;

public sealed class DiscordAdapterTests
{
    private static readonly DiscordOptions Options = new(new SecretRef("discord-bot"))
    {
        Channels = ["G1"],
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private sealed class Discord
    {
        private int _guildPolls;
        private int _dmPolls;

        public ConcurrentQueue<string> Paths { get; } = new();

        public ForwardResponse Answer(ForwardRequest request)
        {
            var path = request.Url["https://discord.com/api/v10/".Length..];
            Paths.Enqueue(request.Method + " " + path);
            return (request.Method, path) switch
            {
                ("GET", "users/@me") => PlatformBehindVault.Json("""{"id":"BOT","username":"corvid"}"""),
                ("POST", "users/@me/channels") => PlatformBehindVault.Json("""{"id":"DM1","type":1}"""),
                ("GET", "channels/G1/messages?limit=1") => PlatformBehindVault.Json("""[{"id":"100"}]"""),
                ("GET", "channels/DM1/messages?limit=1") => PlatformBehindVault.Json("[]"),
                ("GET", "channels/G1/messages?after=100&limit=50") when Interlocked.Increment(ref _guildPolls) == 1 => PlatformBehindVault.Json("""
                    [{"id":"102","author":{"id":"U2","username":"ada","global_name":"Ada"},"content":"<@BOT> what's new?","mentions":[{"id":"BOT"}]},
                     {"id":"101","author":{"id":"U1","username":"mark"},"content":"just chatting","mentions":[]}]
                    """),
                ("GET", "channels/DM1/messages?after=0&limit=50") when Interlocked.Increment(ref _dmPolls) == 1 => PlatformBehindVault.Json("""
                    [{"id":"6","author":{"id":"BOT","bot":true},"content":"my own message","mentions":[]},
                     {"id":"5","author":{"id":"U1","username":"mark"},"content":"hello","mentions":[]}]
                    """),
                ("GET", _) when path.Contains("/messages?after=", StringComparison.Ordinal) => PlatformBehindVault.Json("[]"),
                ("POST", "channels/C1/messages/9/threads") => PlatformBehindVault.Json("""{"id":"T1"}"""),
                ("POST", "channels/C1/messages/10/threads") => PlatformBehindVault.Json(
                    """{"message":"A thread has already been created for this message","code":160004}""", 400),
                ("POST", "channels/DM1/messages/11/threads") => PlatformBehindVault.Json(
                    """{"message":"Cannot execute action on this channel type","code":50024}""", 400),
                ("POST", "channels/LIMITED/messages") => PlatformBehindVault.Json("""{"message":"You are being rate limited.","retry_after":1.5}""", 429),
                ("POST", "channels/GONE/messages") => PlatformBehindVault.Json("""{"message":"Missing Access","code":50001}""", 403),
                ("POST", _) => PlatformBehindVault.Json("""{"id":"900"}"""),
                _ => PlatformBehindVault.Json("""{"message":"404: Not Found"}""", 404),
            };
        }
    }

    [Fact]
    public async Task PollsWatchedChannelsAndKnownUsersDirectMessagesThroughVault()
    {
        var discord = new Discord();
        await using var platform = new PlatformBehindVault(discord.Answer);
        await using var adapter = DiscordAdapter.Create(Options, platform.Gateway);
        var context = new MemoryContext("U1");
        await adapter.ConnectAsync(context, TestContext.Current.CancellationToken);

        // A channel seen for the first time starts at its newest message; the DM, empty, from the start.
        Assert.Equal("100", context.GetCursor("G1"));
        Assert.Equal("0", context.GetCursor("DM1"));

        var received = new List<InboundMessage>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = adapter.ReceiveAsync((message, _) =>
        {
            lock (received)
                received.Add(message);
            return Task.CompletedTask;
        }, stop.Token);
        await Wait.Until(() => context.GetCursor("G1") == "102" && context.GetCursor("DM1") == "6", "both channels read");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);

        Assert.All(platform.Requests, request =>
        {
            Assert.StartsWith("Bot vsec_discord-bot_", FakeVault.Header(request, "Authorization"), StringComparison.Ordinal);
            Assert.Equal(GatewayHttp.Toolset, request.Toolset);
        });
        // Oldest first; the bot's own message is skipped; a mention addresses it and is removed.
        Assert.Equal(3, received.Count);
        Assert.Equal(("G1", "U1", "just chatting", false, false), (received[0].ChatId, received[0].SenderId, received[0].Text, received[0].Direct, received[0].Addressed));
        Assert.Equal(("G1", "U2", "what's new?", true, "Ada"), (received[1].ChatId, received[1].SenderId, received[1].Text, received[1].Addressed, received[1].SenderName));
        Assert.Equal(("DM1", "U1", "hello", true, true), (received[2].ChatId, received[2].SenderId, received[2].Text, received[2].Direct, received[2].Addressed));
    }

    [Fact]
    public async Task RepliesNeverPingAnyoneAndThreadsStartOnTheMessage()
    {
        var discord = new Discord();
        await using var platform = new PlatformBehindVault(discord.Answer);
        await using var adapter = DiscordAdapter.Create(Options, platform.Gateway);

        await adapter.SendAsync(new OutboundMessage("C1", "@everyone look", ReplyTo: "8", Tainted: true), TestContext.Current.CancellationToken);
        await adapter.SendAsync(new OutboundMessage("C1", new string('y', 2500), ReplyTo: "9", ThreadId: "9"), TestContext.Current.CancellationToken);

        var requests = platform.Requests;
        var reply = PlatformBehindVault.Body(requests[0])!;
        Assert.EndsWith("channels/C1/messages", requests[0].Url, StringComparison.Ordinal);
        Assert.Empty(reply["allowed_mentions"]!["parse"]!.AsArray());
        Assert.Equal("8", reply["message_reference"]!["message_id"]!.GetValue<string>());
        Assert.True(requests[0].Tainted);
        Assert.EndsWith("channels/C1/messages/9/threads", requests[1].Url, StringComparison.Ordinal);
        Assert.EndsWith("channels/T1/messages", requests[2].Url, StringComparison.Ordinal);
        Assert.EndsWith("channels/T1/messages", requests[3].Url, StringComparison.Ordinal);
        Assert.Equal(DiscordAdapter.MaxMessageLength, PlatformBehindVault.Body(requests[2])!["content"]!.GetValue<string>().Length);
        Assert.Null(PlatformBehindVault.Body(requests[2])!["message_reference"]);
    }

    [Fact]
    public async Task AThreadReplySentAgainFindsItsThreadAndOneWhereThreadsCannotBeIsAPlainReply()
    {
        var discord = new Discord();
        await using var platform = new PlatformBehindVault(discord.Answer);
        await using var adapter = DiscordAdapter.Create(Options, platform.Gateway);

        // At least once: the thread was made by an earlier attempt whose message failed.
        await adapter.SendAsync(new OutboundMessage("C1", "again", ReplyTo: "10", ThreadId: "10"), TestContext.Current.CancellationToken);
        await adapter.SendAsync(new OutboundMessage("DM1", "in a DM", ReplyTo: "11", ThreadId: "11"), TestContext.Current.CancellationToken);

        var requests = platform.Requests;
        Assert.EndsWith("channels/10/messages", requests[1].Url, StringComparison.Ordinal);
        Assert.EndsWith("channels/DM1/messages", requests[3].Url, StringComparison.Ordinal);
        Assert.Equal("11", PlatformBehindVault.Body(requests[3])!["message_reference"]!["message_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusalsSayWhetherARetryCanHelp()
    {
        var discord = new Discord();
        await using var platform = new PlatformBehindVault(discord.Answer);
        await using var adapter = DiscordAdapter.Create(Options, platform.Gateway);

        var limited = await Assert.ThrowsAsync<GatewaySendException>(() => adapter.SendAsync(new OutboundMessage("LIMITED", "x"), TestContext.Current.CancellationToken));
        var gone = await Assert.ThrowsAsync<GatewaySendException>(() => adapter.SendAsync(new OutboundMessage("GONE", "x"), TestContext.Current.CancellationToken));

        Assert.False(limited.Permanent);
        Assert.Equal(TimeSpan.FromSeconds(1.5), limited.RetryAfter);
        Assert.True(gone.Permanent);
    }
}
