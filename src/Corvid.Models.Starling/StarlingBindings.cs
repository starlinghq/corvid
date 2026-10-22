using Corvid.Abstractions.Tokens;
using Microsoft.Extensions.AI;
using Starlings.Generation.OnnxGenAI;
using Starlings.Models.E5;
using Starlings.Models.E5.Extensions.AI;
using Starlings.Models.EmbeddingGemma2;
using Starlings.Models.EmbeddingGemma2.Extensions.AI;
using Starlings.Models.Gliner2;
using Starlings.Models.Kev;
using Starlings.Models.Qwen35;
using Starlings.Models.Qwen35.Extensions.AI;
using Starlings.Runtime.Onnx;

namespace Corvid.Models.Starling;

/// <summary>
/// Loads Starling models from resolved files and pairs each service with its profile, ready for
/// <see cref="ModelSlotRegistry.Bind"/>. Loading is synchronous and reads the model from disk; resolve the files
/// first with <see cref="StarlingModelStore"/>. Every service runs on the CPU, except Kev, which runs on a GPU when its
/// options ask for CUDA (C-029); the embedders and GLiNER2 still refuse CUDA options.
/// </summary>
/// <remarks>The caller owns the bound services: dispose <see cref="ModelBinding.Service"/> when the binding is retired.</remarks>
public static class StarlingBindings
{
    /// <summary>
    /// Qwen3.5-0.8B as an <see cref="IChatClient"/> for the generator or summarizer slot. Tool calls are template-rendered
    /// and experimental in Starling 0.3.0; <c>ChatToolMode.RequireAny</c>, <c>RequireSpecific</c>, JSON response formats
    /// and unseeded sampling are rejected with <see cref="NotSupportedException"/>. The client answers
    /// <c>GetService(typeof(ITokenCounter))</c> with a <see cref="Qwen35TokenCounter"/>.
    /// </summary>
    public static ModelBinding Qwen35Generator(Qwen35ModelFiles files, Qwen35ChatClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var counter = Qwen35TokenCounter.Create(files);
        var client = Qwen35ChatClient.Create(files, new Qwen35ModelOptions { Provider = GenAIExecutionProvider.Cpu }, options);
        return new ModelBinding(StarlingProfiles.Qwen35, new TokenCountingChatClient(client, counter));
    }

    /// <summary>GLiNER2 as the extractor slot's <see cref="Corvid.Abstractions.Extraction.IExtractor"/>.</summary>
    public static ModelBinding Gliner2Extractor(Gliner2ModelFiles files, Gliner2ExtractorOptions? options = null,
        TaskScheduler? scheduler = null) =>
        new(StarlingProfiles.Gliner2, Starling.Gliner2Extractor.Create(files, options, scheduler));

    /// <summary>EmbeddingGemma 2 text embeddings, the default <c>embedder.text</c>. Runs on the calling thread.</summary>
    public static ModelBinding EmbeddingGemma2Embedder(EmbeddingGemma2ModelFiles files, EmbeddingGemma2EmbeddingGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        RequireCpu(options?.Encoder?.Session);
        return new ModelBinding(StarlingProfiles.EmbeddingGemma2, EmbeddingGemma2EmbeddingGenerator.Create(files, options));
    }

    /// <summary>
    /// Kev-0.8B or Kev-4B as the <c>decider</c> slot's <see cref="Corvid.Abstractions.Fabric.IDecider"/>, with the head's
    /// calibrated temperature. Runs on the calling thread; schedule it on the device (the host does, at fabric priority).
    /// The profile follows where the decider actually runs (<see cref="Starling.KevDecider.Placement"/>): <c>kev-0.8b@cpu</c>
    /// or, with CUDA options, <c>kev-0.8b@cuda:0</c>, so the host schedules it on that device.
    /// </summary>
    /// <exception cref="ModelPlacementUnavailableException">CUDA options in a CPU build, or CUDA cannot start.</exception>
    public static ModelBinding KevDecider(KevModelFiles files, KevOptions? options = null, int rowBudget = Starling.KevDecider.DefaultRowBudget)
    {
        ArgumentNullException.ThrowIfNull(files);
        var decider = Starling.KevDecider.Create(files, options, rowBudget);
        var model = files.Profile.Model == KevModel.Kev4B ? StarlingModel.Kev4B : StarlingModel.Kev08B;
        return new ModelBinding(StarlingProfiles.For(model, decider.Placement), decider);
    }

    /// <summary>multilingual-e5-small text embeddings (384 dimensions). Runs on the calling thread.</summary>
    public static ModelBinding E5Embedder(E5ModelFiles files, E5EmbeddingGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        RequireCpu(options?.Encoder?.Session);
        return new ModelBinding(StarlingProfiles.E5, E5EmbeddingGenerator.Create(files, options));
    }

    private static void RequireCpu(OnnxSessionOptions? session)
    {
        if (session is { Provider: not OnnxExecutionProvider.Cpu })
            throw new NotSupportedException("CUDA placement is built for Kev only (C-029); GLiNER2 and the embedders run on the CPU.");
    }
}

/// <summary>Adds the generator's token counter to the services a chat client offers; everything else passes through.</summary>
internal sealed class TokenCountingChatClient(IChatClient inner, ITokenCounter counter) : DelegatingChatClient(inner)
{
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ITokenCounter) ? counter : base.GetService(serviceType, serviceKey);
}
