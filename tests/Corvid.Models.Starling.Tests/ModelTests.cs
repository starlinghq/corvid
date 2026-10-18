using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Tokens;
using Microsoft.Extensions.AI;
using Starlings.Models.Qwen35;

namespace Corvid.Models.Starling.Tests;

/// <summary>
/// Real models on the CPU. Opt in with <c>CORVID_STARLING_MODELS</c> set to a Starling model cache (for example
/// <c>~/.cache/starlings/models</c>); each test skips when the variable is unset or its model is not in that cache.
/// The tests never download.
/// </summary>
public sealed class ModelTests
{
    public const string CacheVariable = "CORVID_STARLING_MODELS";

    [Fact]
    public async Task Qwen_generates_and_its_counter_matches_the_prompt_usage()
    {
        using var binding = Load(StarlingModel.Qwen35, store => StarlingBindings.Qwen35Generator(store.OpenQwen35()));
        var client = binding.Value.GetService<IChatClient>();
        const string question = "What is the capital of France? Answer in one word.";

        var response = await client.GetResponseAsync(question, new ChatOptions { MaxOutputTokens = 16 },
            TestContext.Current.CancellationToken);

        Assert.Contains("Paris", response.Text, StringComparison.OrdinalIgnoreCase);
        // The counter must tokenize exactly as the model does: count the text of the prompt the model builds.
        var counter = client.GetService<ITokenCounter>()!;
        var prompt = client.GetService<Starlings.Models.Qwen35.Extensions.AI.Qwen35ChatClient>()!.Model
            .CreatePrompt([Qwen35Message.User(question)]);
        Assert.NotNull(prompt.Text);
        Assert.Equal(prompt.TokenIds.Length, counter.CountTokens(prompt.Text));
        Assert.Equal(prompt.TokenIds.Length, response.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task Qwen_calls_a_declared_tool_through_the_template()
    {
        using var binding = Load(StarlingModel.Qwen35, store => StarlingBindings.Qwen35Generator(store.OpenQwen35()));
        var client = binding.Value.GetService<IChatClient>();
        var weather = AIFunctionFactory.Create((string city) => $"18 C and sunny in {city}.", "get_weather", "Get the current weather for a city.");

        var response = await client.GetResponseAsync("What is the weather in Paris today?",
            new ChatOptions { Tools = [weather], MaxOutputTokens = 64 }, TestContext.Current.CancellationToken);

        // Starling's README records this greedy call on the 0.8B model; tool choice itself is not qualified upstream.
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("get_weather", call.Name);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    [Fact]
    public async Task Gliner2_extracts_spans_with_offsets_into_the_input()
    {
        using var binding = Load(StarlingModel.Gliner2, store => StarlingBindings.Gliner2Extractor(store.OpenGliner2()));
        const string text = "Apple CEO Tim Cook announced the iPhone 15 in Cupertino.";

        var result = await binding.Value.GetService<IExtractor>().ExtractAsync(
            new ExtractionRequest(text, ["company", "person", "product", "location"], 0.5f), TestContext.Current.CancellationToken);

        Assert.Contains(result.Spans, s => s is { Label: "person", Text: "Tim Cook" });
        Assert.Contains(result.Spans, s => s is { Label: "location", Text: "Cupertino" });
        Assert.All(result.Spans, s => Assert.Equal(s.Text, text[s.Start..s.End]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => binding.Value.GetService<IExtractor>().ExtractAsync(
            new ExtractionRequest(string.Join(' ', Enumerable.Repeat("token", 600)), ["person"]), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(StarlingModel.EmbeddingGemma2, 768)]
    [InlineData(StarlingModel.E5, 384)]
    public async Task Text_embedders_rank_a_related_passage_above_an_unrelated_one(StarlingModel model, int dimensions)
    {
        using var binding = Load(model, store => model == StarlingModel.E5
            ? StarlingBindings.E5Embedder(store.OpenE5())
            : StarlingBindings.EmbeddingGemma2Embedder(store.OpenEmbeddingGemma2()));
        var registry = new ModelSlotRegistry().Bind(ModelSlots.TextEmbedder, binding.Value);
        var embedder = registry.Get(ModelSlots.TextEmbedder).Default.GetService<IEmbeddingGenerator<string, Embedding<float>>>();

        var vectors = await embedder.GenerateAsync(
            ["how do I water a cactus", "Cacti need little water: soak the soil, then let it dry out.", "The train leaves at noon."],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(vectors, v => Assert.Equal(dimensions, v.Vector.Length));
        Assert.True(Cosine(vectors[0], vectors[1]) > Cosine(vectors[0], vectors[2]));
    }

    private static Loaded Load(StarlingModel model, Func<StarlingModelStore, ModelBinding> bind)
    {
        var cache = Environment.GetEnvironmentVariable(CacheVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(cache), $"Set {CacheVariable} to a Starling model cache to run model-backed tests.");
        var store = new StarlingModelStore(cache);
        var status = store.Check(model, Starlings.Acquisition.CacheVerification.Size);
        Assert.SkipWhen(status.State == StarlingModelState.Missing, status.Describe());
        Assert.True(status.IsReady, status.Describe());
        return new Loaded(bind(store));
    }

    private static float Cosine(Embedding<float> a, Embedding<float> b) =>
        System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(a.Vector.Span, b.Vector.Span);

    private sealed record Loaded(ModelBinding Value) : IDisposable
    {
        public void Dispose() => (Value.Service as IDisposable)?.Dispose();
    }
}
