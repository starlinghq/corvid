using System.Collections.Immutable;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Memory;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Ops;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Knowledge;
using Corvid.Memory;
using Corvid.Models;
using AppCoreBlock = Corvid.App.Core.Memory.CoreBlock;
using AppLesson = Corvid.App.Core.Memory.Lesson;
using EngineCoreBlock = Corvid.Memory.CoreBlock;
using EngineLesson = Corvid.Memory.Lesson;

namespace Corvid.App.Engine.Memory;

/// <summary>
/// The Memory page over the runtime's memory store (docs/plan/ui.md §4.9, F11): the writes the <c>memory_op</c> gate
/// staged, the core blocks, the fact graph, the lessons of the workspace's project and the Hermes import. Approving is the
/// write, through the store's own writer (<see cref="MemoryWriter"/>, <see cref="LessonWriter"/>), so the gate's decision
/// records its outcome; a corrected text is written as corrected (G-8).
/// <para>
/// Shown as the engine keeps it, not guessed: the nearest facts of a staged write, scored by the store's search as it
/// stands now (the pending row keeps only their ids), who wrote a block (only the persona is the user's alone), and a block update as the line it adds
/// when it appends to the block, or as the whole block when it rewrites it. Consolidation is refused: the rewrite job
/// (plan §9.5) is not built.
/// </para>
/// </summary>
public sealed class EngineMemoryService : IMemoryService, IDisposable
{
    /// <summary>The session an import's staged entries are recorded in (the window has no current session for them).</summary>
    public const string ImportSession = "app:memory-import";

    /// <summary>The refusal Consolidate shows.</summary>
    public const string NoConsolidation =
        "Consolidation needs the block rewrite job (plan §9.5), which the engine does not have yet: correct the update by hand, or reject it.";

    private const int FactLimit = 200;
    private const int SearchTokens = 4_000;
    private const int NeighbourTokens = 400;
    private const int ScoredFacts = 20;

    private readonly AppEngine _engine;
    private readonly TimeProvider _time;
    private readonly Func<HermesHomeLocation> _hermes;
    private readonly ChangeSignal? _changed;

    /// <param name="hermes">Finds the Hermes home the import reads; the user's (<see cref="HermesHome.Resolve"/>) by default.</param>
    /// <param name="dispatcher">
    /// The UI thread <see cref="Changed"/> is raised on; without one it is never raised (a service used off the window).
    /// </param>
    public EngineMemoryService(AppEngine engine, TimeProvider time, Func<HermesHomeLocation>? hermes = null, IDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _time = time;
        _hermes = hermes ?? (() => HermesHome.Resolve(CorvidPaths.Home));
        if (dispatcher is not null)
        {
            _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
            _engine.MemoryChanged += OnMemoryChanged;
        }
    }

