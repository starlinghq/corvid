namespace Corvid.App.Core.Memory;

/// <summary>
/// What a staged fact asks the store to do (docs/plan/ui.md §4.9): add a new fact, update (supersede) an existing one,
/// invalidate an existing one, or nothing. The decider proposes one; the reviewer may pick another.
/// </summary>
public enum MemoryOperation
{
    Add,
    Update,
    Invalidate,
    Noop,
}

/// <summary>An entity a fact names, as the fact graph types it: "person · the user", "habit · CI review".</summary>
public sealed record FactEntity(string Kind, string Name)
{
    public string Label => $"{Kind} · {Name}";
}

/// <summary>
/// An existing fact the hybrid search found near a staged one, with its similarity score, so an add that should be an
/// update is visible. <see cref="Text"/> may hold <c>`code`</c> spans (paths, repositories).
/// </summary>
public sealed record NearbyFact(string FactId, string Text, DateOnly ValidFrom, double Score);

/// <summary>
/// A fact the decider staged instead of writing: who staged it and when, why (its score under the floor, or no decider),
/// the triple, its entities, the proposed operation and the nearest existing facts, and where the evidence is.
/// </summary>
public sealed record StagedFact(string Id, string Subject, string Predicate, string Object)
{
    public DateTimeOffset StagedAt { get; init; }

    /// <summary>The session or job that staged it: "nightly-tidy", "cli-9f2e", "hermes import".</summary>
    public string StagedBy { get; init; } = "";

    /// <summary>"memory_op 0.62, under its floor 0.80".</summary>
    public string Why { get; init; } = "";

    public IReadOnlyList<FactEntity> Entities { get; init; } = [];

    public DateOnly ValidFrom { get; init; }

    public MemoryOperation Proposed { get; init; } = MemoryOperation.Add;

    public IReadOnlyList<NearbyFact> Nearest { get; init; } = [];

    /// <summary>How the nearest facts were found: "hybrid search".</summary>
    public string SearchKind { get; init; } = "hybrid search";

    /// <summary>"job:nightly-tidy events 41, 58"; empty when there is none (an import).</summary>
    public string Evidence { get; init; } = "";

    /// <summary>The statement as one sentence, which a correction edits: the parts that are there, one space apart (an
    /// import's entry is all subject).</summary>
    public string Text => string.Join(' ', new[] { Subject, Predicate, Object }.Where(part => part.Length > 0));
}

/// <summary>
/// A staged change to a core block (block writes always wait for review): the unchanged line around it, the line it
/// adds, the block's tokens now and after, its budget, and the fill at which consolidation is offered.
/// </summary>
public sealed record StagedBlockUpdate(string Id, string Block)
{
    public DateTimeOffset StagedAt { get; init; }

    public string StagedBy { get; init; } = "";

    public string Why { get; init; } = "";

    /// <summary>The line before the change, shown as context.</summary>
    public string Context { get; init; } = "";

    /// <summary>The line the update adds.</summary>
    public string Added { get; init; } = "";

    public int TokensNow { get; init; }

    public int TokensAfter { get; init; }

    public int Budget { get; init; }

    /// <summary>The fill (0 to 1) from which "Consolidate the block…" is offered: the configured threshold, 0.9 by default.</summary>
    public double ConsolidateAt { get; init; } = 0.9;

    /// <summary>
    /// The update rewrites the whole block (a consolidation the block rewrite job staged, plan §9.5): <see cref="Added"/> is
    /// the whole block as it would be, and <see cref="Diff"/> its lines against the block now.
    /// </summary>
    public bool Replaces { get; init; }

    /// <summary>For an update that <see cref="Replaces"/> the block: the lines kept, removed and added, in order.</summary>
    public IReadOnlyList<BlockDiffRow> Diff { get; init; } = [];

    /// <summary>
    /// A7 review: a rewrite made from an earlier revision of the block, which has changed since: approving it would fail,
    /// and a diff against the block now would show changes it does not make, so it has none and only Reject is offered (the
    /// sleep-time job rejects it and proposes again on its next pass).
    /// </summary>
    public bool Stale { get; init; }
}

public enum BlockLineKind
{
    Kept,
    Removed,
    Added,
}

/// <summary>One line of a block rewrite's diff.</summary>
public sealed record BlockDiffRow(BlockLineKind Kind, string Text);

