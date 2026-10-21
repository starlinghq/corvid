using System.Collections.Concurrent;
using Corvid.Gateway.Telegram;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Gateway.Tests;

public sealed class TelegramAdapterTests
{
    private static readonly TelegramOptions Options = new(new SecretRef("telegram-bot")) { PollTimeout = TimeSpan.FromSeconds(1) };

    private const string Updates = """
        {"ok":true,"result":[
          {"update_id":10,"message":{"message_id":1,"from":{"id":42,"is_bot":false,"username":"mark"},"chat":{"id":42,"type":"private"},"text":"hello"}},
          {"update_id":11,"message":{"message_id":2,"from":{"id":43,"is_bot":false,"first_name":"Ada"},"chat":{"id":-100,"type":"supergroup"},"text":"@corvid_bot what's up?"}},
          {"update_id":12,"message":{"message_id":3,"from":{"id":43,"is_bot":false},"chat":{"id":-100,"type":"supergroup"},"text":"just chatting"}},
          {"update_id":13,"message":{"message_id":4,"from":{"id":7,"is_bot":true},"chat":{"id":42,"type":"private"},"text":"bot noise"}},
          {"update_id":14,"message":{"message_id":"not a number","from":{"id":42},"chat":{"id":42,"type":"private"},"text":"malformed"}},
          {"update_id":15,"message":{"message_id":5,"from":{"id":43,"is_bot":false},"chat":{"id":-100,"type":"supergroup"},"text":"/status@other_bot"}},
          {"update_id":16,"message":{"message_id":6,"from":{"id":43,"is_bot":false},"chat":{"id":-100,"type":"supergroup"},"is_topic_message":true,"message_thread_id":77,"reply_to_message":{"from":{"id":999}},"text":"in a topic"}}
        ]}
        """;

    [Fact]
    public async Task ReceivesThroughVaultWithThePlaceholderInThePathAndConfirmsUpdatesAfterwards()
    {
        var polls = new ConcurrentQueue<string>();
        await using var platform = new PlatformBehindVault(request =>
        {
            var method = request.Url[(request.Url.LastIndexOf('/') + 1)..];
            if (method.StartsWith("getMe", StringComparison.Ordinal))
                return PlatformBehindVault.Json("""{"ok":true,"result":{"id":999,"username":"corvid_bot"}}""");
            polls.Enqueue(method);
            if (polls.Count == 1)
                return PlatformBehindVault.Json(Updates);
            Thread.Sleep(20);
            return PlatformBehindVault.Json("""{"ok":true,"result":[]}""");
        });
        await using var adapter = TelegramAdapter.Create(Options, platform.Gateway);
        await adapter.ConnectAsync(new MemoryContext(), TestContext.Current.CancellationToken);

        var received = new List<InboundMessage>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = adapter.ReceiveAsync((message, _) =>
        {
            lock (received)
                received.Add(message);
            return Task.CompletedTask;
        }, stop.Token);
        await Wait.Until(() => polls.Count >= 2, "a second poll");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);

        // Every request went through Vault, with a minted placeholder where the token goes, for the gateway's toolset.
        Assert.All(platform.Requests, request =>
        {
            Assert.StartsWith("https://api.telegram.org/botvsec_telegram-bot_", request.Url, StringComparison.Ordinal);
            Assert.Contains(platform.Vault.Minted.Single(), request.Url, StringComparison.Ordinal);
            Assert.DoesNotContain(VaultUrlSecretHandler.Marker, request.Url, StringComparison.Ordinal);
            Assert.Equal(GatewayHttp.Toolset, request.Toolset);
            Assert.DoesNotContain(request.Headers, header => header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        });
        // The next poll confirms everything taken, malformed updates included (skipped, not retried forever).
        Assert.Contains("offset=17", polls.ElementAt(1), StringComparison.Ordinal);

        Assert.Equal(4, received.Count);
        Assert.Equal(("42", "42", "hello", true, true, "mark"), (received[0].ChatId, received[0].SenderId, received[0].Text, received[0].Direct,
            received[0].Addressed, received[0].SenderName));
        // A mention addresses the bot and is removed from the text.
        Assert.Equal(("-100", "what's up?", false, true, "Ada"), (received[1].ChatId, received[1].Text, received[1].Direct, received[1].Addressed,
            received[1].SenderName));
        Assert.False(received[2].Addressed);
        // A reply to the bot in a forum topic: addressed, and the topic is the thread.
        Assert.Equal(("in a topic", true, "77", "6"), (received[3].Text, received[3].Addressed, received[3].ThreadId, received[3].MessageId));
    }

