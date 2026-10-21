using System.Buffers;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;

namespace Corvid.Memory;

/// <summary>The extractor labels of plan §9.2. A profile can add free labels per request.</summary>
public static class MemoryLabels
{
    public static ImmutableArray<string> Default { get; } =
        ["person", "organization", "place", "project", "preference", "date", "commitment"];
}

/// <summary>Something worth remembering, usually after <c>remember</c> said yes at egress, or an explicit <c>memory.add</c>.</summary>
/// <param name="Text">The fact as one statement; it is stored as written.</param>
/// <param name="Labels">Extractor labels; default <see cref="MemoryLabels.Default"/>.</param>
/// <param name="Relation">A relation the caller already knows; the extractor finds entities, not relations.</param>
/// <param name="ValidFrom">When the fact became true; null means when it is written.</param>
/// <param name="SourceSequence">The event the fact came from, for lineage.</param>
/// <param name="Surface">The TurnState's surface, for the decider.</param>
/// <param name="Generator">The TurnState's generator description, for the decider.</param>
/// <param name="Tainted">
/// The write was asked for while the session held content from outside the user (plan §10.3): it is staged for review
/// whatever the decider would say, so injected text never reaches memory unreviewed.
/// </param>
/// <param name="LocalOnly">The fact never leaves the machine: it is left out of prompts for cloud models (plan §10.4).</param>
public sealed record MemoryWriteRequest(
    string SessionId,
    string Text,
    ImmutableArray<string> Labels = default,
    RelationInput? Relation = null,
    DateTimeOffset? ValidFrom = null,
    long? SourceSequence = null,
    string Surface = "memory",
    string Generator = "unknown",
    bool Tainted = false,
    bool LocalOnly = false);

public enum MemoryWriteStatus
{
    /// <summary>The operation was written and a <c>MemoryWritten</c> event appended.</summary>
    Applied,
    /// <summary>Nothing was written; the proposal waits in <c>/memory pending</c>.</summary>
    Staged,
    /// <summary>The gate chose noop: the fact is already known. Nothing was written.</summary>
    Noop,
}

/// <param name="Operation">The operation applied, or proposed when staged (null if the decider gave none).</param>
/// <param name="MemoryId">What the <c>MemoryWritten</c> event names: the new fact, the invalidated fact, the block (<c>block:user</c>) or the pending write.</param>
/// <param name="Decision">The <c>memory_op</c> verdict, absent when the write was staged without asking (a block over its budget).</param>
/// <param name="Added">The fact written by add or update.</param>
/// <param name="Closed">The fact ended by update or invalidate.</param>
public sealed record MemoryWriteResult(
    MemoryWriteStatus Status,
    string? Operation,
    string? MemoryId,
    PendingWrite? Pending,
    FabricVerdict? Decision,
    ImmutableArray<Fact> Neighbours,
    ImmutableArray<EntityRef> Entities,
    Fact? Added = null,
    Fact? Closed = null,
    CoreBlock? Block = null);

public enum MemoryImportStatus
{
    /// <summary>Staged for review in <c>/memory pending</c>.</summary>
    Staged,
    /// <summary>The same text already waits for review; nothing was staged.</summary>
    AlreadyPending,
    /// <summary>The same text is already a current fact; nothing was staged.</summary>
    AlreadyKnown,
}

/// <param name="Pending">The staged write, when <see cref="Status"/> is <see cref="MemoryImportStatus.Staged"/>.</param>
public sealed record MemoryImportResult(MemoryImportStatus Status, PendingWrite? Pending);

/// <param name="Neighbours">How many neighbouring facts the decider sees.</param>
/// <param name="NeighbourTokens">The token budget for retrieving them.</param>
public sealed record MemoryWriterOptions(int Neighbours = 5, int NeighbourTokens = 400)
{
    public static MemoryWriterOptions Default { get; } = new();
}

