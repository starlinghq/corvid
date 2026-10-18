using System.Collections.Immutable;
using System.Globalization;
using Starlings.Acquisition;
using Starlings.Models.E5;
using Starlings.Models.EmbeddingGemma2;
using Starlings.Models.Gliner2;
using Starlings.Models.Qwen35;

namespace Corvid.Models.Starling;

public enum StarlingModelState
{
    /// <summary>Every file is in the cache and passed the requested verification.</summary>
    Ready,
    /// <summary>One or more files are not in the cache; an explicit download fetches them.</summary>
    Missing,
    /// <summary>A cached file has the wrong size or digest; a download replaces it.</summary>
    Corrupt,
    /// <summary>The cache could not be read (permissions, I/O errors).</summary>
    Unreadable,
}

/// <summary>What offline resolution found for one model. Suitable for <c>corvid doctor</c>.</summary>
/// <param name="MissingPaths">Paths inside the repository that are not cached (state <see cref="StarlingModelState.Missing"/>).</param>
/// <param name="Problem">The verification or I/O failure, for <see cref="StarlingModelState.Corrupt"/> and <see cref="StarlingModelState.Unreadable"/>.</param>
public sealed record StarlingModelStatus(
    StarlingModelInfo Model,
    StarlingModelState State,
    string SnapshotDirectory,
    ImmutableArray<string> MissingPaths,
    string? Problem = null)
{
    public bool IsReady => State == StarlingModelState.Ready;

    /// <summary>One paragraph saying what is wrong and how to fix it.</summary>
    public string Describe()
    {
        var model = $"Model '{Model.Name}' ({Model.Slot})";
        var download = $"Download it explicitly ({StarlingModelStore.FormatBytes(Model.DownloadBytes)} for {Model.Lock.Files.Count} files from " +
            $"{Model.Lock.Repository}@{Model.Lock.Revision[..8]}), or set {ModelAcquisitionOptions.CacheDirectoryEnvironmentVariable} " +
            "to a model cache that has it.";
        return State switch
        {
            StarlingModelState.Ready => $"{model} is ready in {SnapshotDirectory}.",
            StarlingModelState.Missing => $"{model} is not available offline: {MissingPaths.Length} of {Model.Lock.Files.Count} files " +
                $"missing from {SnapshotDirectory} ({string.Join(", ", MissingPaths)}). {download}",
            StarlingModelState.Corrupt => $"{model} failed verification in {SnapshotDirectory}: {Problem} Download it again to replace the file.",
            _ => $"{model} cannot be read from {SnapshotDirectory}: {Problem}",
        };
    }
}

/// <summary>Thrown at startup when a model a slot needs is not usable offline. Corvid never downloads implicitly.</summary>
public sealed class StarlingModelUnavailableException(StarlingModelStatus status, Exception? innerException = null)
    : InvalidOperationException(status.Describe(), innerException)
{
    public StarlingModelStatus Status { get; } = status;
}

/// <summary>
/// Explicit acquisition and offline resolution of the Starling models (plan §1, D-3). Checking and opening never touch
/// the network; only <see cref="DownloadAsync"/> does, and fresh downloads are always verified by size and SHA-256.
/// </summary>
/// <remarks>
/// The cache root is <paramref name="cacheDirectory"/>, else <c>$STARLING_MODELS</c>, else the user cache directory
/// (<c>~/.cache/starlings/models</c> on Linux), shared with every other Starling consumer.
/// </remarks>
public sealed class StarlingModelStore(string? cacheDirectory = null, HttpClient? httpClient = null)
{
    private readonly ModelAcquisitionOptions _options = new() { CacheDirectory = cacheDirectory };

    public string CacheDirectory => _options.ResolveCacheDirectory();

    public string GetSnapshotDirectory(StarlingModel model)
    {
        using var store = Store(CacheVerification.Size);
        return store.GetSnapshotDirectory(StarlingModels.Get(model).Lock);
    }

