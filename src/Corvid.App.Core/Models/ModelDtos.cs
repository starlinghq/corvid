using System.Globalization;

namespace Corvid.App.Core.Models;

/// <summary>
/// The seven model slots (docs/plan/ui.md §4.8), in the order the Models page lists them. The names are
/// <c>corvid.json</c>'s and <c>corvid model switch</c>'s.
/// </summary>
public static class ModelSlotNames
{
    public const string Generator = "generator";
    public const string Summarizer = "summarizer";
    public const string Decider = "decider";
    public const string Extractor = "extractor";
    public const string EmbedderText = "embedder.text";
    public const string EmbedderMedia = "embedder.media";
    public const string Vision = "vision";

    public static IReadOnlyList<string> All { get; } = [Generator, Summarizer, Decider, Extractor, EmbedderText, EmbedderMedia, Vision];
}

/// <summary>Where a catalogue model runs: in-process through Starling, on a local OpenAI-compatible server, or in a cloud.</summary>
public enum ModelKind
{
    Local,
    Server,
    Cloud,
}

/// <summary>
/// A catalogue model's state. Local models are cached, missing, queued or downloading; servers are reachable or not;
/// cloud models have their key bound in Vault or are unavailable (no key, or no Vault).
/// </summary>
public enum ModelState
{
    Cached,
    Missing,
    Queued,
    Downloading,
    Reachable,
    Unreachable,
    KeyBound,
    Unavailable,
}

/// <summary>A download's progress: bytes so far of the total, at the current rate.</summary>
public sealed record DownloadProgress(long Bytes, long Total, double BytesPerSecond)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Bytes / Total, 0, 1);

    /// <summary>Whole percent done, rounded down (a hair of tolerance, so 42% of 17 GB is not printed as 41%).</summary>
    public int Percent => (int)Math.Floor(Fraction * 100 + 1e-9);

    /// <summary>The time left at the current rate; zero when the rate is unknown.</summary>
    public TimeSpan Left => BytesPerSecond <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, Total - Bytes) / BytesPerSecond);
}

/// <summary>
/// One model the catalogue knows (docs/plan/ui.md §4.8): a local Starling model, a configured local server or a cloud
/// model. Text fields may mark code, paths and names with backticks (<c>key `anthropic.key` in Vault</c>), which the
/// views set in the code font.
/// </summary>
/// <param name="Name">The name the configuration uses (<c>kev-4b</c>).</param>
/// <param name="Serves">What it serves, as the catalogue says it ("generator, escalation 1").</param>
/// <param name="Role">The short role the first run lists it with ("second decider tier").</param>
/// <param name="Where">Where it runs ("Starling, in-process", "local server · `http://127.0.0.1:8080/v1`").</param>
/// <param name="Fit">Size and device fit ("17 GB · needs 18 GB RAM · cuda fits (24 GB)", "context 32k").</param>
/// <param name="SizeBytes">The download's size; 0 for servers and cloud models.</param>
/// <param name="CanServe">The slots it may be switched into or escalated to.</param>
/// <param name="InDefaultSet">Part of the default download set (about 10 GB); Kev-4B is optional.</param>
/// <param name="SetupNote">What the first run adds after its size ("fits cuda:0", "needs 18 GB RAM").</param>
/// <param name="Secret">The Vault secret a cloud model's key lives in.</param>
/// <param name="Egress">A cloud model's egress posture ("redact").</param>
public sealed record CatalogueModel(
    string Name,
    ModelKind Kind,
    string Serves,
    string Role,
    string Where,
    string Fit,
    long SizeBytes,
    ModelState State,
    IReadOnlyList<string> CanServe,
    bool InDefaultSet = false,
    string SetupNote = "",
    DownloadProgress? Download = null,
    string? Secret = null,
    string? Egress = null)
{
    /// <summary>Usable now: cached, reachable, or with its key bound.</summary>
    public bool IsAvailable => State is ModelState.Cached or ModelState.Reachable or ModelState.KeyBound;

    /// <summary>A local model that is not on disk and not on its way.</summary>
    public bool IsMissing => Kind == ModelKind.Local && State == ModelState.Missing;
}

/// <summary>Where a slot's model runs and how fast: device, queue depth (for generating slots) and median latency.</summary>
public sealed record SlotPlacement(string Device, int? Queue, int P50Milliseconds);

/// <summary>
/// One slot (docs/plan/ui.md §4.8): the model bound to it, whether that is the default, the escalation ladder (a hard
/// step goes up it in order), the fallback, a note on what the slot does, placement and latency, and whether it is
/// shared by every session (the decider) or pinned for this one.
/// </summary>
public sealed record ModelSlot(
    string Name,
    string? Model,
    bool IsDefault = false,
    IReadOnlyList<string>? Ladder = null,
    string? Fallback = null,
    string? Note = null,
    SlotPlacement? Placement = null,
    bool Shared = false,
    bool Pinned = false,
    bool Pinnable = false)
{
    public IReadOnlyList<string> Escalation => Ladder ?? [];

    public bool IsBound => Model is not null;
}

/// <summary>The decider's tier (docs/plan/ui.md §4.8, "Kev tiers"): calibrated, or degraded while its model is missing.</summary>
public enum DeciderTier
{
    Calibrated,
    Degraded,
}

