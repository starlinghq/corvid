using Microsoft.Extensions.AI;

namespace Corvid.Models.Privacy;

/// <summary>What a model request carries besides its messages: the session it belongs to and whether it is tainted.</summary>
public readonly record struct ModelRequestContext(string? SessionId, bool Tainted)
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
        var clone = options.Clone();
        var kept = new AdditionalPropertiesDictionary();
        foreach (var (key, item) in properties)
            if (!key.StartsWith(ModelRequestKeys.Prefix, StringComparison.Ordinal))
                kept[key] = item;
        clone.AdditionalProperties = kept.Count == 0 ? null : kept;
        return (clone, new ModelRequestContext(string.IsNullOrWhiteSpace(session) ? null : session, tainted));
    }

    /// <summary>Adds the context to <paramref name="options"/> (a clone) for the next client in the pipeline.</summary>
    public ChatOptions Apply(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        clone.AdditionalProperties = clone.AdditionalProperties is null ? [] : new AdditionalPropertiesDictionary(clone.AdditionalProperties);
        if (SessionId is not null)
            clone.AdditionalProperties[ModelRequestKeys.SessionId] = SessionId;
        clone.AdditionalProperties[ModelRequestKeys.Tainted] = Tainted;
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
