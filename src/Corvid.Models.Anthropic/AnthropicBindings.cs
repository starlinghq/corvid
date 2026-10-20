using System.Net;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Corvid.Models.Remote;
using Corvid.Vault.Client;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Anthropic;

/// <param name="Model">The API model id, for example <c>claude-opus-5-5</c>.</param>
/// <param name="BaseUrl">The Messages API host. Vault must bind the key to this host.</param>
/// <param name="CachedRecentMessages">Messages at the end of the conversation that carry a cache breakpoint (Hermes: 3).</param>
/// <param name="CacheTtl">Breakpoint lifetime; null is the API's default of five minutes.</param>
/// <param name="ForcedToolChoice">
/// Whether the model accepts a required tool choice. Current models reject it with a 400, so by default a required mode
/// is refused with <see cref="NotSupportedException"/> before sending, and the turn loop asks for the tool in words instead.
/// </param>
public sealed record AnthropicModelOptions(
    string Model,
    string BaseUrl = AnthropicModelOptions.DefaultBaseUrl,
    int CachedRecentMessages = 3,
    Ttl? CacheTtl = null,
    bool ForcedToolChoice = false)
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>The API key travels in this header; Vault substitutes the value for the placeholder.</summary>
    public const string KeyHeader = "x-api-key";
}

/// <summary>Builds the chat client for an Anthropic model (plan §4.1, §8, §10.5).</summary>
public static class AnthropicBindings
{
    /// <summary>
    /// The official SDK's <see cref="IChatClient"/> with no retries of its own (Corvid's fallback chain decides), credentials
    /// from Vault only, cache breakpoints, and failures mapped for the fallback chain.
    /// </summary>
    /// <param name="network">For tests only; a cloud model never gets a direct route (see <see cref="ModelHttp"/>).</param>
    public static IChatClient CreateChatClient(ModelProfile profile, AnthropicModelOptions options, SecretRef secret, VaultAccess? vault,
        HttpMessageHandler? network = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CachedRecentMessages);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.CachedRecentMessages, 3);
        var handler = ModelHttp.Create(profile, new SecretHeader(secret, AnthropicModelOptions.KeyHeader), vault, network);
        var client = new VaultKeyedAnthropicClient(new ClientOptions
        {
            // The handler replaces this with a Vault placeholder; it is never a key.
            ApiKey = ModelHttp.CredentialSentinel,
            BaseUrl = options.BaseUrl,
            HttpClient = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan },
            MaxRetries = 0,
        });
        IChatClient chat = client.AsIChatClient(options.Model, defaultMaxOutputTokens: null);
        chat = new AnthropicCacheBreakpoints(chat, options);
        return new RemoteChatClient(chat, Translate);
    }

    /// <summary>The SDK's API errors keep their status, so 429 and 5xx fall back and 4xx does not; transport errors fall back.</summary>
    internal static Exception? Translate(Exception exception) => exception switch
    {
        AnthropicApiException api => new HttpRequestException(api.Message, api, api.StatusCode),
        AnthropicIOException io => new HttpRequestException(io.Message, io),
        AnthropicSseException sse => new HttpRequestException(sse.Message, sse, HttpStatusCode.BadGateway),
        _ => null,
    };

    // The SDK resolves credentials from ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN and profiles on disk when none is given.
    // Corvid never reads secrets from the environment (plan §10.5), so that resolution is switched off outright.
    private sealed class VaultKeyedAnthropicClient(ClientOptions options) : AnthropicClient(options)
    {
        protected override bool ShouldAutoResolveCredentials => false;
    }
}

/// <summary>
/// Prompt caching in Hermes' layout (plan §8): the system prompt, which Corvid keeps frozen for a session, ends in a
/// breakpoint (covering the tool definitions before it), and so does the last content of each of the last few messages,
/// so the next call reads the conversation so far from the cache. Four breakpoints at most, as the API allows.
/// </summary>
internal sealed class AnthropicCacheBreakpoints(IChatClient inner, AnthropicModelOptions options) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Mark(messages), Check(chatOptions), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(Mark(messages), Check(chatOptions), cancellationToken);

    private ChatOptions? Check(ChatOptions? chatOptions) =>
        chatOptions?.ToolMode is RequiredChatToolMode && !options.ForcedToolChoice
            ? throw new NotSupportedException($"'{options.Model}' does not accept a required tool choice.")
            : chatOptions;

    internal List<ChatMessage> Mark(IEnumerable<ChatMessage> messages)
    {
        // Copies, so the caller's messages are never annotated.
        var list = messages.Select(message => new ChatMessage(message.Role, [.. message.Contents])
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties,
            RawRepresentation = message.RawRepresentation,
        }).ToList();
        var system = list.FindLastIndex(message => message.Role == ChatRole.System && message.Contents.Count > 0);
        var marked = new List<int>();
        if (system >= 0)
            marked.Add(system);
        for (var i = list.Count - 1; i >= 0 && marked.Count < 1 + options.CachedRecentMessages; i--)
            if (list[i].Role != ChatRole.System && list[i].Contents.Count > 0 && list[i].Contents[^1] is TextContent or FunctionCallContent or FunctionResultContent)
                marked.Add(i);
        foreach (var index in marked)
        {
            var contents = list[index].Contents;
            contents[^1] = Copy(contents[^1]).WithCacheControl(options.CacheTtl);
        }
        return list;
    }

    private static AIContent Copy(AIContent content)
    {
        AIContent copy = content switch
        {
            TextContent text => new TextContent(text.Text),
            FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name, call.Arguments),
            FunctionResultContent result => new FunctionResultContent(result.CallId, result.Result),
            _ => content,
        };
        if (!ReferenceEquals(copy, content) && content.AdditionalProperties is { Count: > 0 } properties)
            copy.AdditionalProperties = new AdditionalPropertiesDictionary(properties);
        return copy;
    }
}
