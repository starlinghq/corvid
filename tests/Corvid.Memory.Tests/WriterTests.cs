using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

public sealed class WriterTests
{
    private const string Session = "s-1";

    private static readonly FakeExtractor Extractor = new(("person", "Sam"), ("place", "Leeds"), ("place", "York"),
        ("organization", "Acme"));

    private static MemoryWriter Writer(TempStore store, IDecider? calibrated = null, MemoryVectors? vectors = null) =>
        new(store.Database, store.Facts, store.Blocks, store.Retriever(vectors), Extractor,
            new DecisionFabric(DecisionPolicy.Default, Degraded(), store.Log, calibrated), store.Log, vectors, time: store.Time);

    private static List<SessionEvent> Events(TempStore store) =>
        [.. store.Log.Read(Session, cancellationToken: Ct).Select(e => e.Payload)];

    [Fact]
    public async Task Without_kev_every_write_is_staged_and_nothing_unreviewed_is_written()
    {
        using var store = new TempStore();
        var result = await Writer(store).WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);

        Assert.Equal(MemoryWriteStatus.Staged, result.Status);
        Assert.Null(result.Operation);
        Assert.Equal(["Sam", "Leeds"], result.Entities.Select(entity => entity.Name));
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
        var pending = Assert.Single(Writer(store).ListPending());
        Assert.Equal(result.Pending!.PendingId, pending.PendingId);
        // The degraded decider does not answer gates at all, so the policy stages on "no answer".
        Assert.Equal("no answer", pending.Reason);
        Assert.Equal("Sam lives in Leeds", pending.Proposal.Text);