    /// <summary>
    /// What this process staged or wrote outside the page (<see cref="AppEngine.MemoryChanged"/>), posted to the UI thread and
    /// coalesced. A write another process makes is seen at the page's next load.
    /// </summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_changed is null)
            return;
        _engine.MemoryChanged -= OnMemoryChanged;
        _changed.Dispose();
    }

    private void OnMemoryChanged() => _changed?.Raise();

    /// <summary>The status bar's engine line on the Memory page, once the runtime is up; null before (or when it failed).</summary>
    public static MemoryEngineStatus? Status(AppEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.Runtime.IsCompletedSuccessfully)
            return null;
        var runtime = engine.Runtime.Result;
        // A profile's id names its placement already ("qwen3.5-0.8b@cuda:0"); otherwise the placement follows it.
        var generator = !runtime.Registry.TryGet(ModelSlots.Generator, out var slot) ? "no generator"
            : slot.Default.Profile.Id.Contains(slot.Default.Profile.Placement, StringComparison.Ordinal) ? slot.Default.Profile.Id
            : $"{slot.Default.Profile.Id} · {slot.Default.Profile.Placement}";
        return new MemoryEngineStatus(generator, Models.EngineModelService.StatusDecider(runtime), runtime.SchedulerState);
    }

    public async Task<MemorySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var (runtime, memory) = await MemoryAsync().ConfigureAwait(false);
        return await Task.Run(() => SnapshotAsync(runtime, memory, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<FactWrite> ApproveFactAsync(string id, MemoryOperation operation, string text, string? targetFactId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);
        var (_, memory) = await MemoryAsync().ConfigureAwait(false);
        var writer = Writer(memory);
        Refuse(text);
        return await Task.Run(async () =>
        {
            var pending = Pending(writer, id, PendingKind.Fact);
            var corrected = string.Equals(text.Trim(), pending.Proposal.Text?.Trim(), StringComparison.Ordinal) ? null : text.Trim();
            var result = await writer.ApproveAsync(id, Operation(operation), targetFactId, corrected, cancellationToken).ConfigureAwait(false);
            return new FactWrite(result.Added is { } added ? Record(added) : null, result.Closed is { } closed ? Record(closed) : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppCoreBlock> ApproveBlockUpdateAsync(string id, string text, bool replacesBlock, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);
        var (_, memory) = await MemoryAsync().ConfigureAwait(false);
        var writer = Writer(memory);
        Refuse(text);
        return await Task.Run(async () =>
        {
            var pending = Pending(writer, id, PendingKind.Block);
            var proposal = pending.Proposal;
            var current = memory.Blocks.Get(proposal.Block!);
            // What the card's text stands for: the whole block (a consolidation, or an update that rewrites the block), or
            // the line an appending update adds. The content as staged is written exactly when nothing was changed.
            var added = Appended(current.Content, proposal.Content!);
            var edited = text.Trim();
            var unchanged = replacesBlock || added is null
                ? string.Equals(edited, proposal.Content!.Trim(), StringComparison.Ordinal)
                : string.Equals(edited, added, StringComparison.Ordinal);
            // Unchanged, the staged content is written exactly (not rebuilt around the line) and the decision is not corrected.
            var corrected = unchanged ? null : replacesBlock || added is null ? edited : Join(current.Content, edited);
            var result = await writer.ApproveAsync(id, null, null, corrected, cancellationToken).ConfigureAwait(false);
            return Block(memory.Blocks, result.Block ?? memory.Blocks.Get(proposal.Block!));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(string id, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (_, memory) = await MemoryAsync().ConfigureAwait(false);
        var writer = Writer(memory);
        Refuse(reason);
        await Task.Run(() => writer.Reject(id, string.IsNullOrWhiteSpace(reason) ? "rejected" : reason.Trim(), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BlockConsolidation> ConsolidateAsync(string updateId, CancellationToken cancellationToken) =>
        Task.FromException<BlockConsolidation>(new ServiceUnavailableException(NoConsolidation));

    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken)
    {
        var (_, memory) = await MemoryAsync().ConfigureAwait(false);
        return memory.Blocks.CountTokens(text ?? "");
    }

    public async Task<AppLesson> ApproveLessonAsync(string id, string? supersedesId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (runtime, memory) = await MemoryAsync().ConfigureAwait(false);
        var lessons = Lessons(runtime, memory);
        return await Task.Run(() =>
        {
            // The rule and its "applies when" are approved as staged, with no correction: one that hides a character is rejected.
            if (lessons.Store.Get(id) is { } candidate
                && new[] { candidate.Title, candidate.Body, candidate.Applicability }.Any(text => SafeText.HasHidden(text)))
                throw new ArgumentException("The lesson holds hidden characters, so it cannot be approved as it reads; reject it.", nameof(id));
            var approved = lessons.Approve(id, supersedesId, cancellationToken);
            return Lesson(lessons.Store, approved);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectLessonAsync(string id, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (runtime, memory) = await MemoryAsync().ConfigureAwait(false);
        var lessons = Lessons(runtime, memory);
        Refuse(reason);
        await Task.Run(() => lessons.Reject(id, string.IsNullOrWhiteSpace(reason) ? "rejected by the user" : reason.Trim(), cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FactRecord>> SearchFactsAsync(string query, CancellationToken cancellationToken)
    {
        var (_, memory) = await MemoryAsync().ConfigureAwait(false);
        var facts = new FactGraph(memory.Database);
        if (string.IsNullOrWhiteSpace(query))
            return await Task.Run(() => (IReadOnlyList<FactRecord>)[.. facts.Recent(FactLimit).Select(Record)], cancellationToken).ConfigureAwait(false);
        // Recall's own search: keywords, vectors and the graph, over the facts that hold now.
        var found = await Task.Run(() => memory.Retriever.RetrieveAsync(new RetrievalRequest(query.Trim(), SearchTokens, Kinds: [MemoryItemKind.Fact]),
            cancellationToken), cancellationToken).ConfigureAwait(false);
        return [.. found.Items.Where(item => item.Fact is not null).Select(item => Record(item.Fact!))];
    }

    public async Task<IReadOnlyList<ImportEntry>> DryRunImportAsync(CancellationToken cancellationToken)
    {
        await MemoryAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var scan = HermesMemoryImport.Scan(MemoriesDirectory());
            var entries = new List<ImportEntry>();
            foreach (var file in scan.Files.Where(file => file.Problem is not null))
                entries.Add(new ImportEntry(file.File, "") { Id = HermesMemoryImport.EntryId(file.File, ""), Refused = true, Reason = file.Problem! });
            entries.AddRange(scan.Entries.Select(entry => new ImportEntry(entry.File, entry.Text)
            {
                Id = entry.Id,
                Refused = entry.Flagged,
                Reason = entry.Flagged ? "the scanner flagged it: " + string.Join(", ", entry.Findings.Select(finding => finding.Rule).Distinct()) : "",
            }));
            return (IReadOnlyList<ImportEntry>)entries;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StagedFact>> ImportForReviewAsync(IReadOnlyList<string> entryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var (runtime, memory) = await MemoryAsync().ConfigureAwait(false);
        var writer = Writer(memory);
        return await Task.Run(async () =>
        {
            var wanted = entryIds.ToHashSet(StringComparer.Ordinal);
            // Read again: an entry changed since the dry run has another id, so only what the person saw is staged.
            var scan = HermesMemoryImport.Scan(MemoriesDirectory());
            var staged = new List<StagedFact>();
            foreach (var entry in scan.Entries.Where(entry => !entry.Flagged && wanted.Contains(entry.Id)))
            {
                var result = await HermesMemoryImport.StageAsync(writer, ImportSession, entry, cancellationToken).ConfigureAwait(false);
                if (result is { Status: MemoryImportStatus.Staged, Pending: { } pending })
                    staged.Add(Staged(new FactGraph(memory.Database), pending));
            }
            return (IReadOnlyList<StagedFact>)staged;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- reading ----

    private async Task<(CorvidRuntime Runtime, MemoryServices Memory)> MemoryAsync()
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        return runtime.Memory is { } memory ? (runtime, memory)
            : throw new ServiceUnavailableException("Memory is off (Corvid:Memory:Enabled is false): turn it on in Settings, then restart Corvid.");
    }

    private static MemoryWriter Writer(MemoryServices memory) => memory.Writer
        ?? throw new ServiceUnavailableException("Memory writes need the extractor slot: bind an extractor model to stage and review writes.");

    private static LessonWriter Lessons(CorvidRuntime runtime, MemoryServices memory) =>
        memory.Lessons is { } lessons && runtime.Project is not null ? lessons
            : throw new ServiceUnavailableException(memory.Lessons is null
                ? "Lessons are off (Corvid:Memory:Lessons:Enabled is false)."
                : "Lessons belong to a project, and this workspace has none.");

    private static PendingWrite Pending(MemoryWriter writer, string id, PendingKind kind) =>
        writer.GetPending(id) is { Status: PendingStatus.Pending } pending && pending.Kind == kind ? pending
            : throw new InvalidOperationException($"Nothing is staged as {id}: it may have been approved or rejected elsewhere.");

    /// <summary>Text a person approves may hide nothing (A1's rule); the page asks for a correction first, and so does this.</summary>
    private static void Refuse(string? text)
    {
        if (SafeText.HasHidden(text))
            throw new ArgumentException("The text holds hidden characters; correct it before it is written.", nameof(text));
    }

    private string MemoriesDirectory()
    {
        var location = _hermes();
        if (location.Home is not { } home)
            throw new InvalidOperationException(location.Problem ?? "The Hermes home could not be found.");
        var directory = Path.Combine(home, "memories");
        return Directory.Exists(directory) ? directory : throw new InvalidOperationException($"There is no Hermes memories directory at {directory}.");
    }

    private async Task<MemorySnapshot> SnapshotAsync(CorvidRuntime runtime, MemoryServices memory, CancellationToken cancellationToken)
    {
        var facts = new FactGraph(memory.Database);
        var pending = memory.Writer?.ListPending() ?? [];
        var stagedFacts = new List<StagedFact>();
        foreach (var write in pending.Where(write => write.Kind == PendingKind.Fact))
            stagedFacts.Add(Staged(facts, write, stagedFacts.Count < ScoredFacts ? await ScoresAsync(memory, write, cancellationToken).ConfigureAwait(false) : null));
        var blocks = memory.Blocks.Snapshot().Blocks;
        var location = _hermes();
        var project = runtime.Project;
        var lessons = memory.Lessons is { } writer && project is not null ? writer.Store.List(project.Id) : [];
        return new MemorySnapshot
        {
            StagedFacts = stagedFacts,
            BlockUpdates = [.. pending.Where(write => write.Kind == PendingKind.Block).Select(write => Update(memory.Blocks, write))],
            // In the store's order of importance (user, agent, persona), as the page lists them.
            Blocks = [.. blocks.OrderBy(block => CoreBlockNames.All.IndexOf(block.Name)).Select(block => Block(memory.Blocks, block))],
            Facts = [.. facts.Recent(FactLimit).Select(Record)],
            PendingLessons = [.. lessons.Where(lesson => lesson.State == LessonStates.Candidate).Select(lesson => Candidate(project!, lesson))],
            Lessons = [.. lessons.Where(lesson => lesson.State is LessonStates.Approved or LessonStates.Superseded)
                .OrderByDescending(lesson => lesson.ResolvedAt ?? lesson.CreatedAt).Select(lesson => Lesson(memory.Lessons!.Store, lesson))],
            Import = new ImportSource("Hermes", location.Home is { } home ? Path.Combine(home, "memories") : location.Problem ?? "not found",
                "MEMORY.md and USER.md become staged proposals, never writes. A dry run lists what would be staged; entries the scanner flags are refused."),
            Stats = new MemoryStats(facts.CountCurrent(), memory.Episodes.Count(), blocks.Length),
        };
    }

    /// <summary>
    /// How near each staged neighbour is now: the pending row keeps which facts were nearest, not how near, so the store's
    /// own hybrid search is asked again with the staged text and entities. A neighbour it no longer finds scores 0.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, double>?> ScoresAsync(MemoryServices memory, PendingWrite write, CancellationToken cancellationToken)
    {
        var proposal = write.Proposal;
        if (proposal.NeighbourIds.IsDefaultOrEmpty || proposal.Text is not { Length: > 0 } text)
            return null;
        var entities = proposal.Entities.IsDefault ? [] : proposal.Entities.Select(entity => new EntityRef(entity.Label, entity.Name)).ToImmutableArray();
        var found = await memory.Retriever.RetrieveAsync(new RetrievalRequest(text, NeighbourTokens, entities, [MemoryItemKind.Fact]), cancellationToken)
            .ConfigureAwait(false);
        return found.Items.Where(item => item.Fact is not null).GroupBy(item => item.Fact!.FactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Score), StringComparer.Ordinal);
    }

    private StagedFact Staged(FactGraph facts, PendingWrite write, IReadOnlyDictionary<string, double>? scores = null)
    {
        var proposal = write.Proposal;
        var neighbours = proposal.NeighbourIds.IsDefaultOrEmpty ? [] : facts.Get(proposal.NeighbourIds);
        // The statement is the staged text whole, as written; a correction edits that.
        return new StagedFact(write.PendingId, proposal.Text ?? "", "", "")
        {
            StagedAt = write.CreatedAt,
            StagedBy = write.SessionId,
            Why = write.Reason,
            Entities = [.. (proposal.Entities.IsDefault ? [] : proposal.Entities).Select(entity => new FactEntity(entity.Label, entity.Name))],
            ValidFrom = Date(proposal.ValidFrom ?? write.CreatedAt),
            Proposed = proposal.Operation switch
            {
                MemoryOperations.Update => MemoryOperation.Update,
                MemoryOperations.Invalidate => MemoryOperation.Invalidate,
                MemoryOperations.Noop => MemoryOperation.Noop,
                _ => MemoryOperation.Add,
            },
            Nearest = [.. neighbours.Select(fact => new NearbyFact(fact.FactId, fact.Text, Date(fact.ValidFrom),
                scores is not null && scores.TryGetValue(fact.FactId, out var score) ? score : 0))],
            SearchKind = scores is null ? "hybrid search" : "hybrid search · scored now",
            Evidence = proposal.SourceSequence is { } sequence ? $"{write.SessionId} event {sequence}" : "",
        };
    }

    private static StagedBlockUpdate Update(CoreBlockStore store, PendingWrite write)
    {
        var proposal = write.Proposal;
        var current = store.Get(proposal.Block!);
        var proposed = proposal.Content ?? "";
        var added = Appended(current.Content, proposed);
        return new StagedBlockUpdate(write.PendingId, proposal.Block!)
        {
            StagedAt = write.CreatedAt,
            StagedBy = write.SessionId,
            Why = write.Reason,
            Context = added is null ? "" : LastLine(current.Content),
            Added = added ?? proposed,
            TokensNow = store.CountTokens(current.Content),
            TokensAfter = store.CountTokens(proposed),
            Budget = current.BudgetTokens,
        };
    }

    /// <summary>What <paramref name="proposed"/> adds after <paramref name="current"/>, or null when it rewrites the block.</summary>
    private static string? Appended(string current, string proposed)
    {
        if (current.Length == 0 || !proposed.StartsWith(current, StringComparison.Ordinal) || proposed.Length == current.Length)
            return null;
        var added = proposed[current.Length..].Trim();
        return added.Length == 0 ? null : added;
    }

    private static string Join(string current, string line) => current.Length == 0 ? line : current.TrimEnd() + "\n" + line;

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static AppCoreBlock Block(CoreBlockStore store, EngineCoreBlock block) => new(block.Name, store.CountTokens(block.Content), block.BudgetTokens)
    {
        Revision = (int)Math.Min(block.Revision, int.MaxValue),
        LocalOnly = block.LocalOnly,
        UpdatedAt = block.UpdatedAt,
        // Only the user edits the persona; the other blocks' writer is not recorded.
        SetByUser = block.Name == CoreBlockNames.Persona && block.Revision > 0,
        Text = block.Content,
    };

    private DateOnly Date(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).DateTime);

    private FactRecord Record(Fact fact) => new(fact.FactId, fact.Text, Date(fact.ValidFrom))
    {
        ValidUntil = fact.ValidTo is { } until ? Date(until) : null,
        Recorded = Date(fact.RecordedAt),
        Invalidated = !fact.IsCurrent,
    };

    private static LessonCandidate Candidate(ProjectIdentity project, EngineLesson lesson) => new(lesson.LessonId, Rule(lesson), lesson.Applicability)
    {
        Project = ProjectName(project),
        Status = lesson.ReviewReason is { Length: > 0 } reason ? "waits for review · " + reason : "waits for review",
        Evidence = string.Join("; ", lesson.Evidence.Select(evidence => $"{evidence.SessionId} events {string.Join(", ", evidence.Positions)}")),
        Kind = lesson.Kind.Replace('_', ' '),
    };

    private AppLesson Lesson(LessonStore store, EngineLesson lesson) => new(lesson.LessonId, Rule(lesson), lesson.Applicability)
    {
        Project = lesson.ProjectId.Length > 12 ? lesson.ProjectId[..12] : lesson.ProjectId,
        Approved = Date(lesson.ResolvedAt ?? lesson.CreatedAt),
        SupersededBy = lesson.SupersededBy is { } newer && store.Get(newer) is { } replacement ? Rule(replacement) : lesson.SupersededBy,
    };

    /// <summary>The lesson as one rule: its title, then its body when it says more.</summary>
    private static string Rule(EngineLesson lesson) =>
        lesson.Body.Trim() is { Length: > 0 } body && !string.Equals(body, lesson.Title.Trim(), StringComparison.Ordinal)
            ? $"{lesson.Title.Trim()} {body.ReplaceLineEndings(" ")}"
            : lesson.Title.Trim();

    private static string ProjectName(ProjectIdentity project) => (project.Id.Length > 12 ? project.Id[..12] : project.Id) + $" ({project.Source})";

    private static string? Operation(MemoryOperation operation) => operation switch
    {
        MemoryOperation.Add => MemoryOperations.Add,
        MemoryOperation.Update => MemoryOperations.Update,
        MemoryOperation.Invalidate => MemoryOperations.Invalidate,
        MemoryOperation.Noop => MemoryOperations.Noop,
        _ => null,
    };
}
