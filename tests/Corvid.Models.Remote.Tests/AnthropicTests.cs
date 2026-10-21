using System.Collections.Immutable;
using System.Net;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Extraction;
using Corvid.Models.Anthropic;
using Corvid.Models.Privacy;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Remote.Tests;

public sealed class AnthropicTests : IAsyncDisposable
{
    private const string Reply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"Hello from Claude"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":12,"output_tokens":4,"cache_creation_input_tokens":30,"cache_read_input_tokens":900}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SecretRef Key = new("anthropic.key");

    private readonly FakeVault _vault = new(_ => FakeVault.Json(Reply));
    private readonly FakeNetwork _network = new();
    private readonly ModelProfile _profile = Profiles.Cloud("claude-opus-5-5");

    public async ValueTask DisposeAsync() => await _vault.DisposeAsync();

    private IChatClient Client(AnthropicModelOptions? options = null) =>
        AnthropicBindings.CreateChatClient(_profile, options ?? new AnthropicModelOptions("claude-opus-5-5"), Key, _vault.Access(), _network);

    private static List<ChatMessage> Conversation() =>
    [
        new(ChatRole.System, "You are Corvid."),
        new(ChatRole.User, "list my notes"),
        new(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "list_notes", new Dictionary<string, object?> { ["folder"] = "inbox" })]),
        new(ChatRole.Tool, [new FunctionResultContent("toolu_1", Profiles.Json("""["a.md","b.md"]"""))]),
        new(ChatRole.Assistant, "You have two notes."),
        new(ChatRole.User, "open the first"),
    ];

    private static ChatOptions Tools() => new()
    {
        MaxOutputTokens = 512,
        Tools = [AIFunctionFactory.Create((string folder) => folder, "list_notes", "List the notes in a folder.")],
        ToolMode = ChatToolMode.Auto,
    };

    [Fact]
    public async Task The_request_goes_through_vault_with_a_placeholder_key_and_the_native_messages_shape()
    {
        var response = await Client().GetResponseAsync(Conversation(), Tools(), Ct);

        var request = Assert.Single(_vault.Forwarded);
        Assert.Equal(("POST", "https://api.anthropic.com/v1/messages"), (request.Method, new Uri(request.Url).GetLeftPart(UriPartial.Path)));
        var placeholder = Assert.Single(_vault.Minted);
        Assert.StartsWith("vsec_anthropic.key_", placeholder, StringComparison.Ordinal);
        Assert.Equal(placeholder, FakeVault.Header(request, "x-api-key"));
        Assert.Null(FakeVault.Header(request, "Authorization"));
        Assert.Equal(ModelHttp.Toolset, request.Toolset);
        Assert.Empty(_network.Requests);

        var body = FakeVault.Body(request);
        Assert.Equal("claude-opus-5-5", (string)body["model"]!);
        Assert.Equal(512, (int)body["max_tokens"]!);
        Assert.Equal("You are Corvid.", (string)body["system"]![0]!["text"]!);
        var messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user", "assistant", "user"], messages.Select(message => (string)message!["role"]!));
        var toolUse = messages[1]!["content"]![0]!;
        Assert.Equal(("tool_use", "toolu_1", "list_notes", "inbox"),
            ((string)toolUse["type"]!, (string)toolUse["id"]!, (string)toolUse["name"]!, (string)toolUse["input"]!["folder"]!));
        Assert.Equal(("tool_result", "toolu_1"), ((string)messages[2]!["content"]![0]!["type"]!, (string)messages[2]!["content"]![0]!["tool_use_id"]!));
        var tool = body["tools"]![0]!;
        Assert.Equal(("list_notes", "object"), ((string)tool["name"]!, (string)tool["input_schema"]!["type"]!));

        Assert.Equal("Hello from Claude", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Usage!.InputTokenCount);
        Assert.Equal(4, response.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task Cache_breakpoints_follow_hermes_layout_the_system_prompt_and_the_last_three_messages()
    {
        await Client().GetResponseAsync(Conversation(), Tools(), Ct);

        var body = FakeVault.Body(Assert.Single(_vault.Forwarded));
        Assert.Equal("ephemeral", (string)body["system"]![0]!["cache_control"]!["type"]!);
        var marked = body["messages"]!.AsArray().Select(message => message!["content"]!.AsArray()[^1]!["cache_control"] is not null).ToArray();
        Assert.Equal([false, false, true, true, true], marked);
        // Four breakpoints, the API's maximum, and none on the tools (the system breakpoint covers them).
        Assert.Null(body["tools"]![0]!["cache_control"]);
        var breakpoints = body.ToJsonString().Split("\"cache_control\"").Length - 1;
        Assert.Equal(4, breakpoints);
    }

    [Fact]
    public async Task Corvid_keys_never_reach_the_provider_and_taint_reaches_vault()
    {
        var options = new ModelRequestContext("s1", Tainted: true).Apply(Tools());

        await Client().GetResponseAsync(Conversation(), options, Ct);

        var request = Assert.Single(_vault.Forwarded);
        Assert.True(request.Tainted);
        Assert.DoesNotContain("corvid.", FakeVault.Body(request).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_environment_key_is_never_used()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-from-the-environment");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "token-from-the-environment");
        try
        {
            await Client().GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        }

        var request = Assert.Single(_vault.Forwarded);
        Assert.Equal(_vault.Minted.Single(), FakeVault.Header(request, "x-api-key"));
        Assert.DoesNotContain(request.Headers, header => header.Value.Contains("environment", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(429, HttpStatusCode.TooManyRequests)]
    [InlineData(529, (HttpStatusCode)529)]
    [InlineData(400, HttpStatusCode.BadRequest)]
    public async Task Api_errors_keep_their_status_for_the_fallback_chain(int status, HttpStatusCode expected)
    {
        _vault.Forward = _ => FakeVault.Json("""{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", status);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Client().GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Equal(expected, failure.StatusCode);
        Assert.Equal(status is 429 or >= 500, FallbackErrors.IsProviderFailure(failure));
        Assert.Single(_vault.Forwarded);
    }

    [Theory]
    [InlineData(VaultErrorCodes.RateLimited, HttpStatusCode.TooManyRequests)]
    [InlineData(VaultErrorCodes.HostNotBound, HttpStatusCode.Forbidden)]
    [InlineData(VaultErrorCodes.Denied, HttpStatusCode.Forbidden)]
    public async Task Vault_refusals_become_statuses_and_only_throttling_falls_back(string code, HttpStatusCode expected)
    {
        _vault.Refusals.Enqueue((code, "policy"));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Client().GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Equal(expected, failure.StatusCode);
        Assert.Contains(code, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_placeholder_vault_no_longer_knows_is_minted_again_and_the_request_retried_once()
    {
        _vault.Refusals.Enqueue((VaultErrorCodes.BadPlaceholder, "expired"));

        var response = await Client().GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct);

        Assert.Equal("Hello from Claude", response.Text);
        Assert.Equal(2, _vault.Minted.Count);
        Assert.Equal(2, _vault.Forwarded.Count);
    }

    [Fact]
    public async Task Without_vault_running_the_request_fails_as_unreachable_and_nothing_goes_direct()
    {
        var gone = VaultAccess.Create(Path.Combine(Corvid.Tests.ShortTemp.Name("no-vault-"), "vault.sock"));
        var client = AnthropicBindings.CreateChatClient(_profile, new AnthropicModelOptions("claude-opus-5-5"), Key, gone, _network);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Null(failure.StatusCode);
        Assert.Empty(_network.Requests);
        Assert.Throws<VaultUnavailableException>(() =>
            AnthropicBindings.CreateChatClient(_profile, new AnthropicModelOptions("claude-opus-5-5"), Key, vault: null, _network));
    }

    [Fact]
    public async Task A_required_tool_choice_is_refused_before_sending_unless_the_model_accepts_it()
    {
        var forced = Tools();
        forced.ToolMode = ChatToolMode.RequireSpecific("list_notes");

        await Assert.ThrowsAsync<NotSupportedException>(() => Client().GetResponseAsync(Conversation(), forced, Ct));
        Assert.Empty(_vault.Forwarded);

        await Client(new AnthropicModelOptions("claude-opus-4-6", ForcedToolChoice: true)).GetResponseAsync(Conversation(), forced, Ct);
        Assert.Equal(("tool", "list_notes"), ((string)FakeVault.Body(_vault.Forwarded.Single())["tool_choice"]!["type"]!,
            (string)FakeVault.Body(_vault.Forwarded.Single())["tool_choice"]!["name"]!));
    }

    [Fact]
    public async Task Behind_the_egress_gate_the_provider_sees_placeholders_and_the_caller_sees_values()
    {
        _vault.Forward = _ => FakeVault.Json(Reply.Replace("Hello from Claude", "Booked for <PERSON_1>.", StringComparison.Ordinal));
        var egress = new PrivacyEgress(new Names("Alice Smith"));
        var client = new PrivacyEgressChatClient(Client(), _profile, egress);

        var response = await client.GetResponseAsync([new(ChatRole.User, "Book a table for Alice Smith")],
            new ModelRequestContext("s1", false).Apply(null), Ct);

        var sent = FakeVault.Body(_vault.Forwarded.Single()).ToJsonString();
        Assert.DoesNotContain("Alice", sent, StringComparison.Ordinal);
        Assert.Contains("\\u003CPERSON_1\\u003E", sent, StringComparison.Ordinal);
        Assert.Equal("Booked for Alice Smith.", response.Text);
    }

    private sealed class Names(params string[] names) : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExtractionResult([.. names.Where(name => request.Text.Contains(name, StringComparison.Ordinal))
                .Select(name => new ExtractedSpan("person", name, request.Text.IndexOf(name, StringComparison.Ordinal),
                    request.Text.IndexOf(name, StringComparison.Ordinal) + name.Length, 0.95f))]));
    }
}
