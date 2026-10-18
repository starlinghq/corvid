using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Memory.Tests;

/// <summary>A memory database and an event log in their own temporary directory, removed with their WAL files on dispose.</summary>
internal sealed class TempStore : IDisposable
{
    public TempStore(ManualTime? time = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-memory-" + Guid.NewGuid().ToString("N"));
        Time = time ?? new ManualTime(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        Database = new MemoryDatabase(Path.Combine(Root, "memory.db"));
        Log = new SqliteEventLog(Path.Combine(Root, "events.db"));
        Facts = new FactGraph(Database, Time);
        Episodes = new EpisodicIndex(Database);
        Blocks = new CoreBlockStore(Database, time: Time);
        Index = new SqliteVectorIndex(Database);
    }

    public string Root { get; }

    public ManualTime Time { get; }

    public MemoryDatabase Database { get; }

    public SqliteEventLog Log { get; }

    public FactGraph Facts { get; }

    public EpisodicIndex Episodes { get; }

    public CoreBlockStore Blocks { get; }

    public SqliteVectorIndex Index { get; }

    public MemoryVectors Vectors(params MemoryEmbedder[] embedders) => new(Database, Index, embedders);

    public HybridRetriever Retriever(MemoryVectors? vectors = null, RetrievalOptions? options = null) =>
        new(Episodes, Facts, vectors, options: options, time: Time);

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}

/// <summary>
/// Embeds by keyword: one axis per keyword the text contains, plus a small constant axis so no vector is zero. Records
/// every batch and can be made to fail.
/// </summary>
internal sealed class KeywordEmbedder(params string[] keywords) : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<IReadOnlyList<string>> Batches { get; } = [];

    public bool Fail { get; set; }

    /// <summary>Runs before each batch, so a test can write an item while a re-embed batch is in flight.</summary>
    public Action? OnGenerate { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        OnGenerate?.Invoke();
        if (Fail)
            throw new HttpRequestException("embedder down");
        var batch = values.ToList();
        Batches.Add(batch);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(batch.Select(text => new Embedding<float>(Embed(text)))));
    }

    /// <summary>Replaces the keyword rule, for tests that need particular similarities.</summary>
    public Func<string, float[]>? Override { get; init; }

    public float[] Embed(string text)
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

/// <summary>Finds each configured (label, word) wherever the word occurs, case-insensitively, in request label order.</summary>
internal sealed class FakeExtractor(params (string Label, string Word)[] vocabulary) : IExtractor
{
    public List<ExtractionRequest> Requests { get; } = [];

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var spans = ImmutableArray.CreateBuilder<ExtractedSpan>();
        foreach (var label in request.Labels)
            foreach (var (_, word) in vocabulary.Where(entry => entry.Label == label))
            {
                var start = request.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                    spans.Add(new ExtractedSpan(label, request.Text.Substring(start, word.Length), start, start + word.Length, 0.9f));
            }
        return Task.FromResult(new ExtractionResult(spans.ToImmutable()));
    }
}

/// <summary>A calibrated decider that answers every question from a function (Kev's place, C-004b).</summary>
internal sealed class FakeCalibratedDecider(Func<string, DecisionQuestion, DecisionAnswer?> answer) : IDecider
{
    public List<DecisionRequest> Requests { get; } = [];

    public string Name => "kev-test";

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, request.Questions
            .Select(pair => (pair.Key, Answer: answer(pair.Key, pair.Value)))
            .Where(pair => pair.Answer is not null)
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Answer!, StringComparer.Ordinal)));
    }

    /// <summary>Answers <c>memory_op</c> with <paramref name="option"/> at probability <paramref name="p"/>, the rest spread evenly.</summary>
    public static FakeCalibratedDecider MemoryOp(string option, double p = 0.95) => new((_, question) =>
        DecisionAnswer.For(question, question.Options.Select(o => o.Name == option ? p : (1 - p) / (question.Options.Length - 1))));
}

internal sealed class NoChatClient : IChatClient
{
    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Prompts.Add(messages.Last().Text);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "no")));
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

internal static class Fixtures
{
    public static readonly DateTimeOffset January = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset March = new(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset May = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset June = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static EntityRef Person(string name) => new("person", name);

    public static EntityRef Place(string name) => new("place", name);

    public static DegradedDecider Degraded(NoChatClient? chat = null) =>
        new(new KeywordEmbedder("add", "update"), chat ?? new NoChatClient());
}
