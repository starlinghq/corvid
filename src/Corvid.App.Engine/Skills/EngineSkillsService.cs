using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Skills;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Skills;
using AppSkillCommit = Corvid.App.Core.Skills.SkillCommit;
using EngineSkillCommit = Corvid.Skills.SkillCommit;

namespace Corvid.App.Engine.Skills;

/// <summary>
/// The Skills page over the runtime's skills store (docs/plan/ui.md §4.9, F12): the library from <see cref="SkillStore"/>,
/// the learning loop's drafts (<see cref="SkillLearner"/>), the store's git history, eval runs (<see cref="SkillCurator"/>),
/// pins and archiving, and imports as a dry run first. Every change is the person acting: authored <c>user</c>, except an
/// approved draft left as written (the agent's) and an import (<c>hub:&lt;source&gt;</c>). An edited draft is written as
/// the user's.
/// <para>
/// A draft's replay and an eval run's cases are read back from the line the engine stored (<see cref="ReplayOutcome.Parse"/>);
/// a line not in that form shows as failed with the line as its detail. Changes made here record no <c>skill_changed</c>
/// event, since the page acts for no session.
/// </para>
/// </summary>
public sealed class EngineSkillsService : ISkillsService
{
    private const int MaxDrafts = 100;

    private readonly AppEngine _engine;
    private readonly TimeProvider _time;
    private readonly Func<string?, HermesHomeLocation> _hermes;

    /// <param name="hermes">Finds the Hermes home a Hermes import reads, given the one the person named; the user's by default.</param>
    public EngineSkillsService(AppEngine engine, TimeProvider time, Func<string?, HermesHomeLocation>? hermes = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _time = time;
        _hermes = hermes ?? (given => HermesHome.Resolve(CorvidPaths.Home, given is null ? null : CorvidPaths.ExpandHome(given)));
    }

