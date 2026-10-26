using System.Collections.Immutable;
using Starlings.Acquisition;
using Starlings.Models.E5;
using Starlings.Models.EmbeddingGemma2;
using Starlings.Models.Gliner2;
using Starlings.Models.Kev;
using Starlings.Models.Qwen35;

namespace Corvid.Models.Starling;

public enum StarlingModel
{
    Qwen35,
    Gliner2,
    EmbeddingGemma2,
    E5,
    Kev08B,
    Kev4B,
}

/// <summary>A model Corvid runs in-process, the slot it serves by default, and the asset lock that pins its files.</summary>
/// <param name="Name">The name shown to the user and accepted on the command line, for example <c>gliner2-base-v1</c>.</param>
/// <param name="StarlingProfile">The Starling registry profile whose contract applies (the CPU profile).</param>
/// <param name="Lock">Repository, commit and every file's size and SHA-256. Shipped inside the Starling package.</param>
/// <param name="HostingRepository">
/// The repository the files are downloaded from when it is not <see cref="ModelAssetLock.Repository"/> (Kev: the lock
/// names the checkpoint, the bytes come from Starling's hosting repository); null when the lock's repository hosts them.
/// </param>
public sealed record StarlingModelInfo(
    StarlingModel Model,
    string Name,
    string Slot,
    string StarlingProfile,
    ModelProfile Profile,
    ModelAssetLock Lock,
    string? HostingRepository = null)
{
    public long DownloadBytes => Lock.TotalSize;
}

/// <summary>The catalogue of models this adapter can acquire and bind.</summary>
public static class StarlingModels
{
    public static StarlingModelInfo Qwen35 { get; } = new(StarlingModel.Qwen35, "qwen3.5-0.8b", ModelSlots.Generator,
        Qwen35Capabilities.StableProfile, StarlingProfiles.Qwen35, Qwen35ModelAssets.GetLock(Qwen35Variant.Fp32x));

    public static StarlingModelInfo Gliner2 { get; } = new(StarlingModel.Gliner2, "gliner2-base-v1", ModelSlots.Extractor,
        Gliner2Capabilities.StableProfile, StarlingProfiles.Gliner2, Gliner2ModelAssets.Lock);

    public static StarlingModelInfo EmbeddingGemma2 { get; } = new(StarlingModel.EmbeddingGemma2, "embeddinggemma-2",
        ModelSlots.TextEmbedder, EmbeddingGemma2Capabilities.StableProfile, StarlingProfiles.EmbeddingGemma2,
        EmbeddingGemma2ModelAssets.Lock);

    /// <summary>The cheap multilingual alternative for <c>embedder.text</c>; not the default binding.</summary>
    public static StarlingModelInfo E5 { get; } = new(StarlingModel.E5, "multilingual-e5-small", ModelSlots.TextEmbedder,
        E5Capabilities.StableProfile, StarlingProfiles.E5, E5ModelAssets.Lock);

    /// <summary>
    /// Kev-0.8B, the default <c>decider</c> (plan §5): an Experimental profile in Starling 0.3.0, about 3.1 GB, hosted at
    /// <c>Starlinghq/kev-0.8b-onnx</c>. The lock names the checkpoint's repository; the files come from the hosting one
    /// (<see cref="KevModelAssets.Kev08BHostingRepository"/>).
    /// </summary>
    public static StarlingModelInfo Kev08B { get; } = new(StarlingModel.Kev08B, KevDecider.NameOf(KevModel.Kev08B), ModelSlots.Decider,
        KevModelProfile.Kev08B.Id, StarlingProfiles.Kev08B, KevModelAssets.Kev08BLock, KevModelAssets.Kev08BHostingRepository);

    /// <summary>Kev-4B, the optional second tier for gates below their floor (§5.5): about 17 GB, and 18 GB resident on the CPU.</summary>
    public static StarlingModelInfo Kev4B { get; } = new(StarlingModel.Kev4B, KevDecider.NameOf(KevModel.Kev4B), ModelSlots.Decider,
        KevModelProfile.Kev4B.Id, StarlingProfiles.Kev4B, KevModelAssets.Kev4BLock, KevModelAssets.Kev4BHostingRepository);

    public static ImmutableArray<StarlingModelInfo> All { get; } = [Qwen35, Gliner2, EmbeddingGemma2, E5, Kev08B, Kev4B];

    public static StarlingModelInfo Get(StarlingModel model) => model switch
    {
        StarlingModel.Qwen35 => Qwen35,
        StarlingModel.Gliner2 => Gliner2,
        StarlingModel.EmbeddingGemma2 => EmbeddingGemma2,
        StarlingModel.E5 => E5,
        StarlingModel.Kev08B => Kev08B,
        StarlingModel.Kev4B => Kev4B,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown Starling model."),
    };

    /// <summary>Looks a model up by <see cref="StarlingModelInfo.Name"/>, ignoring case.</summary>
    public static bool TryFind(string name, out StarlingModelInfo model)
    {
        model = All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return model is not null;
    }
}
