using Corvid.Abstractions.Events;
using Corvid.Fabric;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

/// <summary>Plan §9.5's fact dedupe: a later fact saying what an earlier one says, its ending staged for review.</summary>
public sealed class DedupeTests
{
    private static MemoryWriter Writer(TempStore store) =>
        new(store.Database, store.Facts, store.Blocks, store.Retriever(), new FakeExtractor(),
            new DecisionFabric(DecisionPolicy.Default, Degraded(), store.Log, null), store.Log, time: store.Time);

    private static List<SessionEvent> Journey(TempStore store) =>
        [.. store.Log.Read(MemoryJobs.Session, cancellationToken: Ct).Select(e => e.Payload)];

    [Fact]
    public async Task A_later_duplicate_is_staged_to_end_and_approving_ends_only_it()
    {
        using var store = new TempStore();
        var first = store.Facts.Add(new FactInput("Sam lives in Leeds.", [Person("Sam"), Place("Leeds")]));
        store.Time.Advance(TimeSpan.FromDays(1));
        var again = store.Facts.Add(new FactInput("sam lives  in Leeds", [Person("Sam")]));
        store.Facts.Add(new FactInput("Sam lives in York"));
        var writer = Writer(store);

        var staged = Assert.Single(new FactDeduplicator(store.Facts, writer, store.Log, store.Time).StageDuplicates());

        Assert.Equal((MemoryOperations.Invalidate, again.FactId, "sam lives  in Leeds"), (staged.Proposal.Operation, staged.Proposal.TargetFactId, staged.Proposal.Text));
        Assert.Equal([first.FactId], staged.Proposal.NeighbourIds);
        Assert.Equal($"duplicate: says what {first.FactId} says", staged.Reason);
        // Nothing ended yet.
        Assert.Equal(3, store.Facts.CountCurrent());
        Assert.Equal([new MemoryWritten(staged.PendingId, "staged"),
            new MemoryJobRan("dedupe_facts", again.FactId, "staged", $"says what {first.FactId} says", "idle", staged.PendingId)], Journey(store));

        await writer.ApproveAsync(staged.PendingId, cancellationToken: Ct);

        Assert.False(store.Facts.Get(again.FactId)!.IsCurrent);
        Assert.True(store.Facts.Get(first.FactId)!.IsCurrent);
        Assert.Empty(new FactDeduplicator(store.Facts, writer, store.Log, store.Time).StageDuplicates());
    }

    [Fact]
    public async Task Review_L3_approving_a_duplicates_ending_after_the_kept_fact_ended_is_refused_and_rejects_it()
    {
        using var store = new TempStore();
        var kept = store.Facts.Add(new FactInput("Prefers tea"));
        store.Time.Advance(TimeSpan.FromHours(1));
        var again = store.Facts.Add(new FactInput("prefers tea."));
        var writer = Writer(store);
        var staged = Assert.Single(new FactDeduplicator(store.Facts, writer, store.Log, store.Time).StageDuplicates());
        // The kept fact ends before anyone reviews the duplicate: ending the duplicate now would lose the fact.
        store.Facts.Invalidate(kept.FactId);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApproveAsync(staged.PendingId, cancellationToken: Ct));

        Assert.Contains("no longer current", refused.Message, StringComparison.Ordinal);
        Assert.True(store.Facts.Get(again.FactId)!.IsCurrent);
        var resolved = writer.GetPending(staged.PendingId)!;
        Assert.Equal(PendingStatus.Rejected, resolved.Status);
        Assert.StartsWith(MemoryWriter.StaleReason, resolved.Resolution, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_ending_is_not_proposed_again_and_a_waiting_one_not_twice()
    {
        using var store = new TempStore();
        store.Facts.Add(new FactInput("Prefers tea"));
        store.Time.Advance(TimeSpan.FromHours(1));
        store.Facts.Add(new FactInput("prefers tea."));
        var writer = Writer(store);
        var job = new FactDeduplicator(store.Facts, writer, store.Log, store.Time);

        var staged = Assert.Single(job.StageDuplicates());
        Assert.Empty(job.StageDuplicates());
        writer.Reject(staged.PendingId, cancellationToken: Ct);

        Assert.Empty(job.StageDuplicates());
        Assert.Equal(2, store.Facts.CountCurrent());
    }

    [Fact]
    public void Facts_that_differ_in_relation_or_privacy_are_not_duplicates()
    {
        using var store = new TempStore();
        store.Facts.Add(new FactInput("Sam works at Acme", Relation: new RelationInput(Person("Sam"), "works_at", new EntityRef("organization", "Acme"))));
        store.Facts.Add(new FactInput("Sam works at Acme"));
        store.Facts.Add(new FactInput("The door code is private", LocalOnly: true));
        store.Facts.Add(new FactInput("The door code is private"));

        Assert.Empty(new FactDeduplicator(store.Facts, Writer(store), store.Log, store.Time).StageDuplicates());
    }

    [Fact]
    public async Task The_sleep_time_pass_runs_the_dedupe_after_the_blocks()
    {
        using var store = new TempStore();
        store.Facts.Add(new FactInput("Prefers tea"));
        store.Facts.Add(new FactInput("Prefers tea"));
        var writer = Writer(store);
        var job = new BlockConsolidator(store.Blocks, writer, new NoChatClient(), store.Log, _ => new NoScope(), time: store.Time)
        {
            Deduplicator = new FactDeduplicator(store.Facts, writer, store.Log, store.Time),
        };

        Assert.Empty(await job.RunOnceAsync(cancellationToken: Ct));

        var pending = Assert.Single(writer.ListPending());
        Assert.Equal((PendingKind.Fact, MemoryOperations.Invalidate), (pending.Kind, pending.Proposal.Operation));
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
