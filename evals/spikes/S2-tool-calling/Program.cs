// Spike S2 (C-003, C-034): the tool-calling golden set with Qwen3.5-0.8B and Kev-0.8B on the GPU. See
// docs/spikes/S2-tool-calling.md. Everything but the bindings is `corvid-evals tool-calling` (ToolCallingCli): same
// cases, configurations, turn loop, scoring and report. Usage: run.sh (sets the environment and takes the GPU lock).
using System.Reflection;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Tokens;
using Corvid.Evals.ToolCalling;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Models.Starling;
using Microsoft.Extensions.AI;
using Starlings.Acquisition;
using Starlings.Generation.OnnxGenAI;
using Starlings.Models.EmbeddingGemma2;
using Starlings.Models.EmbeddingGemma2.Extensions.AI;
using Starlings.Models.Gliner2;
using Starlings.Models.Kev;
using Starlings.Models.Qwen35;
using Starlings.Models.Qwen35.Extensions.AI;
using Starlings.Runtime.Onnx;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await ToolCallingCli.RunAsync(args, Console.Out, Console.Error, () => new CudaToolCallingModels(), cancellation.Token);

/// <summary>
/// Qwen3.5-0.8B and Kev-0.8B on CUDA device 0 (run.sh pins it to the 3090 by UUID); EmbeddingGemma 2 and GLiNER2 on the
/// CPU, as in production. Files are located offline with size verification, as <c>StarlingModelStore.Open*</c> do
/// (<c>STARLING_MODELS</c> honoured); nothing is downloaded. Profiles are production's (<see cref="StarlingProfiles"/>),
/// so the TurnState Kev reads names the generator as the product does; the header records the real devices.
/// </summary>
internal sealed class CudaToolCallingModels : IToolCallingModels
{
    private static readonly ModelAcquisitionOptions Offline = new() { Verification = CacheVerification.Size };

    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, string> _described = new(StringComparer.Ordinal);
    private ModelBinding? _generator;
    private ModelBinding? _embedder;
    private IDecider? _kev;

    public ModelBinding Generator()
    {
        if (_generator is not null)
            return _generator;
        var files = Open("qwen3.5-0.8b", () => Qwen35ModelAssets.OpenLocal(Qwen35Variant.Fp32x, Offline));
        // Starling's defaults (Qwen35ChatClientOptions null): greedy, thinking off; prefill in 512-token chunks on CUDA.
        var client = Qwen35ChatClient.Create(files, new Qwen35ModelOptions { Provider = GenAIExecutionProvider.Cuda });
        _owned.Add(client);
        // As StarlingBindings and ModelComposer bind it: the token counter, then the request scope (TokenCountingChatClient is internal).
        _generator = new ModelBinding(StarlingProfiles.Qwen35, new ModelRequestScopeChatClient(new CountingChatClient(client, Qwen35TokenCounter.Create(files))));
        _described["qwen3.5-0.8b"] = $"cuda:0 (GenAI CUDA provider), fp32x, Starlings {Version(typeof(Qwen35ChatClient))}; Qwen35ChatClientOptions defaults: greedy, thinking off";
        return _generator;
    }

    public ModelBinding Embedder()
    {
        if (_embedder is not null)
            return _embedder;
        var generator = EmbeddingGemma2EmbeddingGenerator.Create(Open("embeddinggemma-2", () => EmbeddingGemma2ModelAssets.OpenLocal(Offline)));
        _owned.Add(generator);
        _embedder = new ModelBinding(StarlingProfiles.EmbeddingGemma2, generator);
        _described["embeddinggemma-2"] = $"cpu, Starlings {Version(typeof(EmbeddingGemma2EmbeddingGenerator))}";
        return _embedder;
    }

    public ModelBinding? Extractor()
    {
        Gliner2ModelFiles files;
        try
        {
            files = Gliner2ModelAssets.OpenLocal(Offline);
        }
        catch (Exception exception) when (exception is ModelAssetMissingException or ModelAssetIntegrityException)
        {
            _described["gliner2-base-v1"] = "not present; extractor off";
            return null;
        }
        var extractor = Gliner2EntityExtractor.Create(files);
        _owned.Add(extractor);
        // Gliner2Extractor.Create is the same delegate over the CPU model; the internal constructor skips nothing else.
        var adapter = new Gliner2Extractor((text, labels, threshold, token) => extractor.Extract(text, labels, threshold, token).Entities, null, null);
        _described["gliner2-base-v1"] = $"cpu, Starlings {Version(typeof(Gliner2EntityExtractor))}";
        return new ModelBinding(StarlingProfiles.Gliner2, adapter);
    }

    public IDecider Kev()
    {
        if (_kev is not null)
            return _kev;
        var files = Open("kev-0.8b", () => KevModelAssets.OpenLocal(KevModel.Kev08B, Offline));
        var model = KevDecisionModel.Create(files, new KevOptions { Session = new OnnxSessionOptions { Provider = OnnxExecutionProvider.Cuda, CudaDeviceId = 0 } });
        // KevDecider.Create refuses CUDA (C-029); this is what it does, minus that guard (S1's construction).
        var kev = new KevDecider(KevDecider.NameOf(KevModel.Kev08B), new Runner(model), model);
        _owned.Add(kev);
        _described["kev-0.8b"] = $"cuda:0 ({model.ProfileId}), calibrated temperature, Starlings {Version(typeof(KevDecisionModel))}";
        return _kev = kev;
    }

    public IReadOnlyDictionary<string, string> Describe() => _described;

    private static T Open<T>(string name, Func<T> open)
    {
        try
        {
            return open();
        }
        catch (Exception exception) when (exception is ModelAssetMissingException or ModelAssetIntegrityException)
        {
            throw new ToolCallingModelMissingException(name, exception.Message, exception);
        }
    }

    private static string Version(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public void Dispose()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
            owned.Dispose();
    }

    private sealed class Runner(KevDecisionModel model) : IKevRunner
    {
        public KevEncoding Encode(KevRecord record) => model.Encode(record);

        public IReadOnlyList<float[]> Run(KevEncoding encoding, CancellationToken cancellationToken) => model.Run(encoding, cancellationToken);
    }
}

/// <summary>The generator answers <c>GetService(typeof(ITokenCounter))</c> with Qwen's tokenizer, as StarlingBindings' binding does.</summary>
internal sealed class CountingChatClient(IChatClient inner, ITokenCounter counter) : DelegatingChatClient(inner)
{
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ITokenCounter) ? counter : base.GetService(serviceType, serviceKey);
}
