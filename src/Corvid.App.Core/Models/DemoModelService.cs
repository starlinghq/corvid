using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Models;

/// <summary>Which machine the demo's model store describes.</summary>
public enum DemoModelScene
{
    /// <summary>Models.png's moment: every default model cached, Kev-4B 42% downloaded, Vault up, Claude's key bound.</summary>
    Main,

    /// <summary>
    /// FirstRun.png's machine: nothing configured yet, the generator and the extractor cached, embeddinggemma-2 and
    /// kev-0.8b missing (so the decider runs degraded), Kev-4B not downloaded, and no Vault.
    /// </summary>
    FirstRun,
}

/// <summary>
/// The demo's model store (docs/plan/ui.md §4.8, F1, F14, F23), drawing the mockups' moments and answering every change
/// the way the engine's validator would. Downloads a person starts run at 48 MB/s, one at a time, through timers from
/// the given clock (so tests move them with a manual clock); the Main scene's Kev-4B download stands at the mockup's 42%
/// until it is cancelled and started again, so every screenshot of the scene is the same. Changes are announced on the
/// UI thread through the dispatcher.
/// </summary>
public sealed class DemoModelService : IModelService, IDisposable
{
    public const string ConfigPath = "~/.config/corvid/corvid.json";

    /// <summary>The demo's download rate, the mockups' 48 MB/s.</summary>
    public const double Rate = 48e6;

    /// <summary>How often a running download reports progress.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly List<string> _queue = [];
    private readonly Dictionary<(string Slot, string Model), SlotPlacement> _localPlacements = [];
    private ModelsSnapshot _snapshot;
    private ITimer? _timer;

