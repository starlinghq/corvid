using System.Collections.Immutable;
using Corvid.Models;

namespace Corvid.Host.Models;

/// <summary>A model a provider can bind, by the name configuration and <c>/model</c> use.</summary>
/// <param name="Slots">The slots it can serve; the first is its usual one.</param>
/// <param name="DownloadBytes">What an explicit download fetches, when the provider downloads.</param>
public sealed record ModelDescriptor(string Name, ImmutableArray<string> Slots, ModelProfile Profile, string Provider, long? DownloadBytes = null)
{
    public bool Serves(string slot) => Slots.Contains(slot, StringComparer.Ordinal);
}

public enum ModelReadiness
{
    Ready,
    Missing,
    Corrupt,
    Unreadable,
    /// <summary>A remote model whose service is not there: Vault is not running, or lacks the model's key (C-015).</summary>
    Unavailable,
}

/// <param name="Detail">What is wrong and how to fix it, in a sentence or two.</param>
/// <param name="Acquire">The command that fetches the model, when it can be fetched.</param>
public sealed record ModelAvailability(ModelDescriptor Model, ModelReadiness Readiness, string Detail, string? Acquire = null)
{
    public bool IsReady => Readiness == ModelReadiness.Ready;
}

/// <summary>What a provider needs to load a model onto a device.</summary>
/// <param name="ExtractorScheduler">
/// The device's <see cref="TaskScheduler"/> for extractors that take one (GLiNER2 does); the composer never wraps an
/// extractor itself.
/// </param>
public sealed record ModelLoadContext(string Slot, TaskScheduler ExtractorScheduler)
{
    /// <summary>
    /// Reports a load that went other than configured but still succeeded, for example a model placed on a GPU that fell
    /// back to the CPU (C-029). The composer shows these at start with the decider's notices.
    /// </summary>
    public Action<string>? Notice { get; init; }
}

/// <summary>
/// A source of slot bindings: the in-process Starling models by default, and remote models (local servers and cloud
/// providers, C-015) from configuration. Checking never loads a model and never uses the network (a remote model's check
/// asks Vault over its local socket); only <see cref="DownloadAsync"/> does.
/// </summary>
public interface IModelProvider
{
    string Name { get; }

    IReadOnlyList<ModelDescriptor> Models { get; }

    /// <param name="thorough">Verify file digests, not only sizes (seconds per gigabyte).</param>
    ModelAvailability Check(string model, bool thorough = false);

    /// <summary>Loads the model for a slot. The binding's service is the raw model; the composer adds scheduling and telemetry.</summary>
    /// <exception cref="ModelUnavailableException">The model is not usable offline.</exception>
    ModelBinding Load(string model, ModelLoadContext context);

    /// <summary>Fetches a model explicitly. Providers that cannot download throw <see cref="NotSupportedException"/>.</summary>
    Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record ModelDownloadProgress(string File, long Bytes, long? TotalBytes);

public sealed class ModelUnavailableException(ModelAvailability availability, Exception? innerException = null)
    : InvalidOperationException(availability.Detail, innerException)
{
    public ModelAvailability Availability { get; } = availability;
}

/// <summary>Every provider's models by name. Names are unique across providers.</summary>
public sealed class ModelCatalogue
{
    private readonly Dictionary<string, (IModelProvider Provider, ModelDescriptor Model)> _models = new(StringComparer.OrdinalIgnoreCase);

    public ModelCatalogue(IEnumerable<IModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var provider in providers)
        foreach (var model in provider.Models)
            if (!_models.TryAdd(model.Name, (provider, model)))
                throw new InvalidOperationException($"Two model providers name a model '{model.Name}'.");
    }

    public IReadOnlyList<ModelDescriptor> Models => [.. _models.Values.Select(entry => entry.Model).OrderBy(model => model.Name, StringComparer.Ordinal)];

    public bool TryFind(string name, out ModelDescriptor model)
    {
        model = null!;
        if (string.IsNullOrWhiteSpace(name) || !_models.TryGetValue(name, out var entry))
            return false;
        model = entry.Model;
        return true;
    }

    public ModelDescriptor Find(string name) =>
        TryFind(name, out var model) ? model : throw new ArgumentException($"No model is named '{name}'. Run `corvid model` to list them.", nameof(name));

    public IReadOnlyList<ModelDescriptor> ForSlot(string slot) => [.. Models.Where(model => model.Serves(slot))];

    public ModelAvailability Check(string name, bool thorough = false) => Provider(name).Check(Find(name).Name, thorough);

    public ModelBinding Load(string name, ModelLoadContext context) => Provider(name).Load(Find(name).Name, context);

    public Task<ModelAvailability> DownloadAsync(string name, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) => Provider(name).DownloadAsync(Find(name).Name, progress, cancellationToken);

    private IModelProvider Provider(string name) =>
        _models.TryGetValue(name, out var entry) ? entry.Provider : throw new ArgumentException($"No model is named '{name}'.", nameof(name));
}
