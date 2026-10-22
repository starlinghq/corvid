using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Fabric;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

/// <summary>Hermes memory files (C-027), read as Hermes' MemoryStore reads them, and staged for review through the writer.</summary>
public sealed class HermesMemoryTests
{
    private const string Session = "s-import";

    [Fact]
    public void Entries_split_on_the_whole_delimiter_and_are_stripped_deduplicated_and_unblocked()
    {
        var raw = "  Sam prefers tea  \n§\n\n§\nUses § as a section mark\n§\nSam prefers tea\n§\n[BLOCKED: MEMORY.md entry contained threat pattern(s): x.]\n§\n   \n";
        var (entries, blocked) = HermesMemory.Parse(raw);
        Assert.Equal(["Sam prefers tea", "Uses § as a section mark"], entries);
        Assert.Equal(1, blocked);
    }

    [Fact]
    public void A_bom_is_stripped_crlf_splits_like_lf_and_a_missing_file_has_no_entries()
    {
        using var dir = new TempDirectory();
        var memories = Path.Combine(dir.Path, "memories");
        Directory.CreateDirectory(memories);
        File.WriteAllBytes(Path.Combine(memories, "MEMORY.md"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("first\r\n§\r\nsecond")]);

        var files = HermesMemory.Read(memories);
        Assert.Equal(["MEMORY.md", "USER.md"], files.Select(file => file.File));
        Assert.Equal(["first", "second"], files[0].Entries);
        Assert.Null(files[0].Problem);
        Assert.False(files[1].Exists);
        Assert.Empty(files[1].Entries);
    }

    [Fact]
    public void A_file_that_is_not_valid_utf8_is_refused_whole()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "USER.md");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes("good entry\n§\nbad "), 0xC3, 0x28]);

        var file = HermesMemory.ReadFile(path);
        Assert.True(file.Exists);
        Assert.Empty(file.Entries);
        Assert.Equal("not valid UTF-8", file.Problem);
    }

    private static MemoryWriter Writer(TempStore store, IExtractor? extractor = null) =>
        new(store.Database, store.Facts, store.Blocks, store.Retriever(), extractor ?? new FakeExtractor(("person", "Sam")),
            new DecisionFabric(DecisionPolicy.Default, Degraded(), store.Log, null), store.Log, time: store.Time);

    [Fact]
    public async Task An_import_is_staged_local_only_with_its_reason_and_never_applied()
    {
        using var store = new TempStore();
        var writer = Writer(store);

        var result = await writer.StageImportAsync(Session, "Sam prefers tea", "import:hermes USER.md", Ct);

        Assert.Equal(MemoryImportStatus.Staged, result.Status);
        var pending = Assert.Single(writer.ListPending());
        Assert.Equal(result.Pending!.PendingId, pending.PendingId);
        Assert.Equal(("import:hermes USER.md", PendingKind.Fact, "Sam prefers tea"), (pending.Reason, pending.Kind, pending.Proposal.Text));
        Assert.True(pending.Proposal.LocalOnly);
        Assert.Null(pending.DecisionId);
        Assert.Equal(["Sam"], pending.Proposal.Entities.Select(entity => entity.Name));
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
        // No gate was asked: the only event is the staged write.
        Assert.Equal([new MemoryWritten(pending.PendingId, "staged")], store.Log.Read(Session, cancellationToken: Ct).Select(e => e.Payload));

        var approved = await writer.ApproveAsync(pending.PendingId, cancellationToken: Ct);
        Assert.Equal("add", approved.Operation);
        Assert.True(Assert.Single(store.Facts.AsOf(store.Time.GetUtcNow())).LocalOnly);
    }

    [Fact]
    public async Task Text_already_pending_or_already_known_is_not_staged_again()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        await writer.StageImportAsync(Session, "Sam prefers tea", "import:hermes USER.md", Ct);

        Assert.Equal(MemoryImportStatus.AlreadyPending, (await writer.StageImportAsync(Session, "sam  prefers TEA", "import:hermes USER.md", Ct)).Status);
        await writer.ApproveAsync(Assert.Single(writer.ListPending()).PendingId, cancellationToken: Ct);
        Assert.Equal(MemoryImportStatus.AlreadyKnown, (await writer.StageImportAsync(Session, "Sam prefers tea", "import:hermes USER.md", Ct)).Status);
        Assert.Empty(writer.ListPending());
    }

    [Fact]
    public async Task Text_the_extractor_refuses_is_not_staged()
    {
        using var store = new TempStore();
        var writer = Writer(store, new RefusingExtractor());
        await Assert.ThrowsAsync<ArgumentException>(() => writer.StageImportAsync(Session, "far too long", "import:hermes MEMORY.md", Ct));
        Assert.Empty(writer.ListPending());
    }

    private sealed class RefusingExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default) =>
            throw new ArgumentException("The text is over the extractor's limit.", nameof(request));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("corvid-hermes-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