/// <summary>
/// The write path (plan §9.2): extract entities, retrieve neighbouring facts, ask the <c>memory_op</c> gate through the
/// fabric, then write or stage. <c>memory_op</c> is a gate, so only a calibrated answer above its floor writes; the
/// degraded tier (no Kev) never answers a gate, so every write is staged for <c>/memory pending</c> and nothing unreviewed
/// reaches memory. A write asked for in a tainted session (plan §10.3) is staged without asking the gate. Each write appends <c>MemoryWritten</c>; a staged one records <c>staged</c> with the pending id.
/// </summary>
/// <remarks>
/// The memory database and the event log are separate files, so a write and its event are not one transaction: the memory
/// row is written first, then the event. A crash between them leaves a row without its event, never an event without a row.
/// </remarks>
public sealed class MemoryWriter
{
    private const int MaxAppendAttempts = 8;
    private const string MemoryOp = "memory_op";

    /// <summary>The reason recorded on a write staged because the session held content from outside the user.</summary>
    public const string TaintedReason = "tainted: asked for while the session held content from outside the user";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly FactGraph _facts;
    private readonly CoreBlockStore _blocks;
    private readonly HybridRetriever _retriever;
    private readonly MemoryVectors? _vectors;
    private readonly IExtractor _extractor;
    private readonly DecisionFabric _fabric;
    private readonly IEventLog _log;
    private readonly PendingStore _pending;
    private readonly TurnStateBuilder _states;
    private readonly MemoryWriterOptions _options;
    private readonly TimeProvider _time;

    /// <param name="log">The session event log, the same one the fabric appends decisions to.</param>
    public MemoryWriter(MemoryDatabase database, FactGraph facts, CoreBlockStore blocks, HybridRetriever retriever,
        IExtractor extractor, DecisionFabric fabric, IEventLog log, MemoryVectors? vectors = null, TurnStateBuilder? states = null,
        MemoryWriterOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(log);
        _facts = facts;
        _blocks = blocks;
        _retriever = retriever;
        _extractor = extractor;
        _fabric = fabric;
        _log = log;
        _vectors = vectors;
        _pending = new PendingStore(database);
        _states = states ?? new TurnStateBuilder();
        _options = options ?? MemoryWriterOptions.Default;
        _time = time ?? TimeProvider.System;
    }

    public async Task<MemoryWriteResult> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        // 1-2. Entities and neighbours.
        var (entities, neighbours) = await ContextAsync(request.Text, request.Labels, cancellationToken);

        var input = new FactInput(request.Text, entities, request.Relation, request.ValidFrom, request.SessionId, request.SourceSequence,
            request.LocalOnly);
        MemoryProposal Proposal(string? operation) => new(operation, neighbours.IsEmpty ? null : neighbours[0].FactId, request.Text,
            [.. entities.Select(ProposedEntity.From)], request.ValidFrom, request.SourceSequence,
            [.. neighbours.Select(fact => fact.FactId)], Relation: request.Relation is { } relation ? ProposedRelation.From(relation) : null,
            LocalOnly: request.LocalOnly ? true : null);

        // §10.3: with a calibrated decider the gate can write, so a write asked for in a tainted session never reaches it.
        if (request.Tainted)
            return Stage(request.SessionId, PendingKind.Fact, Proposal(null), null, TaintedReason, neighbours, entities);

