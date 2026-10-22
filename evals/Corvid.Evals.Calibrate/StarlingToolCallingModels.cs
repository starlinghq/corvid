using System.Reflection;
using Corvid.Abstractions.Fabric;
using Corvid.Evals.ToolCalling;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Models.Starling;
using Microsoft.Extensions.AI;
using Starlings.Acquisition;
using Starlings.Models.Kev;

namespace Corvid.Evals.Calibrate;

/// <summary>
/// The shipped path for <c>corvid-evals tool-calling</c> (C-034): the Starling models on the CPU, located offline as
/// <see cref="StarlingModelStore"/>'s <c>Open*</c> do (size verification, <c>~/.cache/starlings/models</c>, <c>STARLING_MODELS</c>)
/// and bound through <see cref="StarlingBindings"/> as the host binds them. A missing model is an error naming
/// <c>corvid model download</c>; nothing is downloaded. The S2 GPU wrapper (evals/spikes/S2-tool-calling) binds CUDA instead.
/// </summary>
internal sealed class StarlingToolCallingModels(string? cacheDirectory = null) : IToolCallingModels
{
    private readonly StarlingModelStore _store = new(cacheDirectory);
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, string> _described = new(StringComparer.Ordinal);
    private ModelBinding? _generator;
    private ModelBinding? _embedder;
    private IDecider? _kev;

    public ModelBinding Generator()
    {
        if (_generator is not null)
            return _generator;
        var raw = StarlingBindings.Qwen35Generator(Open(StarlingModels.Qwen35, () => _store.OpenQwen35()));
        Own(raw.Service);
        // As ModelComposer binds a local model the egress gate does not wrap: Corvid's request keys are removed first.
        _generator = new ModelBinding(raw.Profile, new ModelRequestScopeChatClient((IChatClient)raw.Service));
        Note(StarlingModels.Qwen35, raw.Profile, "Qwen35ChatClientOptions defaults: greedy, thinking off");
        return _generator;
    }

    public ModelBinding Embedder()
    {
        if (_embedder is not null)
            return _embedder;
        _embedder = StarlingBindings.EmbeddingGemma2Embedder(Open(StarlingModels.EmbeddingGemma2, () => _store.OpenEmbeddingGemma2()));
        Own(_embedder.Service);
        Note(StarlingModels.EmbeddingGemma2, _embedder.Profile);
        return _embedder;
    }

    public ModelBinding? Extractor()
    {
        // Optional in every configuration, as in production's default slots: absent, the turn runs without entities.
        if (!_store.Check(StarlingModel.Gliner2, CacheVerification.Size).IsReady)
        {
            _described[StarlingModels.Gliner2.Name] = "not present; extractor off";
            return null;
        }
        var binding = StarlingBindings.Gliner2Extractor(_store.OpenGliner2());
        Own(binding.Service);
        Note(StarlingModels.Gliner2, binding.Profile);
        return binding;
    }

    public IDecider Kev()
    {
        if (_kev is not null)
            return _kev;
        var binding = StarlingBindings.KevDecider(Open(StarlingModels.Kev08B, () => _store.OpenKev(KevModel.Kev08B)));
        Own(binding.Service);
        Note(StarlingModels.Kev08B, binding.Profile, "calibrated temperature");
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

    private void Note(StarlingModelInfo model, ModelProfile profile, string? detail = null) =>
        _described[model.Name] = $"{profile.Id}, {model.StarlingProfile}, {model.Lock.Repository}@{model.Lock.Revision[..8]}, " +
            $"Starlings {Version(typeof(KevDecisionModel).Assembly)}" + (detail is null ? "" : "; " + detail);

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