    [Fact]
    public async Task SendsRepliesInPiecesAndTellsVaultWhenTheTextIsTainted()
    {
        await using var platform = new PlatformBehindVault(request =>
            PlatformBehindVault.Json("""{"ok":true,"result":{"message_id":555}}"""));
        await using var adapter = TelegramAdapter.Create(Options, platform.Gateway);

        var sent = await adapter.SendAsync(new OutboundMessage("-100", new string('x', 5000), ReplyTo: "2", ThreadId: "77", Tainted: true),
            TestContext.Current.CancellationToken);
        await adapter.SendAsync(new OutboundMessage("42", "in a thread", ReplyTo: "1", ThreadId: "1"), TestContext.Current.CancellationToken);

        Assert.Equal("555", sent.MessageId);
        var requests = platform.Requests;
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.EndsWith("/sendMessage", request.Url, StringComparison.Ordinal));
        var first = PlatformBehindVault.Body(requests[0])!;
        Assert.Equal(-100, first["chat_id"]!.GetValue<long>());
        Assert.Equal(TelegramAdapter.MaxMessageLength, first["text"]!.GetValue<string>().Length);
        Assert.Equal(2, first["reply_parameters"]!["message_id"]!.GetValue<long>());
        Assert.Equal(77, first["message_thread_id"]!.GetValue<long>());
        Assert.True(requests[0].Tainted);
        Assert.Equal(904, PlatformBehindVault.Body(requests[1])!["text"]!.GetValue<string>().Length);
        // Outside a forum, "reply in a thread" is a reply: no topic id.
        Assert.Null(PlatformBehindVault.Body(requests[2])!["message_thread_id"]);
        Assert.False(requests[2].Tainted);
    }

    [Fact]
    public async Task RefusalsSayWhetherARetryCanHelp()
    {
        await using var platform = new PlatformBehindVault(request => PlatformBehindVault.Body(request)!["chat_id"]!.GetValue<long>() switch
        {
            1 => PlatformBehindVault.Json("""{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""", 400),
            _ => PlatformBehindVault.Json("""{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}""", 429),
        });
        await using var adapter = TelegramAdapter.Create(Options, platform.Gateway);

        var gone = await Assert.ThrowsAsync<GatewaySendException>(() => adapter.SendAsync(new OutboundMessage("1", "hi"), TestContext.Current.CancellationToken));
        var limited = await Assert.ThrowsAsync<GatewaySendException>(() => adapter.SendAsync(new OutboundMessage("2", "hi"), TestContext.Current.CancellationToken));

        Assert.True(gone.Permanent);
        Assert.False(limited.Permanent);
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);
    }

    [Theory]
    // A tainted answer under the secret's default "tainted: ask" rule, or a secret not bound to the host: a retry cannot help.
    [InlineData(VaultErrorCodes.ApprovalRequired, true)]
    [InlineData(VaultErrorCodes.HostNotBound, true)]
    [InlineData(VaultErrorCodes.ToolsetNotAllowed, true)]
    [InlineData(VaultErrorCodes.RateLimited, false)]
    [InlineData(VaultErrorCodes.BudgetExceeded, false)]
    [InlineData(VaultErrorCodes.UpstreamError, false)]
    public async Task ASendVaultRefusesIsGivenUpOnlyWhenARetryCannotHelp(string code, bool permanent)
    {
        await using var platform = new PlatformBehindVault(_ => PlatformBehindVault.Json("""{"ok":true,"result":{"message_id":1}}"""));
        await using var adapter = TelegramAdapter.Create(Options, platform.Gateway);
        platform.Vault.Refusals.Enqueue((code, "policy says no"));

        var refused = await Assert.ThrowsAsync<GatewaySendException>(() =>
            adapter.SendAsync(new OutboundMessage("42", "hi", Tainted: true), TestContext.Current.CancellationToken));

        Assert.Equal(permanent, refused.Permanent);
        Assert.Contains("policy says no", refused.Message, StringComparison.Ordinal);
        Assert.True(Assert.Single(platform.Requests).Tainted);
    }

    [Fact]
    public async Task WithoutVaultNothingIsSent()
    {
        var gateway = GatewayVault.Create(Path.Combine(Path.GetTempPath(), "no-vault-" + Guid.NewGuid().ToString("N"), "vault.sock"));
        await using var adapter = TelegramAdapter.Create(Options, gateway);

        await Assert.ThrowsAnyAsync<Exception>(() => adapter.ConnectAsync(new MemoryContext(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARequestThatDoesNotSayWhereItsSecretGoesIsRefused()
    {
        await using var platform = new PlatformBehindVault(_ => PlatformBehindVault.Json("{}"));
        using var client = GatewayHttp.CreateForUrl(platform.Gateway, new SecretRef("telegram-bot"), TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(new Uri("https://api.telegram.org/getMe"), TestContext.Current.CancellationToken));
        Assert.Empty(platform.Requests);
        Assert.Empty(platform.Vault.Minted);
    }
}