        // 3. The gate.
        var instructions = Instructions(writer =>
        {
            writer.WriteString("candidate", request.Text);
            writer.WriteStartArray("neighbours");
            foreach (var neighbour in neighbours)
            {
                writer.WriteStartObject();
                writer.WriteString("id", neighbour.FactId);
                writer.WriteString("text", neighbour.Text);
                writer.WriteString("valid_from", neighbour.ValidFrom);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        var verdict = await DecideAsync(request.SessionId, request.Text, request.Surface, request.Generator, entities, instructions,
            cancellationToken);

        var proposal = Proposal(verdict.Answer?.Choice);

        // 4. Write only what the gate let through; everything else waits for review.
        if (!verdict.Verdict.Acted || verdict.Verdict.Option is not { } operation)
            return Stage(request.SessionId, PendingKind.Fact, proposal, verdict, verdict.Verdict.Reason, neighbours, entities);
        if (operation is MemoryOperations.Update or MemoryOperations.Invalidate && proposal.TargetFactId is null)
            return Stage(request.SessionId, PendingKind.Fact, proposal, verdict, $"no neighbour to {operation}", neighbours, entities);
        if (operation == MemoryOperations.Noop)
            return new MemoryWriteResult(MemoryWriteStatus.Noop, operation, null, null, verdict, neighbours, entities);
        var written = await ApplyFactAsync(request.SessionId, operation, proposal.TargetFactId, input, cancellationToken);
        return written with { Decision = verdict, Neighbours = neighbours, Entities = entities };
    }

    /// <summary>
    /// Stages one imported memory (C-027: a Hermes <c>MEMORY.md</c> or <c>USER.md</c> entry) as a fact proposal for
    /// <c>/memory pending</c>, local-only, without asking the gate: the import is the user's request, but the text came from
    /// another agent's files, so it is never applied unreviewed. Entities and neighbours are found as for
    /// <see cref="WriteAsync"/>, so the reviewer sees them and an approved fact keeps its entities. Nothing is staged when the
    /// text is already a current fact or already waits for review, so importing twice stages nothing new.
    /// </summary>
    /// <param name="reason">Recorded as why the write was staged, for example <c>import:hermes MEMORY.md</c>.</param>
    /// <exception cref="ArgumentException">The extractor refused the text (too long); nothing was staged.</exception>
    public async Task<MemoryImportResult> StageImportAsync(string sessionId, string text, string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var key = NormalizeText(text);
        if (_pending.List(PendingStatus.Pending).Any(pending => pending.Kind == PendingKind.Fact
                && pending.Proposal.Text is { } waiting && NormalizeText(waiting) == key))
            return new MemoryImportResult(MemoryImportStatus.AlreadyPending, null);

        var (entities, neighbours) = await ContextAsync(text, default, cancellationToken);
        // The exact text as a current fact: among the neighbours, or found by its own words.
        if (neighbours.Concat(_facts.Search(text, 5).Select(hit => hit.Fact)).Any(fact => fact.IsCurrent && NormalizeText(fact.Text) == key))
            return new MemoryImportResult(MemoryImportStatus.AlreadyKnown, null);

        var proposal = new MemoryProposal(null, neighbours.IsEmpty ? null : neighbours[0].FactId, text,
            [.. entities.Select(ProposedEntity.From)], null, null, [.. neighbours.Select(fact => fact.FactId)], LocalOnly: true);
        var staged = Stage(sessionId, PendingKind.Fact, proposal, null, reason, neighbours, entities);
        return new MemoryImportResult(MemoryImportStatus.Staged, staged.Pending);
    }

    /// <summary>
    /// An agent-initiated edit of the <c>user</c> or <c>agent</c> block (plan §9.2 step 4), through the same gate. Content over
    /// the block's budget is staged for a rewrite instead of failing. The persona block is edited only by the user, with
    /// <see cref="CoreBlockStore.Set"/>.
    /// </summary>
    /// <param name="tainted">Asked for while the session held outside content (plan §10.3): staged for review, never applied.</param>
    public async Task<MemoryWriteResult> EditBlockAsync(string sessionId, string block, string content, string surface = "memory",
        string generator = "unknown", bool tainted = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        CoreBlockStore.CheckName(block);
        ArgumentNullException.ThrowIfNull(content);
        if (block == CoreBlockNames.Persona)
            throw new ArgumentException("The persona block is edited only by the user.", nameof(block));

        var current = _blocks.Get(block);
        var proposal = new MemoryProposal(MemoryOperations.Update, null, null, [], null, null, [], block, content, current.Revision);
        var tokens = _blocks.CountTokens(content);
        // The consolidation job that proposes a rewrite to fit (plan §9.5) is not built; the reviewer sees the reason.
        if (tokens > current.BudgetTokens)
            return Stage(sessionId, PendingKind.Block, proposal, null, $"over_budget: {tokens} of {current.BudgetTokens} tokens", [], []);
        if (tainted)
            return Stage(sessionId, PendingKind.Block, proposal, null, TaintedReason, [], []);

        var instructions = Instructions(writer =>
        {
            writer.WriteString("block", block);
            writer.WriteString("current", current.Content);
            writer.WriteString("proposed", content);
        });
        var verdict = await DecideAsync(sessionId, content, surface, generator, [], instructions, cancellationToken);
        proposal = proposal with { Operation = verdict.Answer?.Choice };
        if (!verdict.Verdict.Acted || verdict.Verdict.Option is not (MemoryOperations.Add or MemoryOperations.Update or MemoryOperations.Noop))
            return Stage(sessionId, PendingKind.Block, proposal, verdict, verdict.Verdict.Acted ? "operation does not apply to a block" : verdict.Verdict.Reason, [], []);
        if (verdict.Verdict.Option == MemoryOperations.Noop)
            return new MemoryWriteResult(MemoryWriteStatus.Noop, MemoryOperations.Noop, null, null, verdict, [], [], Block: current);

        var applied = _blocks.Set(block, content, current.Revision);
        if (applied.Status != BlockWriteStatus.Applied)
            return Stage(sessionId, PendingKind.Block, proposal, verdict, applied.Status == BlockWriteStatus.Conflict ? "block changed" : "over_budget", [], []);
        Append(sessionId, new MemoryWritten(BlockId(block), MemoryOperations.Update));
        return new MemoryWriteResult(MemoryWriteStatus.Applied, MemoryOperations.Update, BlockId(block), null, verdict, [], [], Block: applied.Block);
    }

    /// <summary>Staged writes awaiting review, oldest first.</summary>
    public IReadOnlyList<PendingWrite> ListPending() => _pending.List(PendingStatus.Pending);

    public PendingWrite? GetPending(string pendingId) => _pending.Get(pendingId);

    /// <summary>
    /// Applies a staged write as reviewed. The reviewer may change the operation or its target. Records the decision's outcome
    /// for calibration: <c>corrected</c> when the reviewer chose an operation other than the decider's, else <c>approved</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The write was already resolved, its target is no longer current, or the
    /// block changed or would exceed its budget; the write stays pending except in the first case.</exception>
    public async Task<MemoryWriteResult> ApproveAsync(string pendingId, string? operation = null, string? targetFactId = null,
        CancellationToken cancellationToken = default)
    {
        var pending = Open(pendingId);
        var proposal = pending.Proposal;
        MemoryWriteResult result;
        string applied;
        if (pending.Kind == PendingKind.Block)
        {
            applied = MemoryOperations.Update;
            // Resolve first, as for facts, so a concurrent reject or approve cannot also act; a refused write reopens it.
            Resolve(pending, PendingStatus.Approved, applied);
            var write = _blocks.Set(proposal.Block!, proposal.Content!, proposal.BlockRevision);
            if (write.Status != BlockWriteStatus.Applied)
            {
                _pending.Reopen(pending.PendingId, PendingStatus.Approved);
                throw new InvalidOperationException(write.Status == BlockWriteStatus.Conflict
                    ? $"Block '{proposal.Block}' changed since the edit was proposed; reject it and propose again."
                    : $"The edit is {write.ContentTokens} tokens, over block '{proposal.Block}' budget of {write.Block.BudgetTokens}.");
            }
            Append(pending.SessionId, new MemoryWritten(BlockId(proposal.Block!), applied));
            result = new MemoryWriteResult(MemoryWriteStatus.Applied, applied, BlockId(proposal.Block!), pending, null, [], [], Block: write.Block);
        }
        else
        {
            applied = operation ?? proposal.Operation ?? MemoryOperations.Add;
            if (!MemoryOperations.IsOperation(applied))
                throw new ArgumentException($"Unknown memory operation '{applied}'.", nameof(operation));
            var target = targetFactId ?? proposal.TargetFactId;
            if (applied is MemoryOperations.Update or MemoryOperations.Invalidate
                && (target is null || _facts.Get(target) is not { IsCurrent: true }))
                throw new InvalidOperationException($"'{applied}' needs a current target fact.");
            var entities = proposal.Entities.IsDefault ? [] : proposal.Entities.Select(entity => entity.ToEntity()).ToImmutableArray();
            // Resolve first: two reviewers approving at once must not both write. If the graph refuses the write (the target
            // was invalidated since the check above), the write is reopened, so it is never marked approved unwritten.
            Resolve(pending, PendingStatus.Approved, applied);
            if (applied == MemoryOperations.Noop)
            {
                result = new MemoryWriteResult(MemoryWriteStatus.Noop, applied, null, pending, null, [], entities);
            }
            else
            {
                (Fact? Added, Fact? Closed) written;
                try
                {
                    written = WriteFact(applied, target, new FactInput(proposal.Text!, entities, proposal.Relation?.ToRelation(),
                        proposal.ValidFrom, pending.SessionId, proposal.SourceSequence, proposal.LocalOnly == true));
                }
                catch
                {
                    _pending.Reopen(pending.PendingId, PendingStatus.Approved);
                    throw;
                }
                result = (await FinishFactAsync(pending.SessionId, applied, written, cancellationToken)) with
                {
                    Pending = pending,
                    Entities = entities,
                };
            }
        }
        if (pending.DecisionId is { } decisionId)
            _fabric.RecordOutcome(pending.SessionId, decisionId,
                operation is not null && operation != proposal.Operation ? "corrected" : "approved", cancellationToken);
        return result;
    }

    /// <summary>Discards a staged write and records the decision's outcome as <c>rejected</c>. Nothing is written.</summary>
    public PendingWrite Reject(string pendingId, string reason = "rejected", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var pending = Open(pendingId);
        Resolve(pending, PendingStatus.Rejected, reason);
        if (pending.DecisionId is { } decisionId)
            _fabric.RecordOutcome(pending.SessionId, decisionId, "rejected", cancellationToken);
        return _pending.Get(pendingId)!;
    }

    public static string BlockId(string block) => "block:" + block;

    private PendingWrite Open(string pendingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingId);
        var pending = _pending.Get(pendingId) ?? throw new KeyNotFoundException($"Unknown pending write '{pendingId}'.");
        return pending.Status == PendingStatus.Pending ? pending
            : throw new InvalidOperationException($"Pending write '{pendingId}' was already {pending.Status.ToString().ToLowerInvariant()}.");
    }

    private void Resolve(PendingWrite pending, PendingStatus status, string resolution)
    {
        if (!_pending.Resolve(pending.PendingId, status, resolution, _time.GetUtcNow()))
            throw new InvalidOperationException($"Pending write '{pending.PendingId}' was already resolved.");
    }

    private async Task<FabricVerdict> DecideAsync(string sessionId, string text, string surface, string generator,
        ImmutableArray<EntityRef> entities, JsonElement instructions, CancellationToken cancellationToken)
    {
        var entityMap = entities.GroupBy(entity => entity.NormalizedLabel, StringComparer.Ordinal)
            .ToImmutableSortedDictionary(group => group.Key, group => group.Select(entity => entity.Name).ToImmutableArray(), StringComparer.Ordinal);
        var state = _states.Build(new TurnStateInput(text, surface, generator, Entities: entityMap));
        var question = _fabric.Policy.Catalogue[MemoryOp].ToQuestion(instructions: DecisionContent.FromJson(instructions));
        var result = await _fabric.DecideAsync(sessionId, Checkpoint.PreExecute, state, [KeyValuePair.Create(MemoryOp, question)],
            cancellationToken: cancellationToken);
        return result[MemoryOp];
    }

    // Steps 1 and 2 of the write path. Over-long text throws from the extractor; nothing is truncated (IExtractor's contract).
    private async Task<(ImmutableArray<EntityRef> Entities, ImmutableArray<Fact> Neighbours)> ContextAsync(string text,
        ImmutableArray<string> labels, CancellationToken cancellationToken)
    {
        // 1. Entities.
        var extraction = await _extractor.ExtractAsync(new ExtractionRequest(text, labels.IsDefaultOrEmpty ? MemoryLabels.Default : labels),
            cancellationToken);
        var entities = extraction.Spans.Select(span => new EntityRef(span.Label, span.Text))
            .Where(entity => !string.IsNullOrWhiteSpace(entity.Name))
            .DistinctBy(entity => (entity.NormalizedLabel, entity.Key)).ToImmutableArray();

        // 2. Neighbours: current facts about the same entities or with similar text.
        var retrieved = await _retriever.RetrieveAsync(new RetrievalRequest(text, _options.NeighbourTokens, entities,
            [MemoryItemKind.Fact]), cancellationToken);
        return (entities, retrieved.Items.Select(item => item.Fact!).Take(_options.Neighbours).ToImmutableArray());
    }

    // Equal memories differ at most in case and spacing.
    private static string NormalizeText(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private JsonElement Instructions(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("question", _fabric.Policy.Catalogue[MemoryOp].Instructions);
            body(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private Task<MemoryWriteResult> ApplyFactAsync(string sessionId, string operation, string? target, FactInput input,
        CancellationToken cancellationToken) =>
        FinishFactAsync(sessionId, operation, WriteFact(operation, target, input), cancellationToken);

    private (Fact? Added, Fact? Closed) WriteFact(string operation, string? target, FactInput input)
    {
        switch (operation)
        {
            case MemoryOperations.Add:
                return (_facts.Add(input), null);
            case MemoryOperations.Update:
                var (closed, added) = _facts.Supersede(target!, input);
                return (added, closed);
            case MemoryOperations.Invalidate:
                return (null, _facts.Invalidate(target!));
            default:
                throw new ArgumentException($"'{operation}' writes nothing.", nameof(operation));
        }
    }

    private async Task<MemoryWriteResult> FinishFactAsync(string sessionId, string operation, (Fact? Added, Fact? Closed) written,
        CancellationToken cancellationToken)
    {
        var (added, closed) = written;
        var memoryId = (added ?? closed)!.FactId;
        Append(sessionId, new MemoryWritten(memoryId, operation));
        if (added is not null && _vectors is not null)
            await _vectors.EmbedAsync([(added.FactId, added.Text)], cancellationToken);
        return new MemoryWriteResult(MemoryWriteStatus.Applied, operation, memoryId, null, null, [], [], added, closed);
    }

    private MemoryWriteResult Stage(string sessionId, PendingKind kind, MemoryProposal proposal, FabricVerdict? verdict, string reason,
        ImmutableArray<Fact> neighbours, ImmutableArray<EntityRef> entities)
    {
        var pending = _pending.Stage(sessionId, kind, proposal, verdict?.DecisionId, reason, _time.GetUtcNow());
        Append(sessionId, new MemoryWritten(pending.PendingId, MemoryOperations.Staged));
        return new MemoryWriteResult(MemoryWriteStatus.Staged, proposal.Operation, pending.PendingId, pending, verdict, neighbours, entities);
    }

    // MemoryWritten does not depend on the head, so a concurrent writer only means retrying at the new head (as the fabric
    // does). It is not cancellable: the memory row it records is already written.
    private void Append(string sessionId, SessionEvent payload)
    {
        for (var attempt = 1; ; attempt++)
        {
            var head = _log.GetHead(sessionId);
            try
            {
                _log.Append(sessionId, head, [EventData.Create(payload)]);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }
}
