using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Tokens;
using Corvid.Fabric;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Scheduling;
using Corvid.Memory;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Composition;

/// <summary>
/// Builds the slot registry from configuration (plan §4.1): loads each configured model once through the catalogue, then
/// wraps it per slot. A chat or embedding binding starts its calls on its device at interactive priority and carries a
/// span and latency layer (the generator's also streams to a watching surface). The degraded decider gets its own
/// wrappings of the generator and embedder at fabric priority, and memory its embedders at background priority, so one
/// model serves all three in the device's priority order. The decider slot (Kev) runs at fabric priority and is optional:
/// a decider model that is not usable offline leaves the slot unbound, so the fabric runs on the degraded tier, and the
/// reason is kept in <see cref="DeciderNotices"/>.
/// </summary>
public sealed class ModelComposer : IDisposable
{
    private readonly ModelCatalogue _catalogue;
    private readonly IReadOnlyDictionary<string, SlotSettings> _slots;
    private readonly DeviceSchedulers _devices;
    private readonly ModelMetrics _metrics;
    private readonly TurnFeed _feed;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ModelBinding> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Slot, string Model), ModelBinding> _bound = [];
    private readonly List<string> _deciderNotices = [];

    public ModelComposer(ModelCatalogue catalogue, CorvidOptions options, DeviceSchedulers devices, ModelMetrics metrics, TurnFeed feed)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(options);
        _catalogue = catalogue;
        _slots = options.Models.EffectiveSlots();
        _devices = devices;
        _metrics = metrics;
        _feed = feed;
    }

    /// <summary>Every model the configuration names, by slot: default first, then escalation, then fallbacks.</summary>
    public IReadOnlyList<(string Slot, string Model)> ConfiguredModels =>
        [.. _slots.SelectMany(pair => new[] { pair.Value.Model! }.Concat(pair.Value.Escalation).Concat(pair.Value.Fallbacks)
            .Select(model => (pair.Key, _catalogue.Find(model).Name)))];

    /// <summary>
    /// Configured models that are not usable offline, checked without loading anything. The decider's are left out: without
    /// them the fabric runs on the degraded tier (<see cref="DeciderNotices"/>), so they never stop a start.
    /// </summary>
    public IReadOnlyList<ModelAvailability> CheckConfigured() =>
        [.. ConfiguredModels.Where(pair => pair.Slot != ModelSlots.Decider).Select(pair => pair.Model).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(model => _catalogue.Check(model)).Where(availability => !availability.IsReady)];

    /// <summary>Why the decider runs on the degraded tier or without its second tier, after <see cref="Compose"/>; empty when Kev is bound as configured.</summary>
    public IReadOnlyList<string> DeciderNotices
    {
        get
        {
            lock (_gate)
                return [.. _deciderNotices];
        }
    }

    /// <summary>Loads every configured model and binds the registry. The decider slot is bound only with what is usable offline.</summary>
    /// <exception cref="ModelUnavailableException">A model of another slot is not usable offline.</exception>
    public ModelSlotRegistry Compose()
    {
        var registry = new ModelSlotRegistry();
        foreach (var (slot, settings) in _slots)
        {
            if (slot == ModelSlots.Decider)
            {
                BindDecider(registry, settings);
                continue;
            }
            registry.Bind(slot, Bind(slot, settings.Model!), settings.Escalation.Select(model => Bind(slot, model)),
                settings.Fallbacks.Select(model => Bind(slot, model)));
        }
        return registry;
    }

    // Kev is optional (C-004b): a missing default leaves the fabric on the degraded tier, a missing second tier leaves the
    // gates on the first. Either is reported, never fatal, so `corvid chat` starts before anything is downloaded.
    private void BindDecider(ModelSlotRegistry registry, SlotSettings settings)
    {
        if (TryBindDecider(settings.Model!, "the fabric runs on the degraded tier: gates ask the user and memory writes are staged") is not { } primary)
            return;
        var escalation = settings.Escalation
            .Select(model => TryBindDecider(model, $"gates below their floor go to the user without a second opinion from {model}"))
            .OfType<ModelBinding>().ToArray();
        registry.Bind(ModelSlots.Decider, primary, escalation);
    }

    private ModelBinding? TryBindDecider(string model, string consequence)
    {
        var availability = _catalogue.Check(model);
        if (!availability.IsReady)
        {
            Notice($"{availability.Model.Name} is {availability.Readiness.ToString().ToLowerInvariant()}, so {consequence}." +
                (availability.Acquire is { } acquire ? $" Run `{acquire}`." : ""));
            return null;
        }
        try
        {
            return Bind(ModelSlots.Decider, model);
        }
        catch (Exception exception) when (exception is ModelUnavailableException or InvalidOperationException or IOException
            or NotSupportedException or ArgumentException)
        {
            Notice($"{availability.Model.Name} did not load ({exception.Message}), so {consequence}.");
            return null;
        }
    }

    private void Notice(string text)
    {
        lock (_gate)
            _deciderNotices.Add(text);
    }

    /// <summary>A model bound for a slot, loading it on first use. The same instance is returned every time, because a binding's identity is its reference.</summary>
    public ModelBinding Bind(string slot, string model)
    {
        var descriptor = _catalogue.Find(model);
        if (!descriptor.Serves(slot))
            throw new ArgumentException($"'{descriptor.Name}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.", nameof(model));
        lock (_gate)
        {
            if (_bound.TryGetValue((slot, descriptor.Name), out var bound))
                return bound;
            var raw = Raw(slot, descriptor);
            bound = new ModelBinding(raw.Profile, Wrap(slot, raw, DevicePriority.Interactive, slot == ModelSlots.Generator ? _feed : null));
            _bound[(slot, descriptor.Name)] = bound;
            return bound;
        }
    }

    /// <summary>Whether a model is configured for a slot (default, escalation or fallback).</summary>
    public bool IsConfigured(string slot, string model) =>
        ConfiguredModels.Any(pair => pair.Slot == slot && string.Equals(pair.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The degraded decider (plan §5.6) on the default generator and text embedder, at fabric priority. It answers yes/no
    /// with the local default even when a session has escalated, which keeps decisions on the model the floors assume.
    /// </summary>
    public IDecider CreateDegradedDecider(DecisionPolicy? policy = null)
    {
        policy ??= DecisionPolicy.Default;
        var generator = Raw(ModelSlots.Generator, _catalogue.Find(_slots[ModelSlots.Generator].Model!));
        var embedder = Raw(ModelSlots.TextEmbedder, _catalogue.Find(_slots[ModelSlots.TextEmbedder].Model!));
        var chat = (IChatClient)Wrap(ModelSlots.Decider, generator, DevicePriority.Fabric, stream: null);
        var embeddings = (IEmbeddingGenerator<string, Embedding<float>>)Wrap(ModelSlots.Decider, embedder, DevicePriority.Fabric, stream: null);
        var decider = new DegradedDecider(embeddings, chat, policy.Catalogue, chat.GetService(typeof(ITokenCounter)) as ITokenCounter,
            askTurnBlocking: policy.Floors.DegradedTurnBlocking);
        return new InstrumentedDecider(decider, _metrics.For(ModelSlots.Decider, DegradedDecider.DeciderName));
    }

    /// <summary>Every configured <c>embedder.text</c> model, keyed by profile id, at background priority for memory (plan §4.3).</summary>
    public IReadOnlyList<MemoryEmbedder> CreateMemoryEmbedders() =>
        [.. ConfiguredModels.Where(pair => pair.Slot == ModelSlots.TextEmbedder).Select(pair => pair.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(model => Raw(ModelSlots.TextEmbedder, _catalogue.Find(model)))
            .Select(raw => new MemoryEmbedder(raw.Profile.Id,
                (IEmbeddingGenerator<string, Embedding<float>>)Wrap("memory", raw, DevicePriority.Background, stream: null)))];

    /// <summary>The generator's own token counter, when its binding offers one (Starling's Qwen does).</summary>
    public ITokenCounter? GeneratorCounter() =>
        Raw(ModelSlots.Generator, _catalogue.Find(_slots[ModelSlots.Generator].Model!)).Service is IChatClient chat
            ? chat.GetService(typeof(ITokenCounter)) as ITokenCounter
            : null;

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var raw in _raw.Values)
                (raw.Service as IDisposable)?.Dispose();
            _raw.Clear();
            _bound.Clear();
        }
    }

    private ModelBinding Raw(string slot, ModelDescriptor descriptor)
    {
        lock (_gate)
        {
            if (_raw.TryGetValue(descriptor.Name, out var raw))
                return raw;
            var placement = descriptor.Profile.Placement;
            var scheduler = DeviceSchedulers.IsLocalDevice(placement)
                ? _devices.For(placement).GetTaskScheduler(DevicePriority.Interactive)
                : TaskScheduler.Default;
            raw = _catalogue.Load(descriptor.Name, new ModelLoadContext(slot, scheduler));
            _raw[descriptor.Name] = raw;
            return raw;
        }
    }

    private object Wrap(string slot, ModelBinding raw, DevicePriority priority, TurnFeed? stream)
    {
        var profile = raw.Profile;
        var device = DeviceSchedulers.IsLocalDevice(profile.Placement) ? _devices.For(profile.Placement) : null;
        var latency = _metrics.For(slot, profile.Id);
        return raw.Service switch
        {
            IChatClient chat => new InstrumentedChatClient(device is null ? chat : chat.Schedule(device, priority), profile, slot, latency, stream),
            IEmbeddingGenerator<string, Embedding<float>> embedder =>
                new InstrumentedEmbeddingGenerator(device is null ? embedder : embedder.Schedule(device, priority), latency),
            // Extractors take the device's TaskScheduler when they are loaded (ModelLoadContext); wrapping again would nest.
            IExtractor extractor => new InstrumentedExtractor(extractor, latency),
            // Kev runs synchronously in DecideAsync, so starting it on the device runs all of it there, at fabric priority.
            IDecider decider => new InstrumentedDecider(device is null ? decider : decider.Schedule(device, DevicePriority.Fabric), latency),
            var other => other,
        };
    }
}

/// <summary>Latency of a whole checkpoint's answers from one decider, for <c>/status</c>.</summary>
internal sealed class InstrumentedDecider(IDecider inner, ModelLatency latency) : IDecider
{
    public string Name => inner.Name;

    public async ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var failed = true;
        try
        {
            var result = await inner.DecideAsync(request, cancellationToken).ConfigureAwait(false);
            failed = false;
            return result;
        }
        finally
        {
            latency.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started), failed);
        }
    }
}
