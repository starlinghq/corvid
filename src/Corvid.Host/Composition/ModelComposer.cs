using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Tokens;
using Corvid.Fabric;
using Corvid.Host.Audit;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Scheduling;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Models.Images;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Composition;

/// <summary>
/// Builds the slot registry from configuration (plan §4.1): loads each configured model once through the catalogue, then
/// wraps it per slot. A chat or embedding binding starts its calls on its device at interactive priority and carries a
/// span and latency layer (the generator's also streams to a watching surface). The degraded decider gets its own
/// wrappings of the generator and embedder at fabric priority, and memory its embedders at background priority, so one
/// model serves all three in the device's priority order. The decider slot (Kev) runs at fabric priority and is optional:
/// a decider model that is not usable offline leaves the slot unbound, so the fabric runs on the degraded tier, and the
/// reason is kept in <see cref="DeciderNotices"/>. Every chat binding the egress policy gates (each cloud model, by
/// default) is wrapped in the privacy egress gate (plan §10.4) beneath the span layer, so no path reaches it ungated.
/// </summary>
public sealed class ModelComposer : IDisposable
{
    private readonly ModelCatalogue _catalogue;
    private readonly CorvidOptions _options;
    private readonly IReadOnlyDictionary<string, SlotSettings> _slots;
    private readonly Dictionary<string, RemoteModelSettings> _remoteByProfile = new(StringComparer.Ordinal);
    private readonly DeviceSchedulers _devices;
    private readonly ModelMetrics _metrics;
    private readonly TurnFeed _feed;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ModelBinding> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Slot, string Model, DevicePriority Priority), ModelBinding> _bound = [];
    private readonly Dictionary<ModelBinding, string> _names = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _deciderNotices = [];
    private PrivacyEgress? _egress;

    public ModelComposer(ModelCatalogue catalogue, CorvidOptions options, DeviceSchedulers devices, ModelMetrics metrics, TurnFeed feed)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(options);
        _catalogue = catalogue;
        _options = options;
        _slots = options.Models.EffectiveSlots();
        foreach (var model in catalogue.Models.Where(model => model.Provider == RemoteModelProvider.ProviderName))
            if (options.Models.Remote.TryGetValue(model.Name, out var settings))
                _remoteByProfile[model.Profile.Id] = settings;
        _devices = devices;
        _metrics = metrics;
        _feed = feed;
        Ledger = new EgressLedger(CorvidPaths.EgressDatabase(options)) { Retention = options.Privacy.Ledger.Retention() };
    }

    /// <summary>
    /// The egress ledger (G-9) every gated binding records its requests in; its <see cref="EgressLedger.Log"/> is set once the
    /// runtime has opened the event log (no session sends anything before that).
    /// </summary>
    public EgressLedger Ledger { get; }

    /// <summary>Told each model load as it begins and ends, while set (by <see cref="CorvidRuntimeFactory"/> during a start).</summary>
    public IProgress<StartupStep>? Progress { get; set; }

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

    /// <summary>
    /// Why the decider runs on the degraded tier or without its second tier, or on the CPU instead of its configured GPU
    /// (C-029, <see cref="PlacementFallback.Cpu"/>), after <see cref="Compose"/>; empty when Kev is bound as configured.
    /// </summary>
    public IReadOnlyList<string> DeciderNotices
    {
        get
        {
            lock (_gate)
                return [.. _deciderNotices];
        }
    }

    /// <summary>Loads every configured model and binds the registry. The decider slot is bound only with what is usable offline.</summary>
    /// <param name="skip">
    /// Escalation and fallback models to leave out because their service is not there (a cloud model without Vault, C-015);
    /// a slot's default is never skipped.
    /// </param>
    /// <exception cref="ModelUnavailableException">A model of another slot is not usable offline.</exception>
    public ModelSlotRegistry Compose(IReadOnlySet<string>? skip = null)
    {
        var registry = new ModelSlotRegistry();
        bool Keep(string model) => skip?.Contains(_catalogue.Find(model).Name) != true;
        foreach (var (slot, settings) in _slots)
        {
            if (slot == ModelSlots.Decider)
            {
                BindDecider(registry, settings);
                continue;
            }
            registry.Bind(slot, Bind(slot, settings.Model!), settings.Escalation.Where(Keep).Select(model => Bind(slot, model)),
                settings.Fallbacks.Where(Keep).Select(model => Bind(slot, model)));
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
        // Any load failure, ONNX Runtime's own included (Kev-4B without the memory for its 18 GB, say): the decider is
        // optional, and the degraded tier is the fail-closed answer.
        catch (Exception exception)
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

    /// <summary>Whether a model is the default of some slot (it cannot be skipped when unavailable).</summary>
    public bool IsDefault(string model) =>
        _slots.Values.Any(settings => string.Equals(_catalogue.Find(settings.Model!).Name, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The privacy egress gate shared by every gated binding and the switch-time check: the configured extractor's model
    /// (GLiNER2 by default) scans outgoing requests. Without an extractor every gated request is refused.
    /// </summary>
    public PrivacyEgress Egress
    {
        get
        {
            lock (_gate)
            {
                if (_egress is not null)
                    return _egress;
                var extractor = _slots.TryGetValue(ModelSlots.Extractor, out var settings)
                    ? Raw(ModelSlots.Extractor, _catalogue.Find(settings.Model!)).Service as IExtractor
                    : null;
                // A5: Corvid's own system prompts (the turn's configured one, the degraded decider's, titles, recaps, skill and
                // lesson learning, a child session's) are not the person's text: never scanned nor rewritten in a system message.
                var prompt = _options.Turn.SystemPrompt is { Length: > 0 } configured ? configured : Kernel.Turns.TurnOptions.DefaultSystemPrompt;
                return _egress = new PrivacyEgress(extractor, new PrivacyEgressOptions
                {
                    PolicyFor = PolicyFor,
                    TrustedTexts =
                    [
                        prompt, Fabric.DegradedDecider.SystemPrompt, Sessions.SessionTitler.TitlePrompt, Sessions.SessionTitler.RecapPrompt,
                        Skills.SkillLearner.SystemPrompt, Kernel.Lessons.LessonLearner.SystemPrompt,
                        CorvidRuntime.ChildSystemPrompt, CorvidRuntime.ChildSchemaPrompt, Kernel.Turns.TurnRunner.DescribePrompt,
                    ],
                });
            }
        }
    }

    /// <summary>
    /// The egress policy for a profile (plan §10.4): a remote model's own <c>Egress</c> settings over <c>Corvid:Privacy</c>.
    /// Cloud models are gated (redact by default); local servers only when configured; in-process models never.
    /// </summary>
    public EgressPolicy? PolicyFor(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Privacy == PrivacyClass.Local)
            return null;
        var privacy = _options.Privacy;
        var own = _remoteByProfile.GetValueOrDefault(profile.Id)?.Egress;
        var action = own?.Action ?? (profile.Privacy == PrivacyClass.Cloud ? privacy.Cloud : privacy.LocalServer);
        if (action is not { } chosen)
            return null;
        return new EgressPolicy
        {
            Action = chosen,
            Labels = privacy.Labels.Count > 0 ? [.. privacy.Labels] : EgressPolicy.DefaultLabels,
            ExtraLabels = [.. privacy.ExtraLabels, .. own?.ExtraLabels ?? []],
            Threshold = own?.Threshold ?? privacy.Threshold,
            Tainted = own?.Tainted ?? privacy.Tainted,
            Images = own?.Images ?? privacy.Images,
        };
    }

    /// <summary>A model bound for a slot, loading it on first use. The same instance is returned every time, because a binding's identity is its reference.</summary>
    public ModelBinding Bind(string slot, string model) => Bind(slot, model, DevicePriority.Interactive);

    /// <summary>
    /// A model bound for a slot at a device priority (A5: <see cref="DevicePriority.Subagent"/> for a child session, so its
    /// in-process calls queue behind the interactive turn and the fabric, plan §3.3). One instance per slot, model and
    /// priority. A decider runs at fabric priority whatever is asked; a remote model is not scheduled on a device at all.
    /// </summary>
    public ModelBinding Bind(string slot, string model, DevicePriority priority)
    {
        var descriptor = _catalogue.Find(model);
        if (!descriptor.Serves(slot))
            throw new ArgumentException($"'{descriptor.Name}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.", nameof(model));
        lock (_gate)
        {
            if (_bound.TryGetValue((slot, descriptor.Name, priority), out var bound))
                return bound;
            var raw = Raw(slot, descriptor);
            bound = new ModelBinding(raw.Profile, Wrap(slot, raw, priority, slot == ModelSlots.Generator ? _feed : null));
            _bound[(slot, descriptor.Name, priority)] = bound;
            _names[bound] = descriptor.Name;
            return bound;
        }
    }

    /// <summary>The catalogue name of a binding this composer made (any priority), or null for one it did not.</summary>
    public string? NameOf(ModelBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
            return _names.GetValueOrDefault(binding);
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

    /// <summary>
    /// The default generator and text embedder at background priority (plan §9.5), for the skill learning loop: drafting
    /// and replay turns queue behind the user's turns and the fabric on the same device.
    /// </summary>
    public ModelSlotRegistry CreateBackgroundSlots()
    {
        var registry = new ModelSlotRegistry();
        foreach (var slot in new[] { ModelSlots.Generator, ModelSlots.TextEmbedder })
        {
            var raw = Raw(slot, _catalogue.Find(_slots[slot].Model!));
            registry.Bind(slot, new ModelBinding(raw.Profile, Wrap("background", raw, DevicePriority.Background, stream: null)));
        }
        return registry;
    }

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
            _names.Clear();
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
            // A placement fallback (C-029: Kev configured on CUDA, loaded on the CPU) is reported with the decider's notices;
            // the binding's own profile then says cpu, and Wrap schedules by it.
            raw = StartupStep.Run(Progress, $"Loading {descriptor.Name} for {slot} on {placement}",
                () => _catalogue.Load(descriptor.Name, new ModelLoadContext(slot, scheduler) { Notice = Notice }));
            _raw[descriptor.Name] = raw;
            return raw;
        }
    }

    // A remote profile's placement is its endpoint's authority (RemoteModelProvider), which is where the requests go.
    private IChatClient Gate(IChatClient chat, ModelProfile profile, string slot) =>
        PolicyFor(profile) is null ? new ModelRequestScopeChatClient(chat) : new PrivacyEgressChatClient(chat, profile, Egress,
            Ledger.For(slot, profile.Placement, _remoteByProfile.GetValueOrDefault(profile.Id)?.Secret));

    private object Wrap(string slot, ModelBinding raw, DevicePriority priority, TurnFeed? stream)
    {
        var profile = raw.Profile;
        var device = DeviceSchedulers.IsLocalDevice(profile.Placement) ? _devices.For(profile.Placement) : null;
        var latency = _metrics.For(slot, profile.Id);
        return raw.Service switch
        {
            // K-8 (A6): outside the egress gate, so the gate sees what is sent: no image to a model that cannot read one.
            IChatClient chat => new InstrumentedChatClient(new ImageCapabilityChatClient(
                Gate(device is null ? chat : chat.Schedule(device, priority), profile, slot), profile), profile, slot, latency, stream),
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