/// <summary>A consolidation's proposal: the block rewritten shorter, and its size counted with the generator's tokenizer.</summary>
public sealed record BlockConsolidation(string Block, string Text, int Tokens);

/// <summary>
/// A core block (<c>user</c>, <c>agent</c>, <c>persona</c>): its size against its budget, its revision, whether it stays
/// on this machine, and when it was last written and by whom.
/// </summary>
public sealed record CoreBlock(string Name, int Tokens, int Budget)
{
    public int Revision { get; init; }

    /// <summary>Never sent to a cloud model.</summary>
    public bool LocalOnly { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Written by the person rather than by Corvid ("set by you").</summary>
    public bool SetByUser { get; init; }

    /// <summary>The block's text, as the prompt carries it.</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// A7V1-5: the fill (0 to 1) from which consolidation is offered: the block rewrite job's threshold
    /// (<c>Corvid:Memory:Consolidation:Threshold</c>), 0.9 by default.
    /// </summary>
    public double ConsolidateAt { get; init; } = 0.9;
}

/// <summary>A stored fact with its bi-temporal fields: valid from, valid until (when it stopped holding), recorded at.</summary>
public sealed record FactRecord(string Id, string Text, DateOnly ValidFrom)
{
    public DateOnly? ValidUntil { get; init; }

    public DateOnly Recorded { get; init; }

    /// <summary>Superseded or invalidated: kept for history, no longer recalled.</summary>
    public bool Invalidated { get; init; }
}

/// <summary>What approving a staged fact wrote, and the existing fact it closed when it was an update or invalidation.</summary>
public sealed record FactWrite(FactRecord? Written, FactRecord? Closed);

/// <summary>A lesson the agent drew from a session, waiting for review: the rule, when it applies, its scope and evidence.</summary>
public sealed record LessonCandidate(string Id, string Rule, string AppliesWhen)
{
    /// <summary>The project it applies to: "corvid".</summary>
    public string Project { get; init; } = "";

    /// <summary>"quality bar passed · waits for review".</summary>
    public string Status { get; init; } = "";

    /// <summary>"cli-9f2e events 1182 and 1190".</summary>
    public string Evidence { get; init; } = "";

    /// <summary>What kind of lesson: "recovery", "preference", "convention".</summary>
    public string Kind { get; init; } = "";
}

/// <summary>An approved lesson; a later one may supersede it.</summary>
public sealed record Lesson(string Id, string Rule, string AppliesWhen)
{
    public string Project { get; init; } = "";

    public DateOnly Approved { get; init; }

    /// <summary>The rule of the lesson that replaced it, when one did.</summary>
    public string? SupersededBy { get; init; }
}

/// <summary>Where an import reads from: Hermes's <c>~/.hermes</c>, and what it does with what it finds.</summary>
public sealed record ImportSource(string Name, string Path, string Note);

/// <summary>An entry a dry run found: its file, its text, and whether the scanner refused it and why.</summary>
public sealed record ImportEntry(string File, string Text)
{
    /// <summary>
    /// Names the entry as the dry run read it (a hash of its file and text), so staging takes exactly what the dry run
    /// showed: an entry whose source changed since has another id and is not staged.
    /// </summary>
    public string Id { get; init; } = "";

    public bool Refused { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>The store's size, for the status bar: "212 facts · 1,804 episodes · 3 blocks".</summary>
public sealed record MemoryStats(int Facts, int Episodes, int Blocks);

/// <summary>Everything the Memory page shows at once.</summary>
public sealed record MemorySnapshot
{
    public IReadOnlyList<StagedFact> StagedFacts { get; init; } = [];

    public IReadOnlyList<StagedBlockUpdate> BlockUpdates { get; init; } = [];

    public IReadOnlyList<CoreBlock> Blocks { get; init; } = [];

    /// <summary>The facts, most recently written first.</summary>
    public IReadOnlyList<FactRecord> Facts { get; init; } = [];

    public IReadOnlyList<LessonCandidate> PendingLessons { get; init; } = [];

    public IReadOnlyList<Lesson> Lessons { get; init; } = [];

    public ImportSource? Import { get; init; }

    public MemoryStats Stats { get; init; } = new(0, 0, 0);
}
