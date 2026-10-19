using Corvid.Memory;

namespace Corvid.Kernel.Tests;

/// <summary>A memory database in its own temporary directory, for the egress write path and the re-embed job.</summary>
internal sealed class MemoryStore : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-turn-memory-" + Guid.NewGuid().ToString("N"));

    public MemoryStore()
    {
        Database = new MemoryDatabase(Path.Combine(_root, "memory.db"));
        Facts = new FactGraph(Database);
        Episodes = new EpisodicIndex(Database);
        Blocks = new CoreBlockStore(Database);
        Index = new SqliteVectorIndex(Database);
    }

    public MemoryDatabase Database { get; }

    public FactGraph Facts { get; }

    public EpisodicIndex Episodes { get; }

    public CoreBlockStore Blocks { get; }

    public SqliteVectorIndex Index { get; }

    public MemoryWriter Writer(TurnHarness harness) =>
        new(Database, Facts, Blocks, new HybridRetriever(Episodes, Facts), new FakeExtractor(0.9f, ("person", "Ana")), harness.Fabric, harness.Log);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
