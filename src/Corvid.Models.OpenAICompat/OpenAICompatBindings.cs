using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Corvid.Models.Remote;
using Corvid.Vault.Client;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Corvid.Models.OpenAICompat;

/// <summary>The services this adapter speaks to. All use the Chat Completions API.</summary>
public enum OpenAICompatService
{
    /// <summary>llama.cpp's server, Ollama, vLLM, LM Studio or any other server on the user's machine or network.</summary>
    LocalServer,
    OpenAI,
    OpenRouter,
    /// <summary>Gemini through Google's OpenAI-compatible endpoint (a bearer API key).</summary>
    Gemini,
    /// <summary>Any other hosted OpenAI-compatible API; it is a cloud model and needs a Vault secret.</summary>
    OtherCloud,
}

/// <param name="Model">The model id the server knows, for example <c>gpt-5</c> or <c>qwen3.5-27b</c>.</param>
/// <param name="Endpoint">The API base, ending in <c>/v1</c> or the service's equivalent. Vault must bind the key to its host.</param>
/// <param name="ForcedToolChoice">Whether the server honours a required tool choice; when not, a required mode is refused before sending.</param>
public sealed record OpenAICompatModelOptions(string Model, Uri Endpoint, bool ForcedToolChoice = true)
{
    /// <summary>The usual endpoint of a hosted service; a local server has none.</summary>
    public static Uri? DefaultEndpoint(OpenAICompatService service) => service switch
    {
        OpenAICompatService.OpenAI => new Uri("https://api.openai.com/v1"),
        OpenAICompatService.OpenRouter => new Uri("https://openrouter.ai/api/v1"),
        OpenAICompatService.Gemini => new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"),
        _ => null,
    };
}

/// <summary>Builds the chat client for an OpenAI-compatible model (plan §4.1, §10.5).</summary>
public static class OpenAICompatBindings
{
    /// <summary>The key travels as a bearer token; Vault substitutes the value for the placeholder.</summary>
    public const string KeyHeader = "Authorization";

    /// <summary>
    /// The OpenAI SDK's chat client through Microsoft.Extensions.AI, with no retries of its own (Corvid's fallback chain
    /// decides). With a secret, every request goes through Vault; without one (a local server that needs no key) requests
    /// go straight to the endpoint, and a cloud model without one is refused (<see cref="ModelHttp"/>).
    /// </summary>
    /// <param name="network">The direct route for a local server, or a test's fake.</param>
    public static IChatClient CreateChatClient(ModelProfile profile, OpenAICompatModelOptions options, SecretRef? secret, VaultAccess? vault,
        HttpMessageHandler? network = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        var handler = ModelHttp.Create(profile, secret is { } name ? new SecretHeader(name, KeyHeader, "Bearer") : null, vault, network);
        var client = new OpenAIClient(
            // The SDK needs a credential; this one is replaced by a Vault placeholder, or removed for a keyless local server.
            new ApiKeyCredential(ModelHttp.CredentialSentinel),
            new OpenAIClientOptions
            {
                Endpoint = options.Endpoint,
                Transport = new HttpClientPipelineTransport(new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan }),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = TimeSpan.FromMinutes(10),
            });
        IChatClient chat = client.GetChatClient(options.Model).AsIChatClient();
        if (!options.ForcedToolChoice)
            chat = new NoForcedToolChoice(chat, options.Model);
        return new RemoteChatClient(chat, Translate);
    }

    /// <summary>API errors keep their status, so 429 and 5xx fall back and 4xx does not; a status of 0 is a transport failure.</summary>
    internal static Exception? Translate(Exception exception) => exception switch
    {
        ClientResultException { Status: > 0 } result => new HttpRequestException(result.Message, result, (HttpStatusCode)result.Status),
        ClientResultException transport => new HttpRequestException(transport.Message, transport),
        _ => null,
    };

    private sealed class NoForcedToolChoice(IChatClient inner, string model) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, Check(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, Check(options), cancellationToken);

        private ChatOptions? Check(ChatOptions? options) => options?.ToolMode is RequiredChatToolMode
            ? throw new NotSupportedException($"'{model}' does not accept a required tool choice.")
            : options;
    }
}
