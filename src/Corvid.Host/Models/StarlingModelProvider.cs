using Corvid.Models;
using Corvid.Models.Starling;
using Starlings.Acquisition;
using Starlings.Models.Kev;

namespace Corvid.Host.Models;

/// <summary>
/// The default provider (plan §1): Starling's in-process models, CPU only, resolved offline from the model cache: Qwen3.5,
/// GLiNER2, EmbeddingGemma 2, e5, and Kev-0.8B and Kev-4B for the decider. Nothing downloads unless
/// <c>corvid model download</c> asks.
/// </summary>
public sealed class StarlingModelProvider(StarlingModelStore store) : IModelProvider
{
    public const string ProviderName = "starling";

    public string Name => ProviderName;

    public StarlingModelStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    // Qwen can also summarize (plan §4.1: the summarizer defaults to the generator's model).
    public IReadOnlyList<ModelDescriptor> Models { get; } = [.. StarlingModels.All.Select(model => new ModelDescriptor(model.Name,
        model.Slot == ModelSlots.Generator ? [ModelSlots.Generator, ModelSlots.Summarizer] : [model.Slot],
        model.Profile, ProviderName, model.DownloadBytes))];

    public ModelAvailability Check(string model, bool thorough = false) =>
        ToAvailability(Store.Check(Info(model).Model, thorough ? CacheVerification.Sha256 : CacheVerification.Size));

    public ModelBinding Load(string model, ModelLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var info = Info(model);
        try
        {
            return info.Model switch
            {
                StarlingModel.Qwen35 => StarlingBindings.Qwen35Generator(Store.OpenQwen35()),
                StarlingModel.Gliner2 => StarlingBindings.Gliner2Extractor(Store.OpenGliner2(), scheduler: context.ExtractorScheduler),
                StarlingModel.EmbeddingGemma2 => StarlingBindings.EmbeddingGemma2Embedder(Store.OpenEmbeddingGemma2()),
                StarlingModel.E5 => StarlingBindings.E5Embedder(Store.OpenE5()),
                StarlingModel.Kev08B => StarlingBindings.KevDecider(Store.OpenKev(KevModel.Kev08B)),
                StarlingModel.Kev4B => StarlingBindings.KevDecider(Store.OpenKev(KevModel.Kev4B)),
                _ => throw new ArgumentOutOfRangeException(nameof(model)),
            };
        }
        catch (StarlingModelUnavailableException exception)
        {
            throw new ModelUnavailableException(ToAvailability(exception.Status), exception);
        }
    }

    public async Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = Info(model);
        var relay = progress is null ? null : new Progress<ModelAssetProgress>(item =>
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