    /// <summary>
    /// Resolves a model offline and reports its state instead of throwing. <see cref="CacheVerification.Sha256"/>
    /// reads every byte (about 1 s per 500 MB); use it for a doctor check, not on every start.
    /// </summary>
    public StarlingModelStatus Check(StarlingModel model, CacheVerification verification = CacheVerification.Sha256) =>
        Resolve(StarlingModels.Get(model), verification).Status;

    public ImmutableArray<StarlingModelStatus> CheckAll(CacheVerification verification = CacheVerification.Sha256) =>
        [.. StarlingModels.All.Select(m => Resolve(m, verification).Status)];

    /// <summary>Downloads whatever is missing or invalid, then verifies every file. The only call that uses the network.</summary>
    /// <exception cref="ModelAcquisitionException">Access denied, not found, integrity or transfer failures, each with its own subtype.</exception>
    public async Task<StarlingModelStatus> DownloadAsync(StarlingModel model, IProgress<ModelAssetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = StarlingModels.Get(model);
        using var store = Store(CacheVerification.Sha256);
        var snapshot = await store.DownloadAsync(info.Lock, progress, cancellationToken).ConfigureAwait(false);
        return new StarlingModelStatus(info, StarlingModelState.Ready, snapshot.Directory, []);
    }

    // Opening defaults to size-only verification so startup does not hash gigabytes; downloads were hashed, and
    // Check(Sha256) finds same-size corruption.
    public Qwen35ModelFiles OpenQwen35(CacheVerification verification = CacheVerification.Size) =>
        Qwen35ModelFiles.From(Qwen35Variant.Fp32x, Open(StarlingModels.Qwen35, verification));

    public Gliner2ModelFiles OpenGliner2(CacheVerification verification = CacheVerification.Size) =>
        Gliner2ModelFiles.From(Open(StarlingModels.Gliner2, verification));

    public EmbeddingGemma2ModelFiles OpenEmbeddingGemma2(CacheVerification verification = CacheVerification.Size) =>
        EmbeddingGemma2ModelFiles.From(Open(StarlingModels.EmbeddingGemma2, verification));

    public E5ModelFiles OpenE5(CacheVerification verification = CacheVerification.Size) =>
        E5ModelFiles.From(Open(StarlingModels.E5, verification));

    private ModelSnapshot Open(StarlingModelInfo model, CacheVerification verification)
    {
        var (status, snapshot, failure) = Resolve(model, verification);
        return snapshot ?? throw new StarlingModelUnavailableException(status, failure);
    }

    private (StarlingModelStatus Status, ModelSnapshot? Snapshot, Exception? Failure) Resolve(StarlingModelInfo model,
        CacheVerification verification)
    {
        using var store = Store(verification);
        var directory = store.GetSnapshotDirectory(model.Lock);
        try
        {
            var snapshot = store.OpenLocal(model.Lock);
            return (new StarlingModelStatus(model, StarlingModelState.Ready, snapshot.Directory, []), snapshot, null);
        }
        catch (ModelAssetMissingException exception)
        {
            return (new StarlingModelStatus(model, StarlingModelState.Missing, exception.SnapshotDirectory ?? directory,
                [.. exception.MissingPaths ?? []]), null, exception);
        }
        catch (ModelAssetIntegrityException exception)
        {
            var problem = $"{exception.FilePath} has {exception.ActualSize.ToString(CultureInfo.InvariantCulture)} bytes" +
                (exception.ActualSha256 is { } actual ? $", SHA-256 {actual}" : "") +
                $"; the lock expects {exception.ExpectedSize.ToString(CultureInfo.InvariantCulture)} bytes" +
                (exception.ExpectedSha256 is { } expected ? $", SHA-256 {expected}." : ".");
            return (new StarlingModelStatus(model, StarlingModelState.Corrupt, directory, [], problem), null, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (new StarlingModelStatus(model, StarlingModelState.Unreadable, directory, [], exception.Message), null, exception);
        }
    }

    private ModelAssetStore Store(CacheVerification verification) =>
        new(new ModelAcquisitionOptions { CacheDirectory = _options.CacheDirectory, Verification = verification }, httpClient);

    internal static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0 GiB", CultureInfo.InvariantCulture)
        : (bytes / (double)(1L << 20)).ToString("0 MiB", CultureInfo.InvariantCulture);
}
