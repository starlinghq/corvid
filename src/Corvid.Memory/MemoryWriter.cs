using System.Buffers;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Secrets;

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
    /// <summary>
    /// The text holds a credential value (C-040): nothing was written, staged or asked, so the value is in no store. The
    /// result's <see cref="MemoryWriteResult.Reason"/> names its kind, never the value.
    /// </summary>
    Refused,
}

/// <param name="Operation">The operation applied, or proposed when staged (null if the decider gave none).</param>
/// <param name="MemoryId">What the <c>MemoryWritten</c> event names: the new fact, the invalidated fact, the block (<c>block:user</c>) or the pending write.</param>
/// <param name="Decision">The <c>memory_op</c> verdict, absent when the write was staged without asking (a block over its budget).</param>
/// <param name="Added">The fact written by add or update.</param>
/// <param name="Closed">The fact ended by update or invalidate.</param>
/// <param name="Reason">Why a write was <see cref="MemoryWriteStatus.Refused"/>; null otherwise.</param>
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
    CoreBlock? Block = null,
    string? Reason = null);

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

        // 0. A credential value is refused before anything sees the text: the extractor, the gate (whose Decision would log
        // it) and the pending store (C-040).
        if (CredentialRefusal(request.Text) is { } refused)
            return Refused(refused);

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
            return Stage(request.SessionId, PendingKind.Fact, Proposal(null) with { Tainted = true }, null, TaintedReason, neighbours, entities);

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
        if (CredentialRefusal(text) is { } refused)
            throw new ArgumentException("The entry " + refused + "; nothing was staged.", nameof(text));
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
        if (CredentialRefusal(content) is { } refused)
            return Refused(refused);

        var current = _blocks.Get(block);
        var proposal = new MemoryProposal(MemoryOperations.Update, null, null, [], null, null, [], block, content, current.Revision);
        var tokens = _blocks.CountTokens(content);
        var overBudget = tokens > current.BudgetTokens ? $"over_budget: {tokens} of {current.BudgetTokens} tokens" : null;
        // A7 review (security M1): taint first, so a tainted edit over the budget is staged as tainted, never as a routine
        // over-budget edit the sleep-time job would fold into a rewrite that names neither its session nor its taint. The
        // budget is said too, so the reviewer knows approving it as it stands would fail.
        if (tainted)
            return Stage(sessionId, PendingKind.Block, proposal with { Tainted = true }, null,
                overBudget is null ? TaintedReason : TaintedReason + "; " + overBudget, [], []);
        // Staged with the reason; the consolidation job (plan §9.5, BlockConsolidator) proposes a rewrite that fits.
        if (overBudget is not null)
            return Stage(sessionId, PendingKind.Block, proposal, null, overBudget, [], []);

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

    /// <summary>What the reason of every staged consolidation starts with (plan §9.5).</summary>
    public const string ConsolidationReason = "consolidation";

    /// <summary>
    /// Stages a block rewrite the consolidation job proposed (plan §9.5, §9.2 step 4) for <c>/memory pending</c>: once a person
    /// approves it, <paramref name="content"/> replaces the whole block, at <paramref name="revision"/> or not at all. Nothing
    /// is applied here, since a core block enters every prompt. A rewrite holding a credential value is refused (C-040) and
    /// one over the block's budget is never staged. The persona block is the user's alone, so it is never rewritten.
    /// </summary>
    /// <param name="revision">The block revision the rewrite was made from; approving it after the block changed fails.</param>
    /// <param name="reason">Why it was staged, for the reviewer; prefixed with <see cref="ConsolidationReason"/>.</param>
    /// <param name="supersedes">
    /// The staged edit the rewrite folds in, resolved when the rewrite is approved. The edit's session and taint go onto the
    /// proposal and into its reason ("folds in edit X from tainted session Y"), so the reviewer sees whose words it carries.
    /// </param>
    /// <exception cref="ArgumentException">The persona block, or a rewrite over the block's budget; nothing was staged.</exception>
    public MemoryWriteResult StageConsolidation(string sessionId, string block, string content, long revision, string reason,
        string? supersedes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        CoreBlockStore.CheckName(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (block == CoreBlockNames.Persona)
            throw new ArgumentException("The persona block is edited only by the user.", nameof(block));
        if (CredentialRefusal(content) is { } refused)
            return Refused(refused);
        var current = _blocks.Get(block);
        var tokens = _blocks.CountTokens(content);
        if (tokens > current.BudgetTokens)
            throw new ArgumentException($"The rewrite is {tokens} tokens, over block '{block}' budget of {current.BudgetTokens}; nothing was staged.",
                nameof(content));
        var proposal = new MemoryProposal(MemoryOperations.Update, null, null, [], null, null, [], block, content, revision,
            Consolidation: true, Supersedes: supersedes);
        var why = reason.StartsWith(ConsolidationReason, StringComparison.Ordinal) ? reason : ConsolidationReason + ": " + reason;
        if (supersedes is not null)
        {
            var edit = _pending.Get(supersedes);
            var taintedEdit = edit is not null && IsTainted(edit);
            proposal = proposal with { FoldsInSession = edit?.SessionId, Tainted = taintedEdit ? true : null };
            why += edit is null ? $", folds in edit {supersedes}"
                : $", folds in edit {supersedes} from {(taintedEdit ? "tainted " : "")}session {edit.SessionId}";
        }
        return Stage(sessionId, PendingKind.Block, proposal, null, why, [], []);
    }

    /// <summary>
    /// The write was asked for while its session held content from outside the user (plan §10.3), or folds in one that was:
    /// it is staged with <see cref="TaintedReason"/> or marked <see cref="MemoryProposal.Tainted"/>.
    /// </summary>
    public static bool IsTainted(PendingWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write.Proposal.Tainted == true || write.Reason.StartsWith(TaintedReason, StringComparison.Ordinal);
    }

    /// <summary>What the reason of a consolidation retired because its block changed starts with (A7 review).</summary>
    public const string StaleReason = "stale";

    /// <summary>
    /// A7 review (depth M4): the consolidation of <paramref name="block"/> waiting for review, rejected as stale when the block
    /// changed since it was made (its revision is not the block's), so it no longer blocks a new proposal and is never shown
    /// against content it was not made from. Returns the rejected write, or null when none waits or the waiting one is current.
    /// </summary>
    public PendingWrite? RetireStaleConsolidation(string block, CancellationToken cancellationToken = default)
    {
        CoreBlockStore.CheckName(block);
        var current = _blocks.Get(block);
        if (PendingConsolidation(block) is not { } waiting || waiting.Proposal.BlockRevision == current.Revision)
            return null;
        try
        {
            return Reject(waiting.PendingId, $"{StaleReason}: the {block} block changed since the rewrite was made (revision "
                + $"{waiting.Proposal.BlockRevision}, now {current.Revision})", cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Resolved meanwhile by a person: nothing to retire.
            return null;
        }
    }

    /// <summary>What the reason of every staged duplicate starts with (plan §9.5).</summary>
    public const string DuplicateReason = "duplicate";

    /// <summary>
    /// Stages ending <paramref name="duplicate"/> because it says what <paramref name="kept"/> says (plan §9.5's fact dedupe):
    /// an <c>invalidate</c> of the duplicate, with the kept fact as its nearest neighbour, for <c>/memory pending</c>. Nothing
    /// is ended until a person approves it. Null when that duplicate already waits or a person rejected ending it.
    /// </summary>
    public MemoryWriteResult? StageDuplicate(string sessionId, Fact duplicate, Fact kept)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(duplicate);
        ArgumentNullException.ThrowIfNull(kept);
        if (duplicate.FactId == kept.FactId)
            throw new ArgumentException("A fact is not its own duplicate.", nameof(duplicate));
        bool Same(PendingWrite write) => write.Kind == PendingKind.Fact && write.Proposal.Operation == MemoryOperations.Invalidate
            && write.Proposal.TargetFactId == duplicate.FactId && write.Reason.StartsWith(DuplicateReason, StringComparison.Ordinal);
        if (_pending.List(PendingStatus.Pending).Any(Same) || _pending.List(PendingStatus.Rejected).Any(Same))
            return null;
        var proposal = new MemoryProposal(MemoryOperations.Invalidate, duplicate.FactId, duplicate.Text,
            [.. duplicate.Entities.Select(entity => new ProposedEntity(entity.Label, entity.Name))], null, duplicate.SourceSequence,
            [kept.FactId], LocalOnly: duplicate.LocalOnly ? true : null);
        return Stage(sessionId, PendingKind.Fact, proposal, null, $"{DuplicateReason}: says what {kept.FactId} says", [kept], []);
    }

    /// <summary>The consolidation of <paramref name="block"/> waiting for review, or null.</summary>
    public PendingWrite? PendingConsolidation(string block) =>
        _pending.List(PendingStatus.Pending).FirstOrDefault(write => write.Kind == PendingKind.Block
            && write.Proposal.Consolidation == true && write.Proposal.Block == block);

    /// <summary>
    /// A person rejected a consolidation of <paramref name="block"/> made from <paramref name="revision"/> (folding in
    /// <paramref name="supersedes"/>): the job does not propose one again until the block changes.
    /// </summary>
    public bool ConsolidationRejected(string block, long revision, string? supersedes = null) =>
        _pending.List(PendingStatus.Rejected).Any(write => write.Kind == PendingKind.Block && write.Proposal.Consolidation == true
            && write.Proposal.Block == block && write.Proposal.BlockRevision == revision
            && string.Equals(write.Proposal.Supersedes, supersedes, StringComparison.Ordinal));

    /// <summary>
    /// Why <paramref name="text"/> may not be written (C-040), or null: it holds a credential value under the egress rules
    /// (shapes and labelled values; a fact with its value redacted would be useless, so it is refused, not redacted). A
    /// detector timeout refuses. The reason names kinds, never a value.
    /// </summary>
    internal static string? CredentialRefusal(string text)
    {
        ImmutableArray<CredentialMatch> found;
        try
        {
            found = CredentialDetector.Find(text, CredentialDetector.Egress);
        }
        catch (RegexMatchTimeoutException)
        {
            return "could not be checked for credential values (the detector timed out)";
        }
        if (found.IsEmpty)
            return null;
        var kinds = string.Join(", ", found.Select(match => CredentialScrub.KindName(match.Kind)).Distinct(StringComparer.Ordinal));
        return $"holds a credential value ({kinds}); keep secrets in Corvid Vault";
    }

    private static MemoryWriteResult Refused(string reason) =>
        new(MemoryWriteStatus.Refused, null, null, null, null, [], [], Reason: reason);

    /// <summary>Staged writes awaiting review, oldest first.</summary>
    public IReadOnlyList<PendingWrite> ListPending() => _pending.List(PendingStatus.Pending);

    public PendingWrite? GetPending(string pendingId) => _pending.Get(pendingId);

    /// <summary>
    /// Applies a staged write as reviewed. The reviewer may change the operation or its target. Records the decision's outcome
    /// for calibration: <c>corrected</c> when the reviewer chose an operation other than the decider's, else <c>approved</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The write was already resolved, its target is no longer current, or the
    /// block changed or would exceed its budget; the write stays pending except in the first case.</exception>
    public Task<MemoryWriteResult> ApproveAsync(string pendingId, string? operation = null, string? targetFactId = null,
        CancellationToken cancellationToken = default) =>
        ApproveAsync(pendingId, operation, targetFactId, null, cancellationToken);

    /// <summary>
    /// Applies a staged write as reviewed and corrected (G-8): <paramref name="correctedText"/>, when given and different,
    /// is written instead of what was staged: a fact's statement (its entities found again from the new text) or a block's
    /// whole content. A correction is checked as a write is (no credential value; a block within its budget, at the revision
    /// it was staged against) and recorded as the decision's outcome <c>corrected</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The correction holds a credential value, or the extractor refused it (too long);
    /// nothing was written and the write stays pending.</exception>
    public async Task<MemoryWriteResult> ApproveAsync(string pendingId, string? operation, string? targetFactId, string? correctedText,
        CancellationToken cancellationToken)
    {
        var pending = Open(pendingId);
        var proposal = pending.Proposal;
        var staged = pending.Kind == PendingKind.Block ? proposal.Content : proposal.Text;
        var corrected = correctedText is not null && !string.Equals(correctedText, staged, StringComparison.Ordinal);
        if (corrected)
        {
            if (pending.Kind == PendingKind.Fact)
                ArgumentException.ThrowIfNullOrWhiteSpace(correctedText, nameof(correctedText));
            if (CredentialRefusal(correctedText!) is { } refused)
                throw new ArgumentException("The correction " + refused + "; nothing was written.", nameof(correctedText));
            if (pending.Kind == PendingKind.Block)
                proposal = proposal with { Content = correctedText };
            else
            {
                // The entities are the new statement's, so the fact graph links what the reviewer wrote, not what was staged.
                var (entities, _) = await ContextAsync(correctedText!, default, cancellationToken);
                proposal = proposal with { Text = correctedText, Entities = [.. entities.Select(ProposedEntity.From)], Relation = null };
            }
        }
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
            // A consolidation that folded a staged edit in wrote that edit too, so it no longer waits.
            if (proposal.Supersedes is { } folded && _pending.Get(folded) is { Status: PendingStatus.Pending } edit
                && _pending.Resolve(folded, PendingStatus.Approved, "consolidated into " + pending.PendingId, _time.GetUtcNow())
                && edit.DecisionId is { } editDecision)
                _fabric.RecordOutcome(edit.SessionId, editDecision, "corrected", cancellationToken);
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
            // A7 review (depth L3): ending a duplicate is right only while the fact it duplicates still holds; once that one
            // was ended or replaced, ending this one too would lose the fact, so the staged ending is rejected instead.
            if (pending.Reason.StartsWith(DuplicateReason, StringComparison.Ordinal) && applied == MemoryOperations.Invalidate
                && (proposal.NeighbourIds.IsDefaultOrEmpty || _facts.Get(proposal.NeighbourIds[0]) is not { IsCurrent: true }))
            {
                var kept = proposal.NeighbourIds.IsDefaultOrEmpty ? "the fact it duplicates" : proposal.NeighbourIds[0];
                Reject(pendingId, $"{StaleReason}: {kept} is no longer current, so this fact is not a duplicate of a current one",
                    cancellationToken);
                throw new InvalidOperationException(
                    $"{kept} is no longer current, so ending its duplicate would lose the fact; the duplicate's ending was rejected.");
            }
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
                corrected || operation is not null && operation != proposal.Operation ? "corrected" : "approved", cancellationToken);
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
        // A consolidation folding this edit in would write it after all, so it goes with it.
        foreach (var rewrite in _pending.List(PendingStatus.Pending).Where(write => write.Proposal.Supersedes == pendingId))
            _pending.Resolve(rewrite.PendingId, PendingStatus.Rejected, "the edit it folds in was rejected", _time.GetUtcNow());
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

    private void Append(string sessionId, SessionEvent payload) => EventAppend.Append(_log, sessionId, payload);
}