    public DemoModelService(IDispatcher dispatcher, TimeProvider time, DemoModelScene scene = DemoModelScene.Main)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _dispatcher = dispatcher;
        _time = time;
        Scene = scene;
        _snapshot = scene == DemoModelScene.FirstRun ? FirstRunScene() : MainScene();
    }

    public event EventHandler? Changed;

    public DemoModelScene Scene { get; }

    public ModelsSnapshot Snapshot => _snapshot;

    /// <summary>The downloads running or waiting, in order; the first one is the one moving.</summary>
    public IReadOnlyList<string> Queue => _queue;

    public Task<ModelsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);

    public Task<ConfigWrite> SetLadderAsync(string slot, IReadOnlyList<string> ladder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        if (_snapshot.Slot(slot) is not { } current)
            return Refused($"There is no slot named {E(slot)}.");
        if (current.Name is not (ModelSlotNames.Generator or ModelSlotNames.Decider))
            return Refused($"The {current.Name} has no escalation ladder.");
        if (ladder.Distinct(StringComparer.Ordinal).Count() != ladder.Count)
            return Refused("A model can be on the ladder only once.");
        foreach (var name in ladder)
        {
            if (string.Equals(name, current.Model, StringComparison.Ordinal))
                return Refused($"{E(name)} is the {current.Name} itself; the ladder holds the models a hard step goes to.");
            // A reorder keeps the steps already configured, even one unavailable now (a cloud step hidden while Vault is
            // absent); only a step new to the ladder is validated.
            if (current.Escalation.Contains(name, StringComparer.Ordinal))
                continue;
            if (_snapshot.Find(name) is not { } model || !model.CanServe.Contains(current.Name))
                return Refused($"{E(name)} cannot serve as the {current.Name}.");
            if (model.Kind == ModelKind.Cloud && model.State == ModelState.Unavailable)
                return Refused($"{E(name)} has no key: set `{E(model.Secret)}` in Vault first.");
        }

        Replace(current with { Ladder = [.. ladder] });
        var order = ladder.Count == 0 ? "nowhere: no escalation" : string.Join(", then ", ladder.Select(E));
        return Written($"Written to `{ConfigPath}`: a hard step on the {current.Name} goes to {order}. Applies to new sessions.");
    }

    public Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default)
    {
        if (_snapshot.Slot(slot) is not { } current)
            return Refused($"There is no slot named {E(slot)}.");
        if (current.Shared)
            return Refused($"The {current.Name} is shared by every session: run `corvid model switch {current.Name} …`, then restart.");
        if (_snapshot.Find(model) is not { } chosen || !chosen.CanServe.Contains(current.Name))
            return Refused($"{E(model)} cannot serve as the {current.Name}.");
        if (!chosen.IsAvailable)
            return Refused(chosen.Kind == ModelKind.Local
                ? $"{E(model)} is not downloaded yet."
                : $"{E(model)} is unavailable: {Unavailable(chosen)}.");
        if (string.Equals(current.Model, model, StringComparison.Ordinal))
            return Written($"The {current.Name} is already {E(model)}.");

        // A slot back on a local model runs where that model last ran in it (or where its placement says), not where the
        // server or cloud model it leaves ran.
        if (current.Placement is { } leaving && current.Model is { } leavingModel && _snapshot.Find(leavingModel) is { Kind: ModelKind.Local })
            _localPlacements[(current.Name, leavingModel)] = leaving;
        var placement = chosen.Kind switch
        {
            ModelKind.Local => _localPlacements.TryGetValue((current.Name, model), out var last) ? last : LocalPlacement(model),
            ModelKind.Server => new SlotPlacement("local server", 0, 620),
            _ => new SlotPlacement("cloud", 0, 1400),
        };
        Replace(current with
        {
            Model = model,
            IsDefault = model == DefaultModel(current.Name),
            Ladder = current.Ladder?.Where(step => step != model).ToList(),
            Placement = placement,
        });
        return Written($"Written to `{ConfigPath}`: the {current.Name} is {E(model)}. Applies to new sessions; this one switches after its issued tool calls.");
    }

    public Task<ConfigWrite> SetPinnedAsync(string slot, bool pinned, CancellationToken cancellationToken = default)
    {
        if (_snapshot.Slot(slot) is not { } current)
            return Refused($"There is no slot named {E(slot)}.");
        if (!current.Pinnable)
            return Refused($"The {current.Name} cannot be pinned.");
        Replace(current with { Pinned = pinned });
        return Written(pinned
            ? $"The {current.Name} is pinned to {E(current.Model)} for this session: escalation leaves it alone."
            : $"The {current.Name} is no longer pinned: a hard step escalates again.");
    }

    public Task<ConfigWrite> SetPlacementAsync(string model, string device, CancellationToken cancellationToken = default)
    {
        if (_snapshot.Placements.FirstOrDefault(placement => placement.Model == model) is not { } current)
            return Refused($"{E(model)} is not a local model with a placement.");
        if (device is not ("cuda" or "cpu"))
            return Refused($"{E(device)} is not a device on this machine: cuda or cpu.");
        if (device == "cuda" && _snapshot.Gpus.Count == 0)
            return Refused("No GPU passed the doctor's checks.");
        if (current.Device == device)
            return Written($"{E(model)} already runs on {device}.");

        var placements = _snapshot.Placements.Select(placement => placement == current ? placement with { Device = device } : placement).ToList();
        var slots = _snapshot.Slots.Select(slot =>
        {
            if (slot.Model != model || slot.Placement is not { } at)
                return slot;
            var p50 = device == "cpu" ? at.P50Milliseconds * 4 : Math.Max(1, at.P50Milliseconds / 4);
            return slot with { Placement = at with { Device = device == "cuda" ? _snapshot.Gpus[0].Id : "cpu", P50Milliseconds = p50 } };
        }).ToList();
        // Where a slot last ran the model no longer holds: a slot that goes back to it follows the new placement.
        foreach (var key in _localPlacements.Keys.Where(key => key.Model == model).ToList())
            _localPlacements.Remove(key);
        Set(_snapshot with { Placements = placements, Slots = slots });
        return Written($"Written to `{ConfigPath}`: {E(model)} on {device}, falling back to {E(current.Fallback)}. Applies when it next loads.");
    }

    public Task DownloadAsync(IEnumerable<string> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        var catalogue = _snapshot.Catalogue.ToList();
        var changed = false;
        foreach (var name in models)
        {
            var index = catalogue.FindIndex(model => model.Name == name);
            if (index < 0 || catalogue[index] is not { Kind: ModelKind.Local, State: ModelState.Missing } model)
                continue;
            _queue.Add(name);
            catalogue[index] = model with { State = ModelState.Queued, Download = new DownloadProgress(0, model.SizeBytes, Rate) };
            changed = true;
        }

        if (!changed)
            return Task.CompletedTask;
        Set(_snapshot with { Catalogue = catalogue }, raise: false);
        Advance(0);
        _timer ??= _time.CreateTimer(_ => _dispatcher.Post(() => Advance(Rate * Tick.TotalSeconds)), null, Tick, Tick);
        return Task.CompletedTask;
    }

    public Task CancelDownloadAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_snapshot.Find(model) is not { State: ModelState.Downloading or ModelState.Queued } entry)
            return Task.CompletedTask;
        _queue.Remove(model);
        // The partial file goes with the download: the cache holds finished models only.
        Update(entry with { State = ModelState.Missing, Download = null });
        if (_snapshot.Slot(ModelSlotNames.Decider)?.Escalation.Contains(model) == true)
            _snapshot = _snapshot with { Decider = _snapshot.Decider with { SecondTier = $"{model} for gates below their floor, when it is downloaded" } };
        Advance(0);
        StopWhenIdle();
        return Task.CompletedTask;
    }

    public Task<ServerCheck> CheckServerAsync(string model, CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshot.Find(model) switch
        {
            { Kind: ModelKind.Server, State: ModelState.Reachable } server => new ServerCheck(true, $"{E(server.Name)} answered in 41 ms · {E(server.Fit)}."),
            { Kind: ModelKind.Server } server => new ServerCheck(false, $"{E(server.Name)} did not answer: {Where(server)}."),
            null => new ServerCheck(false, $"There is no model named {E(model)}."),
            var other => new ServerCheck(false, $"{E(other.Name)} is not a local server."),
        });

    public Task<ConfigWrite> AddServerAsync(string name, string url, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        url = (url ?? "").Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            return Refused("Give the server a name without spaces, such as qwen-coder.");
        if (_snapshot.Find(name) is not null)
            return Refused($"The catalogue already has a model named {E(name)}.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Refused("The address must be an http or https URL, such as http://127.0.0.1:11434/v1.");

        // The demo's only answering servers are on this machine.
        var answers = uri.IsLoopback;
        var server = new CatalogueModel(name, ModelKind.Server, "generator", "generator", $"local server · `{E(uri.ToString())}`",
            answers ? "context 32k" : "context unknown", 0, answers ? ModelState.Reachable : ModelState.Unreachable,
            [ModelSlotNames.Generator, ModelSlotNames.Summarizer]);
        Set(_snapshot with { Catalogue = [.. _snapshot.Catalogue, server] });
        return answers
            ? Written($"Written to `{ConfigPath}`: {E(name)} at `{E(uri.ToString())}` answered and can serve the generator.")
            : Written($"Written to `{ConfigPath}`: {E(name)} at `{E(uri.ToString())}`, which did not answer; it stays unavailable until it does.");
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Moves the first queued download on by <paramref name="bytes"/>, finishing it and starting the next.</summary>
    private void Advance(double bytes)
    {
        while (_queue.Count > 0)
        {
            var name = _queue[0];
            if (_snapshot.Find(name) is not { Download: { } progress } entry)
            {
                _queue.RemoveAt(0);
                continue;
            }

            var done = Math.Min(progress.Total, progress.Bytes + (long)bytes);
            if (done < progress.Total)
            {
                Update(entry with { State = ModelState.Downloading, Download = progress with { Bytes = done } });
                return;
            }

            _queue.RemoveAt(0);
            bytes = 0;
            Finish(entry);
        }

        StopWhenIdle();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A finished download: cached, and what waited on it (the decider's tier, its second tier) follows.</summary>
    private void Finish(CatalogueModel entry)
    {
        var snapshot = _snapshot with { Catalogue = With(_snapshot.Catalogue, entry with { State = ModelState.Cached, Download = null }) };
        if (entry.Name == snapshot.Decider.Model)
        {
            snapshot = snapshot with
            {
                Decider = snapshot.Decider with
                {
                    Tier = DeciderTier.Calibrated,
                    Floors = "catalogue 2026-10-21 · ingress p50 240 ms on cuda:0",
                },
            };
        }

        if (snapshot.Slot(ModelSlotNames.Decider)?.Escalation.Contains(entry.Name) == true)
            snapshot = snapshot with { Decider = snapshot.Decider with { SecondTier = $"{entry.Name} for gates below their floor" } };
        _snapshot = snapshot;
    }

    private void StopWhenIdle()
    {
        if (_queue.Count > 0)
            return;
        _timer?.Dispose();
        _timer = null;
    }

    private void Update(CatalogueModel entry) => Set(_snapshot with { Catalogue = With(_snapshot.Catalogue, entry) });

    private void Replace(ModelSlot slot) =>
        Set(_snapshot with { Slots = [.. _snapshot.Slots.Select(existing => existing.Name == slot.Name ? slot : existing)] });

    private void Set(ModelsSnapshot snapshot, bool raise = true)
    {
        _snapshot = snapshot;
        if (raise)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<CatalogueModel> With(IReadOnlyList<CatalogueModel> catalogue, CatalogueModel entry) =>
        [.. catalogue.Select(model => model.Name == entry.Name ? entry : model)];

    private static string Unavailable(CatalogueModel model) => model.Secret is { } secret ? $"set `{E(secret)}` in Vault" : "no key";

    /// <summary>Where a server runs, as plain words inside a message: its marks dropped, then escaped.</summary>
    private static string Where(CatalogueModel model) => E(Spans.Plain(model.Where));

    /// <summary>A name or address put into a message (markup): its marks are text (<see cref="Spans.Escape"/>).</summary>
    private static string E(string? value) => Spans.Escape(value);

    /// <summary>Where a local model runs when a slot has not run it yet: its placement's device (the first GPU for cuda).</summary>
    private SlotPlacement LocalPlacement(string model) =>
        _snapshot.Placements.FirstOrDefault(placement => placement.Model == model) is { Device: "cuda" } && _snapshot.Gpus.Count > 0
            ? new SlotPlacement(_snapshot.Gpus[0].Id, 0, 180)
            : new SlotPlacement("cpu", null, 100);

    private static string? DefaultModel(string slot) => slot switch
    {
        ModelSlotNames.Generator or ModelSlotNames.Summarizer => "qwen3.5-0.8b",
        ModelSlotNames.Decider => "kev-0.8b",
        ModelSlotNames.Extractor => "gliner2-base-v1",
        ModelSlotNames.EmbedderText => "embeddinggemma-2",
        _ => null,
    };

    private static Task<ConfigWrite> Written(string message) => Task.FromResult(new ConfigWrite(true, message));

    private static Task<ConfigWrite> Refused(string message) => Task.FromResult(new ConfigWrite(false, message));

    private static IReadOnlyList<ModelSlot> Slots(IReadOnlyList<string> generatorLadder) =>
    [
        new(ModelSlotNames.Generator, "qwen3.5-0.8b", IsDefault: true, Ladder: generatorLadder, Note: "a hard step escalates up this ladder",
            Placement: new("cuda:0", 0, 180), Pinnable: true),
        new(ModelSlotNames.Summarizer, "qwen3.5-0.8b", Placement: new("cuda:0", 0, 210)),
        new(ModelSlotNames.Decider, "kev-0.8b", Ladder: ["kev-4b"], Note: "second tier for gates below their floor",
            Placement: new("cuda:0", 0, 240), Shared: true),
        new(ModelSlotNames.Extractor, "gliner2-base-v1", Placement: new("cpu", null, 38)),
        new(ModelSlotNames.EmbedderText, "embeddinggemma-2", Fallback: "e5", Note: "memory embeds only with models configured at start",
            Placement: new("cpu", null, 12)),
        new(ModelSlotNames.EmbedderMedia, null, Note: "media memory comes with plan §9.4"),
        new(ModelSlotNames.Vision, null, Note: "needed for images in prompts (K-8)"),
    ];

    private static IReadOnlyList<CatalogueModel> LocalModels(bool firstRun) =>
    [
        new("qwen3.5-0.8b", ModelKind.Local, "generator, summarizer", "generator", "Starling, in-process", "1.9 GB · cuda fits",
            1_900_000_000, ModelState.Cached, [ModelSlotNames.Generator, ModelSlotNames.Summarizer], InDefaultSet: true),
        new("kev-0.8b", ModelKind.Local, "decider", "decider", "Starling, in-process", "2.1 GB · cuda fits",
            2_100_000_000, firstRun ? ModelState.Missing : ModelState.Cached, [ModelSlotNames.Decider], InDefaultSet: true,
            SetupNote: "fits cuda:0"),
        new("kev-4b", ModelKind.Local, "decider, second tier", "second decider tier", "Starling, in-process",
            "17 GB · needs 18 GB RAM · cuda fits (24 GB)", 17_000_000_000, firstRun ? ModelState.Missing : ModelState.Downloading,
            [ModelSlotNames.Decider], SetupNote: "needs 18 GB RAM",
            Download: firstRun ? null : new DownloadProgress(7_140_000_000, 17_000_000_000, Rate)),
        new("gliner2-base-v1", ModelKind.Local, "extractor", "extractor", "Starling, in-process", "0.6 GB",
            600_000_000, ModelState.Cached, [ModelSlotNames.Extractor], InDefaultSet: true),
        new("embeddinggemma-2", ModelKind.Local, "embedder.text", "embedder.text", "Starling, in-process", "1.2 GB",
            1_200_000_000, firstRun ? ModelState.Missing : ModelState.Cached, [ModelSlotNames.EmbedderText], InDefaultSet: true),
    ];

    private static IReadOnlyList<GpuDevice> Gpus { get; } =
    [
        new("cuda:0", "NVIDIA RTX 3090 · 24 GB · driver 580 · CUDA 13 runtime found",
        [
            new("driver", true, "NVIDIA 580.65 · CUDA driver API 13.0"),
            new("cuda runtime", true, "libcudart.so.13, libcublas.so.13 resolve for this user"),
        ]),
    ];

    private static IReadOnlyList<ModelPlacement> Placements { get; } =
    [
        new("kev-0.8b", "cuda", "cpu", NoticeOnFallback: true),
        new("qwen3.5-0.8b", "cuda", "cpu", NoticeOnFallback: false),
    ];

    private const string Cpu = "16 threads for the fabric, 12 for ONNX intra-op";

    /// <summary>Models.png: the corvid workspace's configuration, every default model cached, Kev-4B on its way.</summary>
    private static ModelsSnapshot MainScene() => new(
        ConfigPath,
        Validated: true,
        Slots(["qwen-27b", "claude"]),
        [
            .. LocalModels(firstRun: false),
            new("qwen-27b", ModelKind.Server, "generator, escalation 1", "generator", "local server · `http://127.0.0.1:8080/v1`", "context 32k",
                0, ModelState.Reachable, [ModelSlotNames.Generator, ModelSlotNames.Summarizer]),
            new("claude", ModelKind.Cloud, "generator, escalation 2 · claude-opus-5-5", "generator", "anthropic · key `anthropic.key` in Vault",
                "context 1M · accepted 200k", 0, ModelState.KeyBound, [ModelSlotNames.Generator, ModelSlotNames.Summarizer],
                Secret: "anthropic.key", Egress: "redact"),
            new("gemini", ModelKind.Cloud, "configured, no key", "generator", "gemini · key `gemini.key`", "context 1M",
                0, ModelState.Unavailable, [ModelSlotNames.Generator, ModelSlotNames.Summarizer], Secret: "gemini.key", Egress: "redact"),
        ],
        new CloudPrivacy("redact", ExtractorBound: true, VaultPresent: true, RequestsToday: 7, RedactedToday: 11, RefusedToday: 0),
        new DeciderStatus("kev-0.8b", "Kev-0.8B", DeciderTier.Calibrated,
            "catalogue 2026-10-21 · ingress p50 240 ms on cuda:0",
            "kev-4b for gates below their floor, after the download finishes",
            "the degraded tier (generator and embedder) for states over the hard stop",
            "full checkpoints (the CPU-only reduction is off)"),
        Gpus,
        Placements,
        Cpu);

    /// <summary>FirstRun.png's machine: defaults only, two default models missing, no Vault.</summary>
    private static ModelsSnapshot FirstRunScene() => new(
        ConfigPath,
        Validated: true,
        Slots(["claude"]),
        [
            .. LocalModels(firstRun: true),
            new("claude", ModelKind.Cloud, "generator, escalation · claude-opus-5-5", "generator", "anthropic · key `anthropic.key` · Vault absent",
                "context 1M · accepted 200k", 0, ModelState.Unavailable, [ModelSlotNames.Generator, ModelSlotNames.Summarizer],
                Secret: "anthropic.key", Egress: "redact"),
        ],
        new CloudPrivacy("redact", ExtractorBound: true, VaultPresent: false, RequestsToday: 0, RedactedToday: 0, RefusedToday: 0),
        new DeciderStatus("kev-0.8b", "Kev-0.8B", DeciderTier.Degraded,
            "none until kev-0.8b is downloaded; every gate uses the degraded tier",
            "kev-4b for gates below their floor, when it is downloaded",
            "the degraded tier (generator and embedder) for states over the hard stop",
            "full checkpoints (the CPU-only reduction is off)"),
        Gpus,
        Placements,
        Cpu);
}
