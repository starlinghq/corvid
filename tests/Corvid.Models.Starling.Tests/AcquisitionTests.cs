using Starlings.Acquisition;

namespace Corvid.Models.Starling.Tests;

/// <summary>Offline resolution against a temporary cache. Nothing here touches the network.</summary>
public sealed class AcquisitionTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "corvid-starling-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }

    [Fact]
    public void An_empty_cache_reports_every_model_missing_with_its_files()
    {
        var store = new StarlingModelStore(_cache);

        var statuses = store.CheckAll();

        Assert.Equal(Path.GetFullPath(_cache), Path.GetFullPath(store.CacheDirectory));
        Assert.All(statuses, status =>
        {
            Assert.Equal(StarlingModelState.Missing, status.State);
            Assert.False(status.IsReady);
            Assert.Equal(status.Model.Lock.Files.Select(f => f.Path).Order(), status.MissingPaths.Order());
            Assert.StartsWith(Path.GetFullPath(_cache), Path.GetFullPath(status.SnapshotDirectory), StringComparison.Ordinal);
        });
        Assert.False(Directory.Exists(Path.Combine(_cache, ".downloads")));
    }

    [Fact]
    public void Opening_a_missing_model_says_what_is_missing_and_how_to_get_it()
    {
        var store = new StarlingModelStore(_cache);

        var exception = Assert.Throws<StarlingModelUnavailableException>(() => store.OpenGliner2());

        Assert.Equal(StarlingModelState.Missing, exception.Status.State);
        Assert.IsType<ModelAssetMissingException>(exception.InnerException);
        Assert.Contains("'gliner2-base-v1' (extractor)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not available offline", exception.Message, StringComparison.Ordinal);
        Assert.Contains(StarlingModels.Gliner2.Lock.Files[0].Path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("MiB", exception.Message, StringComparison.Ordinal);
        Assert.Contains("STARLING_MODELS", exception.Message, StringComparison.Ordinal);
        Assert.Throws<StarlingModelUnavailableException>(() => store.OpenQwen35());
        Assert.Throws<StarlingModelUnavailableException>(() => store.OpenEmbeddingGemma2());
        Assert.Throws<StarlingModelUnavailableException>(() => store.OpenE5());
    }

    [Fact]
    public void A_wrong_size_file_is_corrupt_and_names_the_file()
    {
        var store = new StarlingModelStore(_cache);
        var directory = Seed(store, StarlingModel.E5);
        var victim = StarlingModels.E5.Lock.Files[0];
        File.WriteAllText(Path.Combine(directory, victim.Path), "not the model");

        var status = store.Check(StarlingModel.E5, CacheVerification.Size);

        Assert.Equal(StarlingModelState.Corrupt, status.State);
        Assert.Contains(victim.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), status.Problem, StringComparison.Ordinal);
        Assert.Contains("Download it again", status.Describe(), StringComparison.Ordinal);
        var exception = Assert.Throws<StarlingModelUnavailableException>(() => store.OpenE5());
        Assert.IsType<ModelAssetIntegrityException>(exception.InnerException);
    }

    [Fact]
    public void Size_verification_is_cheap_and_digest_verification_catches_same_size_corruption()
    {
        var store = new StarlingModelStore(_cache);
        Seed(store, StarlingModel.E5);

        // Startup trusts sizes (downloads were hashed); a doctor check hashes every byte.
        Assert.Equal(StarlingModelState.Ready, store.Check(StarlingModel.E5, CacheVerification.Size).State);
        var files = store.OpenE5();
        Assert.True(File.Exists(files.ModelPath));
        Assert.Equal(StarlingModelState.Corrupt, store.Check(StarlingModel.E5).State);
        Assert.Equal(StarlingModelState.Missing, store.Check(StarlingModel.Gliner2, CacheVerification.Size).State);
    }

    // Every file of the lock at its locked size, as sparse zeros: right sizes, wrong digests.
    private static string Seed(StarlingModelStore store, StarlingModel model)
    {
        var directory = store.GetSnapshotDirectory(model);
        foreach (var file in StarlingModels.Get(model).Lock.Files)
        {
            var path = Path.Combine(directory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            stream.SetLength(file.Size);
        }

        return directory;
    }
}
