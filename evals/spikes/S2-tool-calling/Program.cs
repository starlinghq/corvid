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
using Starlings.Models.Kev;
using Starlings.Models.Qwen35;
using Starlings.Models.Qwen35.Extensions.AI;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await ToolCallingCli.RunAsync(args, Console.Out, Console.Error, () => new CudaToolCallingModels(), cancellation.Token);

/// <summary>
/// Qwen3.5-0.8B and Kev-0.8B on CUDA device 0 (run.sh pins it to the 3090 by UUID); EmbeddingGemma 2 and GLiNER2 on the
/// CPU, as in production. Files are opened offline through <see cref="StarlingModelStore"/> (size verification,
/// <c>STARLING_MODELS</c> honoured); nothing is downloaded. Kev, the embedder and GLiNER2 are bound through
/// <see cref="StarlingBindings"/> exactly as the host binds them (Kev with C-029's CUDA options, so its profile is
/// <c>kev-0.8b@cuda:0</c>). Qwen on CUDA is spike-only: production places only Kev on a GPU, so the generator is built
/// here as <see cref="StarlingBindings.Qwen35Generator"/> builds it, with the CUDA provider instead of the CPU one.
/// </summary>
internal sealed class CudaToolCallingModels : IToolCallingModels
{
    private readonly StarlingModelStore _store = new();
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, string> _described = new(StringComparer.Ordinal);
    private ModelBinding? _generator;
    private ModelBinding? _embedder;
    private IDecider? _kev;

    public ModelBinding Generator()
    {
        if (_generator is not null)
            return _generator;
        var files = Open(StarlingModels.Qwen35, () => _store.OpenQwen35());
        // Qwen35ChatClientOptions null: greedy, thinking off (Starlings' defaults; the turn loop sets no sampling option);
        // prefill in 512-token chunks on CUDA.
        var client = Qwen35ChatClient.Create(files, new Qwen35ModelOptions { Provider = GenAIExecutionProvider.Cuda });
        _owned.Add(client);
        // As StarlingBindings and ModelComposer bind it: the token counter, then the request scope.
        _generator = new ModelBinding(StarlingProfiles.Qwen35,
            new ModelRequestScopeChatClient(new CountingChatClient(client, Qwen35TokenCounter.Create(files))));
        Note(StarlingModels.Qwen35, "cuda:0 (GenAI CUDA provider, spike-only); Qwen35ChatClientOptions defaults: greedy, thinking off");
        return _generator;
    }

    public ModelBinding Embedder()
    {
        if (_embedder is not null)
            return _embedder;
        _embedder = StarlingBindings.EmbeddingGemma2Embedder(Open(StarlingModels.EmbeddingGemma2, () => _store.OpenEmbeddingGemma2()));
        Own(_embedder.Service);
        Note(StarlingModels.EmbeddingGemma2, $"{_embedder.Profile.Id}, cpu");
        return _embedder;
    }

    public ModelBinding? Extractor()
    {
        if (!_store.Check(StarlingModel.Gliner2, CacheVerification.Size).IsReady)
        {
            _described[StarlingModels.Gliner2.Name] = "not present; extractor off";
            return null;
        }
        var binding = StarlingBindings.Gliner2Extractor(_store.OpenGliner2());
        Own(binding.Service);
        Note(StarlingModels.Gliner2, $"{binding.Profile.Id}, cpu");
        return binding;
    }

    public IDecider Kev()
    {
        if (_kev is not null)
            return _kev;
        // C-029's production construction: CUDA is checked before the weights are read, and nothing falls back.
        var binding = StarlingBindings.KevDecider(Open(StarlingModels.Kev08B, () => _store.OpenKev(KevModel.Kev08B)),
            KevDecider.OptionsFor(ModelPlacement.Cuda(0)));
        Own(binding.Service);
        Note(StarlingModels.Kev08B, $"{binding.Profile.Id} (KevDecider.Create, C-029); calibrated temperature");
        return _kev = (IDecider)binding.Service;
    }

    public IReadOnlyDictionary<string, string> Describe() => _described;

    private static T Open<T>(StarlingModelInfo model, Func<T> open)
    {
        try
        {
            return open();
        }
        catch (StarlingModelUnavailableException exception)
        {
            throw new ToolCallingModelMissingException(model.Name, $"{exception.Status.State.ToString().ToLowerInvariant()} in {exception.Status.SnapshotDirectory}", exception);
        }
    }

    private void Note(StarlingModelInfo model, string detail) =>
        _described[model.Name] = $"{detail}, {model.StarlingProfile}, {model.Lock.Repository}@{model.Lock.Revision[..8]}, " +
            $"Starlings {Version(typeof(KevDecisionModel).Assembly)}";

    private static string Version(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private void Own(object service)
    {
        if (service is IDisposable disposable)
            _owned.Add(disposable);
    }

    public void Dispose()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
            owned.Dispose();
        _owned.Clear();
    }
}

/// <summary>The generator answers <c>GetService(typeof(ITokenCounter))</c> with Qwen's tokenizer, as StarlingBindings' binding does.</summary>
internal sealed class CountingChatClient(IChatClient inner, ITokenCounter counter) : DelegatingChatClient(inner)
{
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ITokenCounter) ? counter : base.GetService(serviceType, serviceKey);
}
