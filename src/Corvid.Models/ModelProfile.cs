using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corvid.Models;

/// <summary>What a bound model can do and what it costs. Policies read the profile and never assume a model name.</summary>
/// <param name="Id">A stable name for the binding, for example <c>qwen3.5-0.8b@cuda0</c>.</param>
/// <param name="ContextWindow">The model's context window in tokens (for an embedder, its maximum input).</param>
/// <param name="AcceptedContext">How much of the window Corvid lets a prompt use; budget policy plans against this.</param>
/// <param name="Placement">Device or endpoint, for example <c>cuda:0</c>, <c>cpu</c> or <c>remote</c>. Configuration, not identity.</param>
public sealed record ModelProfile(
    string Id,
    int ContextWindow,
    int AcceptedContext,
    ToolCallSupport ToolCalls,
    StructuredOutputSupport StructuredOutput,
    PromptCachingStyle Caching,
    ModelCost Cost,
    PrivacyClass Privacy,
    string Placement)
{
    /// <summary>Throws when the profile cannot be planned against.</summary>
    public ModelProfile Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Placement);
        ArgumentNullException.ThrowIfNull(Cost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ContextWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(AcceptedContext);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AcceptedContext, ContextWindow);
        if (Cost.InputPerMillionTokens < 0 || Cost.OutputPerMillionTokens < 0 || Cost.CachedInputPerMillionTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(Cost), "Costs cannot be negative.");
        if (!Enum.IsDefined(ToolCalls) || !Enum.IsDefined(StructuredOutput) || !Enum.IsDefined(Caching) || !Enum.IsDefined(Privacy))
            throw new ArgumentOutOfRangeException(nameof(Privacy), "Unknown profile capability value.");
        return this;
    }

    /// <summary>The immutable form recorded in <c>ModelSwitched</c>; later registry changes cannot rewrite history.</summary>
    public JsonElement ToSnapshot() => JsonSerializer.SerializeToElement(this, ModelProfileJsonContext.Default.ModelProfile);

    public static ModelProfile FromSnapshot(JsonElement snapshot)
    {
        try
        {
            return snapshot.Deserialize(ModelProfileJsonContext.Default.ModelProfile)
                ?? throw new InvalidDataException("Null model profile snapshot.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Invalid model profile snapshot.", exception);
        }
    }
}

/// <summary>Price in the provider's currency per million tokens. Zero for local models.</summary>
public sealed record ModelCost(decimal InputPerMillionTokens, decimal OutputPerMillionTokens, decimal CachedInputPerMillionTokens = 0)
{
    public static ModelCost Free { get; } = new(0, 0);
}

public enum ToolCallSupport
{
    [JsonStringEnumMemberName("none")] None,
    /// <summary>Tools are rendered into the chat template and parsed back out (small local models).</summary>
    [JsonStringEnumMemberName("template")] TemplateRendered,
    [JsonStringEnumMemberName("native")] Native,
}

public enum StructuredOutputSupport
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("json_object")] JsonObject,
    [JsonStringEnumMemberName("json_schema")] JsonSchema,
    /// <summary>Constrained decoding with an arbitrary grammar (llguidance).</summary>
    [JsonStringEnumMemberName("grammar")] Grammar,
}

public enum PromptCachingStyle
{
    [JsonStringEnumMemberName("none")] None,
    /// <summary>The provider or runtime reuses a matching prefix without hints.</summary>
    [JsonStringEnumMemberName("automatic")] Automatic,
    /// <summary>The request must mark cache breakpoints (Anthropic content blocks).</summary>
    [JsonStringEnumMemberName("breakpoints")] Breakpoints,
}

/// <summary>Where prompt content goes. Moving a slot to <see cref="Cloud"/> passes the privacy gate first (plan §10.4).</summary>
public enum PrivacyClass
{
    [JsonStringEnumMemberName("local")] Local,
    [JsonStringEnumMemberName("local-server")] LocalServer,
    [JsonStringEnumMemberName("cloud")] Cloud,
}

// The snapshot shape is persisted inside ModelSwitched events: renaming a property or enum value needs a migration.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ModelProfile))]
internal sealed partial class ModelProfileJsonContext : JsonSerializerContext;
