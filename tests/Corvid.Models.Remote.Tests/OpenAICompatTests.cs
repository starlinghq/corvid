using System.Net;
using System.Text.Json.Nodes;
using Corvid.Models.OpenAICompat;
using Corvid.Models.Privacy;
using Corvid.Vault.Client;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Remote.Tests;

public sealed class OpenAICompatTests : IAsyncDisposable
{
    private const string Reply = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1760000000,"model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello from the server"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeVault _vault = new(_ => FakeVault.Json(Reply));

    public async ValueTask DisposeAsync() => await _vault.DisposeAsync();

    private static ChatOptions Tools() => new()
    {
        MaxOutputTokens = 256,
        Tools = [AIFunctionFactory.Create((string city) => city, "get_weather", "Weather for a city.")],
    };

    [Fact]
    public async Task A_keyless_local_server_is_called_directly_with_no_credential_header()
    {
        var network = new FakeNetwork((_, _) => FakeNetwork.Json(Reply));
        var client = OpenAICompatBindings.CreateChatClient(Profiles.LocalServer("qwen3.5-27b"),
            new OpenAICompatModelOptions("qwen3.5-27b", new Uri("http://127.0.0.1:8080/v1")), secret: null, vault: null, network);

        var response = await client.GetResponseAsync([new(ChatRole.System, "Be brief."), new(ChatRole.User, "weather in Leeds?")],
            new ModelRequestContext("s1", false).Apply(Tools()), Ct);

        var (request, body) = Assert.Single(network.Requests);
        Assert.Equal(new Uri("http://127.0.0.1:8080/v1/chat/completions"), request.RequestUri);
        Assert.Null(request.Headers.Authorization);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("qwen3.5-27b", (string)json["model"]!);
        Assert.Equal(["system", "user"], json["messages"]!.AsArray().Select(message => (string)message!["role"]!));
        Assert.Equal("get_weather", (string)json["tools"]![0]!["function"]!["name"]!);
        Assert.DoesNotContain("corvid.", body, StringComparison.Ordinal);
        Assert.Equal("Hello from the server", response.Text);
        Assert.Equal(3, response.Usage!.OutputTokenCount);
        Assert.Empty(_vault.Forwarded);
    }

    [Theory]
    [InlineData(OpenAICompatService.OpenAI, "https://api.openai.com/v1/chat/completions")]
    [InlineData(OpenAICompatService.OpenRouter, "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData(OpenAICompatService.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    public async Task Hosted_services_are_reached_only_through_vault_with_a_bearer_placeholder(OpenAICompatService service, string url)
    {
        var network = new FakeNetwork();
        var client = OpenAICompatBindings.CreateChatClient(Profiles.Cloud("hosted", PromptCachingStyle.Automatic),
            new OpenAICompatModelOptions("some-model", OpenAICompatModelOptions.DefaultEndpoint(service)!), new SecretRef("hosted.key"),
            _vault.Access(), network);

        var response = await client.GetResponseAsync([new(ChatRole.User, "hello")], new ModelRequestContext("s1", true).Apply(null), Ct);

        var request = Assert.Single(_vault.Forwarded);
        Assert.Equal(url, new Uri(request.Url).GetLeftPart(UriPartial.Path));
        Assert.Equal("Bearer " + _vault.Minted.Single(), FakeVault.Header(request, "Authorization"));
        Assert.DoesNotContain(request.Headers, header => header.Value.Contains(ModelHttp.CredentialSentinel, StringComparison.Ordinal));
        Assert.True(request.Tainted);
        Assert.Equal(ModelHttp.Toolset, request.Toolset);
        Assert.Empty(network.Requests);
        Assert.Equal("Hello from the server", response.Text);
    }

    [Fact]
    public void A_cloud_model_without_a_vault_secret_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => OpenAICompatBindings.CreateChatClient(Profiles.Cloud("gpt"),
            new OpenAICompatModelOptions("gpt-5", OpenAICompatModelOptions.DefaultEndpoint(OpenAICompatService.OpenAI)!), secret: null,
            _vault.Access()));
        Assert.Throws<VaultUnavailableException>(() => OpenAICompatBindings.CreateChatClient(Profiles.Cloud("gpt"),
            new OpenAICompatModelOptions("gpt-5", OpenAICompatModelOptions.DefaultEndpoint(OpenAICompatService.OpenAI)!), new SecretRef("openai.key"),
            vault: null));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task Server_errors_keep_their_status_for_the_fallback_chain(HttpStatusCode status, bool fallsBack)
    {
        var network = new FakeNetwork((_, _) => FakeNetwork.Json("""{"error":{"message":"nope"}}""", status));
        var client = OpenAICompatBindings.CreateChatClient(Profiles.LocalServer("m"),
            new OpenAICompatModelOptions("m", new Uri("http://localhost:11434/v1")), secret: null, vault: null, network);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Equal(status, failure.StatusCode);
        Assert.Equal(fallsBack, FallbackErrors.IsProviderFailure(failure));
        Assert.Single(network.Requests);
    }

    [Fact]
    public async Task A_server_that_rejects_forced_tool_choice_is_asked_without_it()
    {
        var network = new FakeNetwork((_, _) => FakeNetwork.Json(Reply));
        var client = OpenAICompatBindings.CreateChatClient(Profiles.LocalServer("m"),
            new OpenAICompatModelOptions("m", new Uri("http://localhost:1234/v1"), ForcedToolChoice: false), secret: null, vault: null, network);
        var forced = Tools();
        forced.ToolMode = ChatToolMode.RequireSpecific("get_weather");

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")], forced, Ct));
        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_falls_back()
    {
        var client = OpenAICompatBindings.CreateChatClient(Profiles.LocalServer("m"),
            new OpenAICompatModelOptions("m", new Uri("http://127.0.0.1:9/v1")), secret: null, vault: null,
            new Unreachable());

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Null(failure.StatusCode);
        Assert.True(FallbackErrors.IsProviderFailure(failure));
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(111));
    }
}