/// <summary>The decider panel: the tier and the four facts the Models page lists.</summary>
public sealed record DeciderStatus(string Model, string DisplayName, DeciderTier Tier, string Floors, string SecondTier, string Fallback, string Profile);

/// <summary>
/// What the egress gate does with requests to cloud models (docs/plan/ui.md §4.8, F8): the posture, whether an extractor
/// is bound (without one every cloud request is refused), whether Vault answers (without it cloud slots disappear), and
/// today's counts.
/// </summary>
public sealed record CloudPrivacy(string Posture, bool ExtractorBound, bool VaultPresent, int RequestsToday, int RedactedToday, int RefusedToday)
{
    /// <summary>
    /// What the "today" line says in place of the counts when they are not known (the engine's, until the egress ledger
    /// counts requests, G-9); null when the counts are real.
    /// </summary>
    public string? TodayUnknown { get; init; }
}

/// <summary>One of the doctor's checks for a GPU (the driver, the CUDA runtime), shown beside a model placed on it.</summary>
public sealed record DeviceCheck(string Name, bool Ok, string Detail);

/// <summary>A GPU from the doctor's GPU section, with its one-line summary and its checks.</summary>
public sealed record GpuDevice(string Id, string Summary, IReadOnlyList<DeviceCheck> Checks);

/// <summary>Where a local model runs: its device, and what it falls back to (with a notice in the chat, or silently).</summary>
public sealed record ModelPlacement(string Model, string Device, string Fallback, bool NoticeOnFallback);

/// <summary>Everything the Models page shows, as the configuration and the engine have it now.</summary>
public sealed record ModelsSnapshot(
    string ConfigPath,
    bool Validated,
    IReadOnlyList<ModelSlot> Slots,
    IReadOnlyList<CatalogueModel> Catalogue,
    CloudPrivacy Privacy,
    DeciderStatus Decider,
    IReadOnlyList<GpuDevice> Gpus,
    IReadOnlyList<ModelPlacement> Placements,
    string Cpu)
{
    /// <summary>
    /// False for a snapshot standing in until the first load (or for a store that cannot load): its empty lists and
    /// flags say nothing, so the pages claim neither Vault's absence nor a degraded decider from it.
    /// </summary>
    public bool IsLoaded { get; init; } = true;

    public CatalogueModel? Find(string name) => Catalogue.FirstOrDefault(model => string.Equals(model.Name, name, StringComparison.Ordinal));

    public ModelSlot? Slot(string name) => Slots.FirstOrDefault(slot => string.Equals(slot.Name, name, StringComparison.Ordinal));
}

/// <summary>The answer to a configuration change: written (with what was written) or refused (with why).</summary>
public sealed record ConfigWrite(bool Written, string Message);

/// <summary>A connectivity check of a local server: whether it answered, and what it said.</summary>
public sealed record ServerCheck(bool Reachable, string Message);

/// <summary>Sizes, rates and times as the Models page and the first run print them.</summary>
public static class ModelText
{
    private const double Gigabyte = 1e9;
    private const double Megabyte = 1e6;

    /// <summary>"1.9 GB", "17 GB", "0.6 GB": one decimal under 10 GB, whole numbers above.</summary>
    public static string Size(long bytes)
    {
        var gb = bytes / Gigabyte;
        return gb >= 10
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(gb):0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{gb:0.0} GB");
    }

    /// <summary>"7.1 of 17 GB".</summary>
    public static string Of(long bytes, long total)
    {
        var done = Size(bytes).Replace(" GB", "", StringComparison.Ordinal);
        return $"{done} of {Size(total)}";
    }

    /// <summary>"48 MB/s".</summary>
    public static string Rate(double bytesPerSecond) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytesPerSecond / Megabyte):0} MB/s");

    /// <summary>"3 m left", "40 s left": whole minutes from a minute up.</summary>
    public static string Left(TimeSpan left) => left.TotalSeconds >= 60
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(left.TotalMinutes):0} m left")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(left.TotalSeconds):0} s left");

    /// <summary>"42% · 7.1 of 17 GB · 48 MB/s · 3 m left".</summary>
    public static string Progress(DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return string.Create(CultureInfo.InvariantCulture,
            $"{progress.Percent}% · {Of(progress.Bytes, progress.Total)} · {Rate(progress.BytesPerSecond)} · {Left(progress.Left)}");
    }

    /// <summary>"about 2 minutes at 48 MB/s": rounded up, so the estimate is not beaten by a slow last minute.</summary>
    public static string Estimate(long bytes, double bytesPerSecond)
    {
        if (bytes <= 0 || bytesPerSecond <= 0)
            return "nothing to download";
        var minutes = (int)Math.Ceiling(bytes / bytesPerSecond / 60);
        return minutes == 1
            ? $"about 1 minute at {Rate(bytesPerSecond)}"
            : string.Create(CultureInfo.InvariantCulture, $"about {minutes} minutes at {Rate(bytesPerSecond)}");
    }

    /// <summary>"cuda:0 · q0 · 180 ms", "cpu · 38 ms".</summary>
    public static string Placement(SlotPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return placement.Queue is { } queue
            ? string.Create(CultureInfo.InvariantCulture, $"{placement.Device} · q{queue} · {placement.P50Milliseconds} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{placement.Device} · {placement.P50Milliseconds} ms");
    }
}
