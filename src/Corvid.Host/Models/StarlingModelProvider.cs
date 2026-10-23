using Corvid.Host.Configuration;
using Corvid.Models;
using Corvid.Models.Starling;
using Starlings.Acquisition;
using Starlings.Models.Kev;

namespace Corvid.Host.Models;

/// <summary>
/// The default provider (plan §1): Starling's in-process models, resolved offline from the model cache: Qwen3.5, GLiNER2,
/// EmbeddingGemma 2, e5, and Kev-0.8B and Kev-4B for the decider. Nothing downloads unless <c>corvid model download</c>
/// asks. Every model runs on the CPU unless <c>Corvid:Models:Placement</c> puts it elsewhere (C-029: Kev on CUDA).
/// </summary>
public sealed class StarlingModelProvider : IModelProvider
{
    public const string ProviderName = "starling";

    private readonly IReadOnlyDictionary<string, ModelPlacementSettings> _placement;

    /// <param name="placement">
    /// <c>Corvid:Models:Placement</c>, read leniently: entries the validator refuses (an unknown model, a bad device, CUDA
    /// for a model that cannot run there) are ignored here, so they leave the model on the CPU until the host refuses to start.
    /// </param>
    public StarlingModelProvider(StarlingModelStore store, IReadOnlyDictionary<string, ModelPlacementSettings>? placement = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        _placement = placement ?? new Dictionary<string, ModelPlacementSettings>(StringComparer.OrdinalIgnoreCase);
        // Qwen can also summarize (plan §4.1: the summarizer defaults to the generator's model). A placed model's descriptor
        // carries its placed profile (kev-0.8b@cuda:0), so the composer loads it with that device's scheduler.
        Models = [.. StarlingModels.All.Select(model => new ModelDescriptor(model.Name,
            model.Slot == ModelSlots.Generator ? [ModelSlots.Generator, ModelSlots.Summarizer] : [model.Slot],
            StarlingProfiles.For(model.Model, PlacementOf(model).Device), ProviderName, model.DownloadBytes))];
        Loader = LoadOn;
    }

    public string Name => ProviderName;

    public StarlingModelStore Store { get; }

    public IReadOnlyList<ModelDescriptor> Models { get; }

    /// <summary>Loads a model on a placement (<c>cpu</c> or <c>cuda:N</c>). Tests replace it to exercise the fallback without a model.</summary>
    internal Func<StarlingModel, string, ModelLoadContext, ModelBinding> Loader { get; set; }

    /// <summary>Whether a model can be placed on CUDA (C-029: Kev only; the generator, embedders and GLiNER2 are deferred).</summary>
    public static bool CanPlaceOnCuda(string model) =>
        StarlingModels.TryFind(model, out var info) && info.Model is StarlingModel.Kev08B or StarlingModel.Kev4B;

    /// <summary>A model's configured placement (<c>cpu</c> when it has none or an invalid one) and fallback policy.</summary>
    public (string Device, PlacementFallback Fallback) PlacementOf(StarlingModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!_placement.TryGetValue(model.Name, out var settings) || settings is null
            || !ModelPlacement.TryParse(settings.Device, out var device, out _)
            || ModelPlacement.IsCuda(device) && !CanPlaceOnCuda(model.Name))
            return (ModelPlacement.Cpu, PlacementFallback.Fail);
        return (device, settings.Fallback);
    }

    public ModelAvailability Check(string model, bool thorough = false) =>
        ToAvailability(Store.Check(Info(model).Model, thorough ? CacheVerification.Sha256 : CacheVerification.Size));

    /// <summary>
    /// Loads on the configured placement. When the GPU is not usable (<see cref="ModelPlacementUnavailableException"/>),
    /// <see cref="PlacementFallback.Cpu"/> loads on the CPU instead and reports it through <see cref="ModelLoadContext.Notice"/>;
    /// the binding's CPU profile is what the composer schedules by. <see cref="PlacementFallback.Fail"/> lets it propagate.
    /// </summary>
    public ModelBinding Load(string model, ModelLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var info = Info(model);
        var (device, fallback) = PlacementOf(info);
        try
        {
            try
            {
                return Loader(info.Model, device, context);
            }
            catch (ModelPlacementUnavailableException exception) when (fallback == PlacementFallback.Cpu && device != ModelPlacement.Cpu)
            {
                var binding = Loader(info.Model, ModelPlacement.Cpu, context);
                context.Notice?.Invoke($"{info.Name}: configured {device}, running on cpu: {exception.Message}" +
                    (info.Slot == ModelSlots.Decider ? " CPU checkpoints take over 1 s (spike S1)." : ""));
                return binding;
            }
        }
        catch (StarlingModelUnavailableException exception)
        {
            throw new ModelUnavailableException(ToAvailability(exception.Status), exception);
        }
    }

    private ModelBinding LoadOn(StarlingModel model, string placement, ModelLoadContext context) => model switch
    {
        StarlingModel.Qwen35 => StarlingBindings.Qwen35Generator(Store.OpenQwen35()),
        StarlingModel.Gliner2 => StarlingBindings.Gliner2Extractor(Store.OpenGliner2(), scheduler: context.ExtractorScheduler),
        StarlingModel.EmbeddingGemma2 => StarlingBindings.EmbeddingGemma2Embedder(Store.OpenEmbeddingGemma2()),
        StarlingModel.E5 => StarlingBindings.E5Embedder(Store.OpenE5()),
        // The files are opened (offline, size-checked) before the placement is tried, so missing weights stay a plain
        // unavailable model whatever the device.
        StarlingModel.Kev08B => StarlingBindings.KevDecider(Store.OpenKev(KevModel.Kev08B), KevDecider.OptionsFor(placement)),
        StarlingModel.Kev4B => StarlingBindings.KevDecider(Store.OpenKev(KevModel.Kev4B), KevDecider.OptionsFor(placement)),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    public async Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = Info(model);
        // Synchronous (C-039): a Progress<T> here would hop each report to the thread pool before the caller saw it.
        var relay = progress is null ? null : new SynchronousProgress<ModelAssetProgress>(item =>
            progress.Report(new ModelDownloadProgress(item.Path, item.BytesProcessed, item.TotalBytes)));
        return ToAvailability(await Store.DownloadAsync(info.Model, relay, cancellationToken).ConfigureAwait(false));
    }

    private static StarlingModelInfo Info(string model) =>
        StarlingModels.TryFind(model, out var info) ? info : throw new ArgumentException($"Starling has no model '{model}'.", nameof(model));

    private ModelAvailability ToAvailability(StarlingModelStatus status)
    {
        var descriptor = Models.Single(model => model.Name == status.Model.Name);
        var readiness = status.State switch
        {
            StarlingModelState.Ready => ModelReadiness.Ready,
            StarlingModelState.Missing => ModelReadiness.Missing,
            StarlingModelState.Corrupt => ModelReadiness.Corrupt,
            _ => ModelReadiness.Unreadable,
        };
        return new ModelAvailability(descriptor, readiness, status.Describe(),
            readiness is ModelReadiness.Missing or ModelReadiness.Corrupt ? $"corvid model download {status.Model.Name}" : null);
    }
}