    public Task<SkillsSnapshot> LoadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);

    private async Task<SkillsSnapshot> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var skills = await SkillsAsync().ConfigureAwait(false);
        var store = skills.Store!;
        await store.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<SkillRecord>();
        foreach (var skill in store.Skills)
            records.Add(await RecordAsync(store, skills.Database, skill, cancellationToken).ConfigureAwait(false));
        var drafts = new List<SkillDraft>();
        foreach (var pending in (skills.Database?.Pending() ?? []).Take(MaxDrafts))
            drafts.Add(await DraftAsync(store, pending, cancellationToken).ConfigureAwait(false));
        return new SkillsSnapshot(records, drafts, store.Root, [.. store.Issues.Select(issue => $"{issue.Path}: {issue.Problem}")]);
    }

    public Task<SkillRecord> ApproveDraftAsync(string draftId, string? editedContent, CancellationToken cancellationToken) =>
        Task.Run(() => ApproveDraftCoreAsync(draftId, editedContent, cancellationToken), cancellationToken);

    private async Task<SkillRecord> ApproveDraftCoreAsync(string draftId, string? editedContent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        var skills = await SkillsAsync().ConfigureAwait(false);
        var learner = Learner(skills);
        var pending = skills.Database!.FindPending(draftId) is { Status: PendingSkillStatus.Pending } found ? found
            : throw new InvalidOperationException($"No draft '{draftId}' is waiting for review.");
        // As the draft shows it: the joiners Persian and emoji need stay; anything else hidden is refused.
        if (SkillsText.HasHiddenContent(editedContent ?? pending.Content) || SafeText.HasHidden(pending.Skill))
            throw new InvalidOperationException("The SKILL.md holds hidden characters; the scanner refuses it.");
        await learner.ApproveAsync(draftId, null, editedContent, cancellationToken).ConfigureAwait(false);
        return skills.Store!.TryGet(pending.Skill, out var skill)
            ? await RecordAsync(skills.Store, skills.Database, skill, cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException($"{pending.Skill} was written but did not load; see the library's issues.");
    }

    public Task RejectDraftAsync(string draftId, string reason, CancellationToken cancellationToken) =>
        Task.Run(() => RejectDraftCoreAsync(draftId, reason, cancellationToken), cancellationToken);

    private async Task RejectDraftCoreAsync(string draftId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        if (SafeText.HasHidden(reason))
            throw new ArgumentException("The reason holds hidden characters; it is not stored.", nameof(reason));
        var skills = await SkillsAsync().ConfigureAwait(false);
        var learner = Learner(skills);
        await Task.Run(() => learner.Reject(draftId, reason, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AppSkillCommit>> LogAsync(string? skill, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => LogCoreAsync(skill, limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<AppSkillCommit>> LogCoreAsync(string? skill, int limit, CancellationToken cancellationToken)
    {
        var skills = await SkillsAsync().ConfigureAwait(false);
        var store = skills.Store!;
        string? folder = null;
        if (skill is not null)
            folder = store.TryGet(skill, out var found) ? found.Folder : throw new InvalidOperationException($"There is no skill named '{skill}'.");
        var log = await store.Repository.LogWithFilesAsync(folder, limit, cancellationToken).ConfigureAwait(false);
        return [.. log.Select(entry => Commit(store, entry.Commit, entry.Files))];
    }

    public Task<SkillCommitDetail> ShowAsync(string revision, CancellationToken cancellationToken) =>
        Task.Run(() => ShowCoreAsync(revision, cancellationToken), cancellationToken);

    private async Task<SkillCommitDetail> ShowCoreAsync(string revision, CancellationToken cancellationToken)
    {
        var skills = await SkillsAsync().ConfigureAwait(false);
        var store = skills.Store!;
        var shown = await store.Repository.ShowFilesAsync(revision, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No revision {revision} in the skills store.");
        return new SkillCommitDetail(Commit(store, shown.Commit, [.. shown.Files.Select(file => file.Path)]),
            [.. shown.Files.Select(file => new SkillCommitFile(file.Path, file.Before, file.After))]);
    }

    public Task<AppSkillCommit> RevertAsync(string revision, CancellationToken cancellationToken) =>
        Task.Run(() => RevertCoreAsync(revision, cancellationToken), cancellationToken);

    private async Task<AppSkillCommit> RevertCoreAsync(string revision, CancellationToken cancellationToken)
    {
        var skills = await SkillsAsync().ConfigureAwait(false);
        var store = skills.Store!;
        var reverted = await store.RevertAsync(revision, null, cancellationToken).ConfigureAwait(false);
        var shown = await store.Repository.ShowFilesAsync(reverted, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The revert {reverted} is not in the skills store.");
        return Commit(store, shown.Commit, [.. shown.Files.Select(file => file.Path)]);
    }

    public Task<IReadOnlyList<EvalRun>> EvalHistoryAsync(string skill, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => EvalHistoryCoreAsync(skill, limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<EvalRun>> EvalHistoryCoreAsync(string skill, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skill);
        var skills = await SkillsAsync().ConfigureAwait(false);
        var database = skills.Database ?? throw new ServiceUnavailableException("Skill evals need the skills database, which is not open.");
        return await Task.Run(() => (IReadOnlyList<EvalRun>)[.. database.Evals(skill, limit).Select(Run)], cancellationToken).ConfigureAwait(false);
    }

    public Task<EvalRun?> RunEvalsAsync(string skill, CancellationToken cancellationToken) =>
        Task.Run(() => RunEvalsCoreAsync(skill, cancellationToken), cancellationToken);

    private async Task<EvalRun?> RunEvalsCoreAsync(string skill, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skill);
        var skills = await SkillsAsync().ConfigureAwait(false);
        var curator = skills.Curator ?? throw new ServiceUnavailableException("Skill evals need the learning services, which are not running.");
        // The evals run turns on the background generator: never on the caller's thread.
        var report = await Task.Run(() => curator.RunEvalsAsync(skill, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (report is null)
            return null;
        var at = skills.Database?.LastEval(skill)?.At ?? _time.GetUtcNow();
        return new EvalRun(report.Skill, report.Revision, at, [.. report.Cases.Select(item => new EvalCaseResult(item.Case.Id, Replay(item.Outcome)))]);
    }

    public Task SetPinnedAsync(string skill, bool pinned, CancellationToken cancellationToken) =>
        Task.Run(() => SetPinnedCoreAsync(skill, pinned, cancellationToken), cancellationToken);

    private async Task SetPinnedCoreAsync(string skill, bool pinned, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skill);
        var skills = await SkillsAsync().ConfigureAwait(false);
        if (!skills.Store!.TryGet(skill, out _))
            throw new InvalidOperationException($"There is no skill named '{skill}'.");
        var database = skills.Database ?? throw new ServiceUnavailableException("Pins live in the skills database, which is not open.");
        await Task.Run(() => database.SetPinned(skill, pinned), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ArchiveAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken) =>
        Task.Run(() => ArchiveCoreAsync(skills, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<string>> ArchiveCoreAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var services = await SkillsAsync().ConfigureAwait(false);
        var store = services.Store!;
        var known = skills.Where(name => store.TryGet(name, out _)).Distinct(StringComparer.Ordinal).ToList();
        // A name the store does not hold (archived, removed or renamed since the page loaded) is reported, not dropped.
        if (skills.Where(name => !known.Contains(name, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
            throw new InvalidOperationException(
                $"There is no skill named {string.Join(", ", unknown.Select(name => SafeText.Reveal(name)))} any more (archived or removed since the page loaded); nothing was archived. Refresh the library.");
        // All or nothing, as the page asked: a skill the curator may not move stops the lot before anything moves.
        foreach (var name in known)
        {
            store.TryGet(name, out var skill);
            var pinned = services.Database?.Usage(name).Pinned == true;
            if (!skill.AgentOwned || pinned)
                throw new InvalidOperationException($"{name} is {(pinned ? "pinned" : "not wholly the agent's")}; the curator archives only unpinned agent skills.");
        }
        var archived = new List<string>();
        foreach (var name in known)
        {
            await store.ArchiveAsync(name, null, cancellationToken).ConfigureAwait(false);
            archived.Add(name);
        }
        return archived;
    }

    public Task<ImportScan> ScanImportAsync(SkillImportKind kind, string? folder, string? source, CancellationToken cancellationToken) =>
        Task.Run(() => ScanImportCoreAsync(kind, folder, source, cancellationToken), cancellationToken);

    private async Task<ImportScan> ScanImportCoreAsync(SkillImportKind kind, string? folder, string? source, CancellationToken cancellationToken)
    {
        // The folder is what is read and the source the author written into git: neither may hide anything.
        if (SafeText.HasHidden(folder))
            throw new ArgumentException("The folder holds hidden characters.", nameof(folder));
        if (kind == SkillImportKind.Folder && SafeText.HasHidden(source))
            throw new ArgumentException("The hub name holds hidden characters.", nameof(source));
        var skills = await SkillsAsync().ConfigureAwait(false);
        string location;
        string author;
        if (kind == SkillImportKind.Hermes)
        {
            var hermes = _hermes(string.IsNullOrWhiteSpace(folder) ? null : folder.Trim());
            location = hermes.Home is { } home ? Path.Combine(home, "skills") : throw new InvalidOperationException(hermes.Problem ?? "The Hermes home could not be found.");
            author = SkillAuthor.Hub("hermes").Name;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("Give the folder that holds the skill folders, for example ~/skills.", nameof(folder));
            location = Path.GetFullPath(CorvidPaths.ExpandHome(folder.Trim()));
            author = SkillAuthor.Hub(string.IsNullOrWhiteSpace(source) ? "local" : source.Trim()).Name;
        }
        if (!Directory.Exists(location))
            throw new InvalidOperationException($"There is no folder at {location}.");
        var previews = await skills.Store!.PreviewImportAsync(location, cancellationToken).ConfigureAwait(false);
        return new ImportScan(kind, location, author, [.. previews.Select(Candidate)]);
    }

    public Task<SkillImportResult> ImportAsync(ImportScan scan, IReadOnlyList<string> candidateIds, CancellationToken cancellationToken) =>
        Task.Run(() => ImportCoreAsync(scan, candidateIds, cancellationToken), cancellationToken);

    private async Task<SkillImportResult> ImportCoreAsync(ImportScan scan, IReadOnlyList<string> candidateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (SafeText.HasHidden(scan.Author) || SafeText.HasHidden(scan.Location))
            throw new ArgumentException("The dry run's source or folder holds hidden characters; nothing is imported.", nameof(scan));
        if (!scan.Author.StartsWith("hub:", StringComparison.Ordinal))
            throw new ArgumentException("An import is authored by a hub (hub:<source>).", nameof(scan));
        var skills = await SkillsAsync().ConfigureAwait(false);
        var store = skills.Store!;
        var wanted = candidateIds.ToHashSet(StringComparer.Ordinal);
        var refused = new List<string>();
        var picked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in scan.Candidates.Where(candidate => wanted.Contains(candidate.Id)))
        {
            if (candidate.IsRefused || SafeText.HasHidden(candidate.Name))
                refused.Add($"{candidate.Path}: refused by the dry run");
            else
                picked[candidate.Path] = Fingerprint(candidate.Id);
        }
        if (picked.Count == 0)
            return new SkillImportResult([], refused, null);
        var result = await store.ImportAsync(scan.Location, SkillAuthor.Hub(scan.Author["hub:".Length..]), null, picked, cancellationToken).ConfigureAwait(false);
        refused.AddRange(result.Refused.Select(issue => $"{issue.Path}: {issue.Problem}"));
        var imported = new List<SkillRecord>();
        foreach (var name in result.Imported)
            if (store.TryGet(name, out var skill))
                imported.Add(await RecordAsync(store, skills.Database, skill, cancellationToken).ConfigureAwait(false));
        return new SkillImportResult(imported, refused, result.Revision);
    }

    // ---- mapping ----

    private async Task<SkillServices> SkillsAsync()
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        return runtime.Skills is { Store: not null } skills ? skills
            : throw new ServiceUnavailableException($"Skills are {runtime.Skills.State}.");
    }

    private static SkillLearner Learner(SkillServices skills) => skills.Learner
        ?? throw new ServiceUnavailableException("The learning loop is off (Corvid:Skills:Learning is false), so drafts cannot be reviewed here.");

    private static async Task<SkillRecord> RecordAsync(SkillStore store, SkillsDatabase? database, Skill skill, CancellationToken cancellationToken)
    {
        var last = (await store.Repository.LogAsync(skill.Folder, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var usage = database?.Usage(skill.Name);
        var eval = database?.LastEval(skill.Name);
        return new SkillRecord(skill.Name, skill.Document.Description, skill.Author?.Name ?? "uncommitted", last?.Revision ?? "uncommitted",
            usage?.LastUsed, usage?.Uses ?? 0, usage?.Pinned == true, skill.AgentOwned, skill.Trusted, skill.Evals.Length, skill.Document.Text)
        {
            Path = skill.Path,
            RequiresTools = skill.Document.Activation.RequiresTools.IsDefaultOrEmpty ? [] : [.. skill.Document.Activation.RequiresTools],
            LastEval = eval is null ? null : new EvalScore(eval.Passed, eval.Total),
        };
    }

    private static async Task<SkillDraft> DraftAsync(SkillStore store, PendingSkill pending, CancellationToken cancellationToken)
    {
        string? replaces = null, previous = null;
        if (store.TryGet(pending.Skill, out var existing))
        {
            replaces = (await store.Repository.LogAsync(existing.Folder, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Revision ?? "uncommitted";
            previous = existing.Document.Text;
        }
        return new SkillDraft(pending.Id, pending.Skill, pending.Content, pending.SessionId, pending.Reason, pending.Created,
            ReplayOutcome.Parse(pending.Replay) is { } outcome ? Replay(outcome) : new ReplayResult(false, [], [], "unknown", pending.Replay), replaces)
        {
            Previous = previous,
        };
    }

    private static ReplayResult Replay(ReplayOutcome outcome) => new(outcome.Passed, [.. outcome.Expected], [.. outcome.Actual], outcome.Status, outcome.Detail);

    private static EvalRun Run(EvalRecord record)
    {
        var cases = SkillEvalReport.ParseDetail(record.Detail);
        // A record whose cases cannot be read back keeps its score: each case is listed as the record counts it.
        IReadOnlyList<EvalCaseResult> results = cases.Length == record.Total
            ? [.. cases.Select(item => new EvalCaseResult(item.Case, Replay(item.Outcome)))]
            : [.. Enumerable.Range(0, record.Total).Select(i => new EvalCaseResult($"case {i + 1} (not recorded by name)",
                new ReplayResult(i < record.Passed, [], [], "unknown", i == 0 ? record.Detail : "")))];
        return new EvalRun(record.Skill, record.Revision, record.At, results);
    }

    /// <summary>A commit with the skills it touched: the library's skill whose folder holds each file, else the file's first folder.</summary>
    private static AppSkillCommit Commit(SkillStore store, EngineSkillCommit commit, IReadOnlyList<string> files)
    {
        var folders = store.Skills.Select(skill => (skill.Path + "/", skill.Name)).ToList();
        var names = files.Select(file => folders.FirstOrDefault(folder => file.StartsWith(folder.Item1, StringComparison.Ordinal)).Name
                ?? (file.Contains('/', StringComparison.Ordinal) ? file[..file.IndexOf('/', StringComparison.Ordinal)] : file))
            .Distinct(StringComparer.Ordinal);
        return new AppSkillCommit(commit.Revision, commit.Author, commit.Date, commit.Subject, [.. names]);
    }

    private static ImportCandidate Candidate(ImportPreview preview)
    {
        var findings = preview.Findings.Select(finding => new SkillScanFinding(finding.File, finding.Line, finding.Rule, finding.Excerpt)).ToList();
        // A refusal the scanner did not make (a link, a name in use, too much content) is listed as a finding too, so it refuses.
        if (preview.Problem is { } problem)
            findings.Add(new SkillScanFinding(SkillDocument.FileName, 0, "not_importable", problem));
        return new ImportCandidate(CandidateId(preview), preview.Name ?? preview.Label, preview.Label, preview.Description, findings);
    }

    /// <summary>The candidate's id carries its fingerprint, so the import checks the folder against what the dry run read.</summary>
    private static string CandidateId(ImportPreview preview) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Label)))[..8] + ":" + preview.Fingerprint;

    private static string Fingerprint(string candidateId) => candidateId[(candidateId.IndexOf(':', StringComparison.Ordinal) + 1)..];
}
