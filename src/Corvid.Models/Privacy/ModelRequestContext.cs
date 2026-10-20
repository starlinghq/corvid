using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Privacy;

/// <summary>What a model request carries besides its messages: the session it belongs to and whether it is tainted.</summary>
/// <param name="LocalOnly">
/// The messages were rendered for a local model and hold local-only memory (core blocks or facts, plan §10.4). A cloud
/// model must never receive them, even when a fallback hands the same request on: the gate refuses it.
/// </param>
public readonly record struct ModelRequestContext(string? SessionId, bool Tainted, bool LocalOnly = false)
{
    private static readonly AsyncLocal<ModelRequestContext?> Ambient = new();

    /// <summary>
    /// The context of the request being sent on this flow, for HTTP handlers below a provider SDK that cannot see the chat
    /// options (the Vault handler's taint flag). Null outside a request.
    /// </summary>
    public static ModelRequestContext? Current => Ambient.Value;

    /// <summary>Reads Corvid's keys from <paramref name="options"/> and returns options without them.</summary>
    /// <remarks>A clone is made only when there is something to strip; the caller's options are never changed.</remarks>
    public static (ChatOptions? Options, ModelRequestContext Context) Strip(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { Count: > 0 } properties
            || !properties.Keys.Any(key => key.StartsWith(ModelRequestKeys.Prefix, StringComparison.Ordinal)))
            return (options, default);

        var session = properties.TryGetValue(ModelRequestKeys.SessionId, out var value) ? value as string : null;
        var tainted = properties.TryGetValue(ModelRequestKeys.Tainted, out var flag) && flag is true;
        var localOnly = properties.TryGetValue(ModelRequestKeys.LocalOnly, out var local) && local is true;
        var clone = options.Clone();
        var kept = new AdditionalPropertiesDictionary();
        foreach (var (key, item) in properties)
            if (!key.StartsWith(ModelRequestKeys.Prefix, StringComparison.Ordinal))
                kept[key] = item;
        clone.AdditionalProperties = kept.Count == 0 ? null : kept;
        return (clone, new ModelRequestContext(string.IsNullOrWhiteSpace(session) ? null : session, tainted, localOnly));
    }

    /// <summary>Adds the context to <paramref name="options"/> (a clone) for the next client in the pipeline.</summary>
    public ChatOptions Apply(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        clone.AdditionalProperties = clone.AdditionalProperties is null ? [] : new AdditionalPropertiesDictionary(clone.AdditionalProperties);
        if (SessionId is not null)
            clone.AdditionalProperties[ModelRequestKeys.SessionId] = SessionId;
        clone.AdditionalProperties[ModelRequestKeys.Tainted] = Tainted;
        if (LocalOnly)
            clone.AdditionalProperties[ModelRequestKeys.LocalOnly] = true;
        return clone;
    }

    /// <summary>Makes this the ambient context until the returned scope is disposed.</summary>
    public IDisposable Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Scope(previous);
    }

    private sealed class Scope(ModelRequestContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// For a binding the egress gate does not wrap: removes Corvid's request keys before the model's client sees the options,
/// and makes the context ambient for anything beneath it.
/// </summary>
public sealed class ModelRequestScopeChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (stripped, context) = ModelRequestContext.Strip(options);
        using var scope = context.Enter();
        return await base.GetResponseAsync(messages, stripped, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (stripped, context) = ModelRequestContext.Strip(options);
        var updates = base.GetStreamingResponseAsync(messages, stripped, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool more;
                using (context.Enter())
                    more = await updates.MoveNextAsync().ConfigureAwait(false);
                if (!more)
                    yield break;
                yield return updates.Current;
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
    }
}
