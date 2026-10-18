using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Corvid.Abstractions.Fabric;
using Corvid.Kernel.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Fabric.Tests;

/// <summary>An event database in its own temporary directory, removed with its WAL files on dispose.</summary>
internal sealed class TempLog : IDisposable
{
    public TempLog()
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-fabric-" + Guid.NewGuid().ToString("N"));
        Log = new SqliteEventLog(Path.Combine(Root, "events.db"));
    }

    public string Root { get; }

    public SqliteEventLog Log { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

/// <summary>Replies through a script keyed on the prompt, or fails. Records every prompt.</summary>
internal sealed class ScriptedChatClient(Func<string, string> reply) : IChatClient
{
    public List<string> Prompts { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public static ScriptedChatClient Always(string text) => new(_ => text);

    public static ScriptedChatClient Throwing() => new(_ => throw new HttpRequestException("generator down"));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = messages.Last().Text;
        Prompts.Add(prompt);
        Options.Add(options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(prompt))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Embeds by keyword: each text becomes a vector with one axis per keyword it contains, plus a small constant axis so no
/// vector is zero. Similarities are therefore exact and easy to reason about in tests.
/// </summary>
internal sealed class KeywordEmbedder(params string[] keywords) : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<IReadOnlyList<string>> Batches { get; } = [];

    public Func<string, float[]>? Override { get; init; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var batch = values.ToList();
        Batches.Add(batch);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(batch.Select(text => new Embedding<float>(Embed(text)))));
    }

    private float[] Embed(string text)
    {
        if (Override is { } custom)
            return custom(text);
        var vector = new float[keywords.Length + 1];
        for (var i = 0; i < keywords.Length; i++)
            vector[i] = text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        vector[^1] = 0.1f;
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A calibrated decider that answers from a script, or declines with a fixed reason.</summary>
internal sealed class FakeCalibratedDecider(string name, Func<DecisionRequest, DeciderResult> decide) : IDecider
{
    public List<DecisionRequest> Requests { get; } = [];

    public string Name { get; } = name;

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult(decide(request));
    }

    public static FakeCalibratedDecider Answering(string name, Func<string, DecisionQuestion, DecisionAnswer?> answer) =>
        new(name, request => new DeciderAnswers(name, request.Questions
            .Select(pair => (pair.Key, Answer: answer(pair.Key, pair.Value)))
            .Where(pair => pair.Answer is not null)
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Answer!, StringComparer.Ordinal)));

    public static FakeCalibratedDecider Declining(string name, DeciderUnavailableReason reason) =>
        new(name, _ => new DeciderUnavailable(name, reason, "declined in test"));
}

internal static class Fixtures
{
    public static QuestionCatalogue Catalogue => QuestionCatalogue.Default;

    public static TurnStateInput Input(string message = "can you move tomorrow's dentist reminder to friday and tell sam", int step = 0) =>
        new(message, "telegram", "qwen3.5-0.8b local, 8k", Step: step,
            Recent: ["user asked about dentist reminder (cron job 14)", "assistant listed 3 reminders"],
            Entities: ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                KeyValuePair.Create("date", ImmutableArray.Create("tomorrow", "friday")),
                KeyValuePair.Create("person", ImmutableArray.Create("sam")),
            ]));

    public static TurnState State(string message = "can you move tomorrow's dentist reminder to friday and tell sam", int step = 0) =>
        new TurnStateBuilder().Build(Input(message, step));

    public static DecisionQuestion Question(string id, params QuestionOption[] options) =>
        Catalogue[id].ToQuestion(options.Length == 0 ? null : options);

    public static KeyValuePair<string, DecisionQuestion> Ask(string key, params QuestionOption[] options) =>
        KeyValuePair.Create(key, Catalogue.Resolve(key).ToQuestion(options.Length == 0 ? null : options));

    public static QuestionOption[] Tools => [new("cron.update", "change a scheduled reminder"), new("message.send", "send a message to a person"),
        new("none", "no tool is needed")];

    public static IReadOnlySet<string> Conditions(params string[] conditions) => conditions.ToHashSet(StringComparer.Ordinal);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
