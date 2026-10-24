namespace Corvid.App.Core.Skills;

/// <summary>
/// The skills store as the Skills page sees it: what <c>/skills</c> offers (src/Corvid.Host/Commands/SkillCommands.cs:
/// list, show, pending, approve, reject, log, diff, revert, import, evals, pin, unpin, stale), behind one seam. The demo
/// serves it in memory; <c>Corvid.App.Engine</c> will over <c>SkillStore</c>, <c>SkillLearner</c>, <c>SkillCurator</c>
/// and <c>SkillsDatabase</c>. Every change made here is the user acting, so it is authored <c>user</c>, except an approved
/// draft, which stays <c>agent</c>, and an import, which is <c>hub:&lt;source&gt;</c>.
/// </summary>
public interface ISkillsService
{
    /// <summary>The library and the drafts waiting for review.</summary>
    Task<SkillsSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a draft as the agent's skill and returns it as stored. With <paramref name="editedContent"/>, the user's
    /// edit is written instead of the draft (the engine's approve takes the draft as staged today: an edited approval is
    /// a seam for <c>Corvid.App.Engine</c>). The engine then runs the skill's evals.
    /// </summary>
    Task<SkillRecord> ApproveDraftAsync(string draftId, string? editedContent, CancellationToken cancellationToken);

    /// <summary>Rejects a draft; the reason is recorded with it (the engine records the rejection; the reason is the seam).</summary>
    Task RejectDraftAsync(string draftId, string reason, CancellationToken cancellationToken);

    /// <summary>The store's history, newest first, or one skill's; at most <paramref name="limit"/> commits.</summary>
    Task<IReadOnlyList<SkillCommit>> LogAsync(string? skill, int limit, CancellationToken cancellationToken);

    /// <summary>A commit's changed files.</summary>
    Task<SkillCommitDetail> ShowAsync(string revision, CancellationToken cancellationToken);

    /// <summary>Reverts a commit as the user and returns the new commit; the files it touched become the user's.</summary>
    Task<SkillCommit> RevertAsync(string revision, CancellationToken cancellationToken);

    /// <summary>A skill's eval runs, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<EvalRun>> EvalHistoryAsync(string skill, int limit, CancellationToken cancellationToken);

    /// <summary>Runs a skill's evals at its current revision; null when it has no <c>evals/</c> cases.</summary>
    Task<EvalRun?> RunEvalsAsync(string skill, CancellationToken cancellationToken);

    /// <summary>Pins (the curator never touches it) or unpins a skill.</summary>
    Task SetPinnedAsync(string skill, bool pinned, CancellationToken cancellationToken);

    /// <summary>Moves skills to <c>.archive/</c>: only agent-authored, unpinned ones; returns the names archived.</summary>
    Task<IReadOnlyList<string>> ArchiveAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken);

    /// <summary>
    /// A dry run of an import: the skill folders found and the scanner's findings, nothing imported. The engine imports
    /// and refuses in one step today; the dry run is a seam for <c>Corvid.App.Engine</c>.
    /// </summary>
    /// <param name="folder">The folder for <see cref="SkillImportKind.Folder"/>; Hermes' home for <see cref="SkillImportKind.Hermes"/>, or null for the default.</param>
    /// <param name="source">The hub name a folder import is authored as (<c>hub:&lt;source&gt;</c>); <c>local</c> by default.</param>
    Task<ImportScan> ScanImportAsync(SkillImportKind kind, string? folder, string? source, CancellationToken cancellationToken);

    /// <summary>Imports what a dry run passed, by the candidates' ids; anything that changed since is refused, not imported.</summary>
    Task<SkillImportResult> ImportAsync(ImportScan scan, IReadOnlyList<string> candidateIds, CancellationToken cancellationToken);
}
