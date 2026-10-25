namespace Corvid.App.Core.Memory;

/// <summary>
/// The Memory area's seam to the engine (docs/plan/ui.md §4.9, F11): the review queue of staged writes, the core blocks,
/// the facts, the lessons and the Hermes import. The demo implements it now; <c>Corvid.App.Engine</c> will over the
/// memory store. Nothing here writes without the person's approval: approving is the write.
/// </summary>
public interface IMemoryService
{
    Task<MemorySnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The store changed outside the Memory page (a <c>#</c> line staged a fact, a turn wrote): what the page, its badge and
    /// the Board's tray show may be stale. Raised on the UI thread; a service that cannot tell never raises it.
    /// </summary>
    event EventHandler? Changed { add { } remove { } }

    /// <summary>
    /// Writes a staged fact (G-8: correct then approve). <paramref name="text"/> is the statement as approved, corrected
    /// or not; <paramref name="targetFactId"/> is the existing fact an update or an invalidation closes.
    /// </summary>
    Task<FactWrite> ApproveFactAsync(string id, MemoryOperation operation, string text, string? targetFactId, CancellationToken cancellationToken);

    /// <summary>Writes a staged block update with <paramref name="text"/> as the added line, or as the whole block when it
    /// <paramref name="replacesBlock"/> (a consolidation). Returns the block as written.</summary>
    Task<CoreBlock> ApproveBlockUpdateAsync(string id, string text, bool replacesBlock, CancellationToken cancellationToken);

    /// <summary>Drops a staged fact or block update and records why.</summary>
    Task RejectAsync(string id, string reason, CancellationToken cancellationToken);

    /// <summary>Proposes the block rewritten shorter, the update folded in; nothing is written.</summary>
    Task<BlockConsolidation> ConsolidateAsync(string updateId, CancellationToken cancellationToken);

    /// <summary>Counts text with the generator's tokenizer, as the block budgets are counted.</summary>
    Task<int> CountTokensAsync(string text, CancellationToken cancellationToken);

    /// <summary>Approves a lesson, superseding the approved lesson <paramref name="supersedesId"/> when given.</summary>
    Task<Lesson> ApproveLessonAsync(string id, string? supersedesId, CancellationToken cancellationToken);

    Task RejectLessonAsync(string id, string reason, CancellationToken cancellationToken);

    /// <summary>Facts whose text matches <paramref name="query"/>; every fact for an empty query.</summary>
    Task<IReadOnlyList<FactRecord>> SearchFactsAsync(string query, CancellationToken cancellationToken);

    /// <summary>Lists what an import would stage, and what the scanner refuses; nothing is staged.</summary>
    Task<IReadOnlyList<ImportEntry>> DryRunImportAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stages the dry run's entries named by <paramref name=entryIds/> (<see cref="ImportEntry.Id"/>) that the scanner
    /// passed, as proposals for review (never writes), and returns them. An id the source no longer has (it changed after
    /// the dry run) or one already staged stages nothing: what is staged is what the person saw.
    /// </summary>
    Task<IReadOnlyList<StagedFact>> ImportForReviewAsync(IReadOnlyList<string> entryIds, CancellationToken cancellationToken);
}