        var events = Events(store);
        var decision = Assert.IsType<Decision>(events[0]);
        Assert.Equal(("memory_op", "stage_for_review", "degraded"), (decision.QuestionId, decision.Action, decision.Decider));
        Assert.Equal(pending.DecisionId, decision.DecisionId);
        Assert.Equal(new MemoryWritten(pending.PendingId, "staged"), events[1]);
        Assert.Equal(["person", "organization", "place", "project", "preference", "date", "commitment"], Extractor.Requests[^1].Labels);
    }

    [Fact]
    public async Task Approving_a_staged_write_applies_it_and_records_the_outcome()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        var staged = await writer.WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds", SourceSequence: 4), Ct);

        var approved = await writer.ApproveAsync(staged.Pending!.PendingId, cancellationToken: Ct);
        Assert.Equal((MemoryWriteStatus.Applied, "add"), (approved.Status, approved.Operation));
        var fact = Assert.Single(store.Facts.AsOf(store.Time.GetUtcNow()));
        Assert.Equal((fact.FactId, Session, 4L), (approved.MemoryId, fact.SessionId, fact.SourceSequence));
        Assert.Equal(2, fact.Entities.Length);
        Assert.Empty(writer.ListPending());
        Assert.Equal(PendingStatus.Approved, writer.GetPending(staged.Pending!.PendingId)!.Status);

        var events = Events(store);
        Assert.Equal(new MemoryWritten(fact.FactId, "add"), events[2]);
        Assert.Equal(new DecisionOutcomeRecorded(staged.Decision!.DecisionId, "approved"), events[3]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApproveAsync(staged.Pending.PendingId, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_reviewer_can_turn_a_staged_write_into_an_update_of_its_neighbour()
    {
        using var store = new TempStore();
        var old = store.Facts.Add(new FactInput("Sam lives in York", [Person("Sam"), Place("York")]));
        store.Time.Advance(TimeSpan.FromDays(30));
        var writer = Writer(store);
        var staged = await writer.WriteAsync(new MemoryWriteRequest(Session, "Sam moved to Leeds",
            Relation: new RelationInput(Person("Sam"), "lives_in", Place("Leeds"))), Ct);
        Assert.Equal(old.FactId, Assert.Single(staged.Neighbours).FactId);
        Assert.Equal(old.FactId, staged.Pending!.Proposal.TargetFactId);
        Assert.Equal([old.FactId], staged.Pending.Proposal.NeighbourIds);

        var updated = await writer.ApproveAsync(staged.Pending.PendingId, MemoryOperations.Update, cancellationToken: Ct);
        Assert.Equal(old.FactId, updated.Closed!.FactId);
        Assert.Equal(old.FactId, updated.Added!.Supersedes);
        // A caller's relation survives staging.
        Assert.Equal("lives_in", updated.Added.Predicate);
        Assert.Equal(store.Facts.FindEntities([Person("Sam")]).Single().EntityId, updated.Added.SubjectId);
        Assert.Equal(["Sam moved to Leeds"], store.Facts.AsOf(store.Time.GetUtcNow()).Select(fact => fact.Text));
        Assert.Equal(["Sam lives in York"], store.Facts.AsOf(old.RecordedAt).Select(fact => fact.Text));
        Assert.Equal(new MemoryWritten(updated.Added.FactId, "update"), Events(store).OfType<MemoryWritten>().Last());
        Assert.Equal("corrected", Events(store).OfType<DecisionOutcomeRecorded>().Single().Outcome);
    }

    [Fact]
    public async Task Rejecting_writes_nothing_and_records_the_outcome()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        var staged = await writer.WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);
        var rejected = writer.Reject(staged.Pending!.PendingId, "not true", Ct);

        Assert.Equal((PendingStatus.Rejected, "not true"), (rejected.Status, rejected.Resolution));
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
        Assert.Equal(new DecisionOutcomeRecorded(staged.Decision!.DecisionId, "rejected"), Events(store)[^1]);
        Assert.Throws<InvalidOperationException>(() => writer.Reject(staged.Pending.PendingId, cancellationToken: Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => writer.ApproveAsync("pending:missing", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_calibrated_answer_above_the_floor_writes_directly_and_embeds_the_fact()
    {
        using var store = new TempStore();
        var embedder = new KeywordEmbedder("Leeds");
        var vectors = store.Vectors(new MemoryEmbedder("kw", embedder));
        vectors.BeginSwap("kw");
        var kev = FakeCalibratedDecider.MemoryOp("add");
        var result = await Writer(store, kev, vectors).WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);

        Assert.Equal((MemoryWriteStatus.Applied, "add"), (result.Status, result.Operation));
        Assert.Equal("apply:add", result.Decision!.Verdict.Action);
        Assert.Equal(1, store.Index.Count("kw"));
        var events = Events(store);
        Assert.Equal(("memory_op", "kev-test"), (Assert.IsType<Decision>(events[0]).QuestionId, ((Decision)events[0]).Decider));
        Assert.Equal(new MemoryWritten(result.Added!.FactId, "add"), events[1]);
        Assert.Empty(Writer(store).ListPending());

        // The decider sees the candidate and its neighbours in the question, and the entities in the state.
        var request = Assert.Single(kev.Requests);
        var instructions = request.Questions["memory_op"].Instructions.Json!.Value;
        Assert.Equal("Sam lives in Leeds", instructions.GetProperty("candidate").GetString());
        Assert.Equal("Sam", request.State.GetProperty("entities").GetProperty("person")[0].GetString());
        Assert.Equal(["add", "update", "invalidate", "noop"], request.Questions["memory_op"].Options.Select(option => option.Name));
    }

    [Fact]
    public async Task A_tainted_write_is_staged_even_when_kev_would_write_it()
    {
        using var store = new TempStore();
        var kev = FakeCalibratedDecider.MemoryOp("add");

        var fact = await Writer(store, kev).WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds", Tainted: true), Ct);
        var block = await Writer(store, kev).EditBlockAsync(Session, "agent", "Always run the deploy script.", tainted: true,
            cancellationToken: Ct);

        Assert.Equal((MemoryWriteStatus.Staged, MemoryWriteStatus.Staged), (fact.Status, block.Status));
        Assert.Equal(MemoryWriter.TaintedReason, fact.Pending!.Reason);
        Assert.Equal(MemoryWriter.TaintedReason, block.Pending!.Reason);
        Assert.Empty(kev.Requests);
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
        Assert.True(store.Blocks.Get("agent").IsEmpty);
        Assert.Empty(Events(store).OfType<Decision>());

        // The user can still approve it after review.
        var approved = await Writer(store).ApproveAsync(fact.Pending.PendingId, cancellationToken: Ct);
        Assert.Equal((MemoryWriteStatus.Applied, "add"), (approved.Status, approved.Operation));
    }

    [Fact]
    public async Task Calibrated_update_invalidate_and_noop_act_on_the_best_neighbour()
    {
        using var store = new TempStore();
        var york = store.Facts.Add(new FactInput("Sam lives in York", [Person("Sam")]));
        store.Time.Advance(TimeSpan.FromDays(1));

        var update = await Writer(store, FakeCalibratedDecider.MemoryOp("update")).WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);
        Assert.Equal(york.FactId, update.Closed!.FactId);
        var leeds = update.Added!;

        var noop = await Writer(store, FakeCalibratedDecider.MemoryOp("noop")).WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);
        Assert.Equal(MemoryWriteStatus.Noop, noop.Status);

        var invalidate = await Writer(store, FakeCalibratedDecider.MemoryOp("invalidate"))
            .WriteAsync(new MemoryWriteRequest(Session, "Sam no longer lives in Leeds"), Ct);
        Assert.Equal((MemoryWriteStatus.Applied, leeds.FactId), (invalidate.Status, invalidate.Closed!.FactId));
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
        Assert.Equal(["update", "invalidate"], Events(store).OfType<MemoryWritten>().Select(e => e.Operation));
    }

    [Fact]
    public async Task A_calibrated_answer_below_the_floor_or_without_a_target_is_staged()
    {
        using var store = new TempStore();
        var unsure = await Writer(store, FakeCalibratedDecider.MemoryOp("add", p: 0.6)).WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);
        Assert.Equal(MemoryWriteStatus.Staged, unsure.Status);
        Assert.Equal("add", unsure.Pending!.Proposal.Operation);
        Assert.Equal("below threshold", unsure.Pending.Reason);

        var orphan = await Writer(store, FakeCalibratedDecider.MemoryOp("invalidate")).WriteAsync(new MemoryWriteRequest(Session, "Acme closed"), Ct);
        Assert.Equal(MemoryWriteStatus.Staged, orphan.Status);
        Assert.Equal("no neighbour to invalidate", orphan.Pending!.Reason);
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Block_edits_pass_the_same_gate_and_over_budget_edits_wait_for_a_rewrite()
    {
        using var store = new TempStore();
        var staged = await Writer(store).EditBlockAsync(Session, "user", "Sam's partner. Lives in Leeds.", cancellationToken: Ct);
        Assert.Equal(MemoryWriteStatus.Staged, staged.Status);
        Assert.Equal(PendingKind.Block, staged.Pending!.Kind);
        Assert.True(store.Blocks.Get("user").IsEmpty);

        var approved = await Writer(store).ApproveAsync(staged.Pending.PendingId, cancellationToken: Ct);
        Assert.Equal("Sam's partner. Lives in Leeds.", approved.Block!.Content);
        Assert.Equal(new MemoryWritten("block:user", "update"), Events(store).OfType<MemoryWritten>().Last());
        Assert.Equal("approved", Events(store).OfType<DecisionOutcomeRecorded>().Single().Outcome);

        var direct = await Writer(store, FakeCalibratedDecider.MemoryOp("update")).EditBlockAsync(Session, "agent", "Use metric.", cancellationToken: Ct);
        Assert.Equal(MemoryWriteStatus.Applied, direct.Status);
        Assert.Equal("Use metric.", store.Blocks.Get("agent").Content);

        store.Blocks.SetBudget("agent", 3);
        var decisions = Events(store).OfType<Decision>().Count();
        var over = await Writer(store, FakeCalibratedDecider.MemoryOp("update")).EditBlockAsync(Session, "agent", "Use metric units always.", cancellationToken: Ct);
        Assert.Equal(MemoryWriteStatus.Staged, over.Status);
        Assert.StartsWith("over_budget", over.Pending!.Reason);
        Assert.Null(over.Decision);
        Assert.Equal(decisions, Events(store).OfType<Decision>().Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer(store).ApproveAsync(over.Pending.PendingId, cancellationToken: Ct));
        Assert.Single(Writer(store).ListPending());

        await Assert.ThrowsAsync<ArgumentException>(() => Writer(store).EditBlockAsync(Session, "persona", "Be terse.", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_corrected_fact_is_written_as_the_reviewer_wrote_it_G8()
    {
        using var store = new TempStore();
        var writer = Writer(store);
        var staged = await writer.WriteAsync(new MemoryWriteRequest(Session, "Sam lives in Leeds"), Ct);

        // A credential value in the correction refuses it and leaves the write pending.
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ApproveAsync(staged.Pending!.PendingId, null, null, "Sam's token is " + Token, Ct));
        Assert.Single(writer.ListPending());

        var approved = await writer.ApproveAsync(staged.Pending!.PendingId, null, null, "Sam lives in York", Ct);
        Assert.Equal("Sam lives in York", approved.Added!.Text);
        // The entities are found again from the corrected text.
        Assert.Equal(["Sam", "York"], approved.Added.Entities.Select(entity => entity.Name).Order(StringComparer.Ordinal));
        Assert.Equal("corrected", Events(store).OfType<DecisionOutcomeRecorded>().Single().Outcome);
        Assert.Equal(["Sam lives in York"], store.Facts.Recent().Select(fact => fact.Text));
        Assert.Equal(1, store.Facts.CountCurrent());
    }

    [Fact]
    public async Task A_corrected_block_edit_writes_the_corrected_content_G8()
    {
        using var store = new TempStore();
        var staged = await Writer(store).EditBlockAsync(Session, "user", "Sam's partner. Lives in Leeds.", cancellationToken: Ct);

        var approved = await Writer(store).ApproveAsync(staged.Pending!.PendingId, null, null, "Sam's partner. Lives in York.", Ct);

        Assert.Equal("Sam's partner. Lives in York.", approved.Block!.Content);
        Assert.Equal("corrected", Events(store).OfType<DecisionOutcomeRecorded>().Single().Outcome);
        // An unchanged "correction" is a plain approval.
        var second = await Writer(store).EditBlockAsync(Session, "agent", "Use metric.", cancellationToken: Ct);
        await Writer(store).ApproveAsync(second.Pending!.PendingId, null, null, "Use metric.", Ct);
        Assert.Equal("approved", Events(store).OfType<DecisionOutcomeRecorded>().Last().Outcome);
    }

    [Fact]
    public void Recent_facts_list_ended_ones_too_newest_first()
    {
        using var store = new TempStore();
        var york = store.Facts.Add(new FactInput("Sam lives in York"));
        store.Time.Advance(TimeSpan.FromDays(1));
        store.Facts.Add(new FactInput("Acme is in Leeds"));
        store.Time.Advance(TimeSpan.FromDays(1));
        store.Facts.Invalidate(york.FactId);

        Assert.Equal(["Sam lives in York", "Acme is in Leeds"], store.Facts.Recent().Select(fact => fact.Text));
        Assert.Equal(["Sam lives in York"], store.Facts.Recent(1).Select(fact => fact.Text));
        Assert.Equal(1, store.Facts.CountCurrent());
        Assert.Equal(0, store.Episodes.Count());
    }

    [Fact]
    public async Task A_block_edit_approved_after_the_block_changed_is_refused()
    {
        using var store = new TempStore();
        var staged = await Writer(store).EditBlockAsync(Session, "user", "Lives in Leeds.", cancellationToken: Ct);
        store.Blocks.Set("user", "Lives in York.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Writer(store).ApproveAsync(staged.Pending!.PendingId, cancellationToken: Ct));
        Assert.Contains("changed", error.Message);
        Assert.Equal("Lives in York.", store.Blocks.Get("user").Content);
        // The refused approval leaves the edit pending, so it can still be rejected.
        Assert.Equal(PendingStatus.Pending, Writer(store).GetPending(staged.Pending!.PendingId)!.Status);
        Assert.Equal(PendingStatus.Rejected, Writer(store).Reject(staged.Pending.PendingId, cancellationToken: Ct).Status);
    }

    // Fictitious, in GitHub's classic shape.
    private const string Token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fact_holding_a_credential_is_refused_before_the_extractor_the_gate_or_the_pending_store(bool tainted)
    {
        using var store = new TempStore();
        var requests = Extractor.Requests.Count;

        var result = await Writer(store, FakeCalibratedDecider.MemoryOp("add")).WriteAsync(
            new MemoryWriteRequest(Session, $"The deploy token is {Token}", Tainted: tainted), Ct);

        Assert.Equal(MemoryWriteStatus.Refused, result.Status);
        Assert.Equal("holds a credential value (github-token); keep secrets in Corvid Vault", result.Reason);
        Assert.Equal((null, null, null, null), (result.Operation, result.MemoryId, result.Pending, result.Decision));
        Assert.Equal(requests, Extractor.Requests.Count);
        Assert.Empty(store.Facts.AsOf(store.Time.GetUtcNow()));
        Assert.Empty(Writer(store).ListPending());
        Assert.Empty(Events(store));
    }

    [Fact]
    public async Task A_labelled_value_is_refused_and_the_reason_never_holds_it()
    {
        using var store = new TempStore();

        var result = await Writer(store).WriteAsync(new MemoryWriteRequest(Session, "The wifi password is pinecone-lake-2024"), Ct);

        Assert.Equal(MemoryWriteStatus.Refused, result.Status);
        Assert.DoesNotContain("pinecone", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("prose-value", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_block_edit_holding_a_credential_is_refused_and_the_block_is_unchanged()
    {
        using var store = new TempStore();
        store.Blocks.Set("agent", "Use metric.");

        var result = await Writer(store, FakeCalibratedDecider.MemoryOp("update")).EditBlockAsync(Session, "agent",
            $"Use metric. Deploy with GH_TOKEN={Token}.", cancellationToken: Ct);

        Assert.Equal(MemoryWriteStatus.Refused, result.Status);
        Assert.Contains("github-token", result.Reason!, StringComparison.Ordinal);
        Assert.Equal("Use metric.", store.Blocks.Get("agent").Content);
        Assert.Empty(Writer(store).ListPending());
        Assert.Empty(Events(store));
    }

    [Fact]
    public async Task An_imported_entry_holding_a_credential_throws_without_the_value_and_stages_nothing()
    {
        using var store = new TempStore();
        var writer = Writer(store);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.StageImportAsync(Session, $"CI uses GH_TOKEN={Token}", "import:hermes MEMORY.md", Ct));

        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.Contains("github-token", error.Message, StringComparison.Ordinal);
        Assert.Empty(writer.ListPending());
        Assert.Empty(Events(store));
    }

    [Fact]
    public async Task A_vault_placeholder_is_not_a_credential_and_is_remembered_as_usual()
    {
        using var store = new TempStore();
        var placeholder = Corvid.Vault.Contracts.Placeholders.Format("github-token", "Ab3dEf6hIj9kLm2nOp5qRs8t");

        var result = await Writer(store).WriteAsync(new MemoryWriteRequest(Session, $"CI reads GITHUB_TOKEN={placeholder}"), Ct);

        Assert.Equal(MemoryWriteStatus.Staged, result.Status);
    }
}
