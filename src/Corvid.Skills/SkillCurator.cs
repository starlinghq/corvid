using System.Collections.Immutable;

namespace Corvid.Skills;

/// <summary>One skill's eval run: each case's outcome.</summary>
public sealed record SkillEvalReport(string Skill, string Revision, ImmutableArray<(SkillEvalCase Case, ReplayOutcome Outcome)> Cases)
{
    public int Passed => Cases.Count(item => item.Outcome.Passed);
}

/// <summary>
/// The skill curator (plan §9.5, §11). It runs a skill's <c>evals/</c> cases after the skill changes, and archives stale
/// skills: agent-authored, not pinned, and unused (or, never used, unchanged) for longer than the idle limit. Like the
/// learning loop it may change only agent-authored skills; the store enforces that under the repository lock. Proposing
/// merges is not built yet.
/// </summary>
public sealed class SkillCurator(SkillStore store, SkillsDatabase database, SkillReplayer replayer, TimeProvider? time = null)
{
    private readonly SkillStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SkillsDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly SkillReplayer _replayer = replayer ?? throw new ArgumentNullException(nameof(replayer));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Runs the skill's eval cases and records the result; null when it has none.</summary>
    public async Task<SkillEvalReport?> RunEvalsAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGet(name, out var skill))
            throw new ArgumentException($"There is no skill named '{name}'.", nameof(name));
        if (skill.Evals.IsEmpty)
            return null;
        var revision = (await _store.Repository.LogAsync(skill.Folder, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Revision ?? "uncommitted";
        var cases = ImmutableArray.CreateBuilder<(SkillEvalCase, ReplayOutcome)>();
        foreach (var evalCase in skill.Evals)
            cases.Add((evalCase, await _replayer.EvalAsync(skill.Document, evalCase, cancellationToken).ConfigureAwait(false)));
        var report = new SkillEvalReport(skill.Name, revision, cases.ToImmutable());
        _database.RecordEval(new EvalRecord(skill.Name, revision, report.Passed, report.Cases.Length,
            string.Join("; ", report.Cases.Select(item => $"{item.Item1.Id}: {item.Item2.Describe()}")), _time.GetUtcNow()));
        return report;
    }

    /// <summary>Stale agent-authored skills, by the idle rule; pinned skills and anything the user or a hub wrote never appear.</summary>
    public async Task<IReadOnlyList<Skill>> StaleAsync(TimeSpan idle, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var stale = new List<Skill>();
        foreach (var skill in _store.Skills.Where(skill => skill.AgentOwned))
        {
            var usage = _database.Usage(skill.Name);
            if (usage.Pinned)
                continue;
            var last = usage.LastUsed ?? (await _store.Repository.LogAsync(skill.Folder, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Date;
            if (last is { } at && now - at > idle)
                stale.Add(skill);
        }
        return stale;
    }

    /// <summary>Archives every stale skill (moves it to <c>.archive/</c> as the agent) and returns their names.</summary>
    public async Task<IReadOnlyList<string>> ArchiveStaleAsync(TimeSpan idle, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var archived = new List<string>();
        foreach (var skill in await StaleAsync(idle, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _store.ArchiveAsync(skill.Name, sessionId, cancellationToken).ConfigureAwait(false);
                archived.Add(skill.Name);
            }
            catch (SkillOwnershipException)
            {
                // Edited by the user since the scan: no longer the curator's to move.
            }
        }
        return archived;
    }
}
