using System.Text.Json;
using System.Text.Json.Serialization;

// The OpenAI Chat Completions wire shapes Corvid serves (snake_case). Only what Corvid reads or writes is modelled; unknown
// request members (temperature, max_tokens and the rest) are ignored, because Corvid's slots and turn options decide them.
namespace Corvid.Http;

internal sealed record ChatCompletionRequest
{
    public string? Model { get; init; }

    public JsonElement[]? Messages { get; init; }

    public bool? Stream { get; init; }

    public int? N { get; init; }

    public JsonElement? Tools { get; init; }

    public JsonElement? ToolChoice { get; init; }

    public JsonElement? Functions { get; init; }

    public JsonElement? FunctionCall { get; init; }
}

internal sealed record ChatMessage(string Role, string Content);

internal sealed record Choice(int Index, ChatMessage Message, string FinishReason);

internal sealed record ChatCompletion(string Id, string Object, long Created, string Model, Choice[] Choices);

internal sealed record Delta
{
    public string? Role { get; init; }

    public string? Content { get; init; }
}

internal sealed record ChunkChoice(int Index, Delta Delta)
{
    // Present and null until the last chunk, as the API sends it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FinishReason { get; init; }
}

internal sealed record ChatCompletionChunk(string Id, string Object, long Created, string Model, ChunkChoice[] Choices);

internal sealed record ModelEntry(string Id, string Object, long Created, string OwnedBy);

internal sealed record ModelList(string Object, ModelEntry[] Data);

internal sealed record ErrorBody(string Message, string Type)
{
    public string? Code { get; init; }

    public string? Param { get; init; }
}

internal sealed record ErrorEnvelope(ErrorBody Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletion))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ModelList))]
[JsonSerializable(typeof(ErrorEnvelope))]
internal sealed partial class OpenAIJson : JsonSerializerContext;
