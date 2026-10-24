namespace Corvid.App.Core.Skills;

/// <summary>
/// A skill in the library (<c>/skills list</c> and <c>show</c>): its SKILL.md, who last wrote it, its revision, how it is
/// used and whether the user pinned it. Every string but the revision is outside text: the agent, a hub or the user wrote
/// it, so the views clean or reveal it.
/// </summary>
/// <param name="Author">The author of SKILL.md's last commit: <c>user</c>, <c>agent</c> or <c>hub:&lt;source&gt;</c>.</param>
/// <param name="Revision">SKILL.md's last commit in the skills store's git repository.</param>
/// <param name="AgentOwned">Every file was last changed by the agent: the only skills the curator and the learning loop may change, archive included.</param>
/// <param name="Trusted">No file came from a hub. Loading a skill that is not trusted taints the session.</param>
/// <param name="EvalCases">How many cases its <c>evals/</c> folder holds.</param>
/// <param name="Content">The whole SKILL.md, frontmatter included.</param>
public sealed record SkillRecord(
    string Name,
    string Description,
    string Author,
    string Revision,
    DateTimeOffset? LastUsed,
    int Uses,
    bool Pinned,
    bool AgentOwned,
    bool Trusted,
    int EvalCases,
    string Content)
{
    /// <summary>The folder under the skills directory (<c>devops/deploy-k8s</c>).</summary>
    public string Path { get; init; } = "";

    /// <summary>The session that last loaded it, when the log says.</summary>
    public string? LastSession { get; init; }

    /// <summary>Tools it requires to be offered at all (frontmatter <c>requires_tools</c>).</summary>
    public IReadOnlyList<string> RequiresTools { get; init; } = [];

    /// <summary>The latest eval run's score, if one ran.</summary>
    public EvalScore? LastEval { get; init; }
}

/// <summary>Passed of total for one eval run.</summary>
public sealed record EvalScore(int Passed, int Total)
{
    public bool AllPassed => Passed == Total;
}

/// <summary>
/// What a replay of the source turn, or an eval case, did with the skill loaded, against what was expected (the engine's
/// <c>ReplayOutcome</c>). The engine's pending draft carries the replay only as its described line today; the structured
/// form is the seam <c>Corvid.App.Engine</c> fills.
/// </summary>
/// <param name="Expected">The tool names expected, in order.</param>
/// <param name="Actual">The tool names the turn called, in order.</param>
/// <param name="Status">How the turn ended: <c>completed</c>, <c>stopped</c>, <c>denied</c>.</param>
public sealed record ReplayResult(bool Passed, IReadOnlyList<string> Expected, IReadOnlyList<string> Actual, string Status, string Detail = "");

/// <summary>
/// A drafted skill waiting for review (<c>/skills pending</c>): <c>skill_write_safe</c> did not let it go live, so the user
/// approves it (it is written authored <c>agent</c>), edits it first, or rejects it with a reason.
/// </summary>
/// <param name="Skill">The skill's name, as its frontmatter gives it.</param>
/// <param name="Content">The SKILL.md that approving writes.</param>
/// <param name="SessionId">The session whose turn it was distilled from.</param>
/// <param name="Reason">Why it was staged rather than written.</param>
/// <param name="Replaces">The revision of the skill it would change, or null for a new skill.</param>
public sealed record SkillDraft(
    string Id,
    string Skill,
    string Content,
    string SessionId,
    string Reason,
    DateTimeOffset Created,
    ReplayResult Replay,
    string? Replaces = null)
{
    /// <summary>The turn the draft came from, for the card's head ("turn 6").</summary>
    public int? Turn { get; init; }

    /// <summary>The source events, for the evidence link ("events 412 to 466").</summary>
    public (long From, long Through)? Events { get; init; }

    /// <summary>The SKILL.md it would replace, for the diff an update shows; null for a new skill.</summary>
    public string? Previous { get; init; }
}

/// <summary>A commit in the skills store's history (<c>/skills log</c>).</summary>
/// <param name="Author">Who the commit is authored as: <c>user</c>, <c>agent</c> or <c>hub:&lt;source&gt;</c>.</param>
/// <param name="Skills">The skills it touched.</param>
public sealed record SkillCommit(string Revision, string Author, DateTimeOffset Date, string Subject, IReadOnlyList<string> Skills);

/// <summary>A file a commit changed, before and after (<c>/skills diff</c>).</summary>
public sealed record SkillCommitFile(string Path, string? OldText, string? NewText);

/// <summary>A commit with its changed files.</summary>
public sealed record SkillCommitDetail(SkillCommit Commit, IReadOnlyList<SkillCommitFile> Files);

/// <summary>One eval case's outcome.</summary>
public sealed record EvalCaseResult(string Case, ReplayResult Outcome);

/// <summary>An eval run of a skill at one revision (<c>/skills evals</c>).</summary>
public sealed record EvalRun(string Skill, string Revision, DateTimeOffset At, IReadOnlyList<EvalCaseResult> Cases)
{
    public int Passed => Cases.Count(c => c.Outcome.Passed);

    public EvalScore Score => new(Passed, Cases.Count);
}

/// <summary>A scanner finding in a skill offered for import (the engine's <c>ScanFinding</c>).</summary>
/// <param name="File">Relative to the skill folder.</param>
/// <param name="Rule">The scanner's rule: <c>credential_read</c>, <c>invisible_character</c>, <c>download_to_shell</c>…</param>
/// <param name="Excerpt">The matched text, as the scanner quotes it: outside text.</param>
public sealed record SkillScanFinding(string File, int Line, string Rule, string Excerpt);

/// <summary>A skill folder an import found, with what the scanner found in it; any finding refuses it.</summary>
public sealed record ImportCandidate(string Id, string Name, string Path, string Description, IReadOnlyList<SkillScanFinding> Findings)
{
    public bool IsRefused => Findings.Count > 0;
}

/// <summary>Where an import reads from.</summary>
public enum SkillImportKind
{
    /// <summary>Hermes' skills folder (<c>/skills import hermes</c>), imported as <c>hub:hermes</c>.</summary>
    Hermes,

    /// <summary>A folder of skill folders (<c>/skills import &lt;folder&gt; [source]</c>), imported as <c>hub:&lt;source&gt;</c>.</summary>
    Folder,
}

/// <summary>A dry run of an import: what would be imported, as whom, and what the scanner refused.</summary>
/// <param name="Location">The folder read, as the user would write it.</param>
/// <param name="Author">What the imported skills would be authored as: <c>hub:hermes</c>, <c>hub:local</c>.</param>
public sealed record ImportScan(SkillImportKind Kind, string Location, string Author, IReadOnlyList<ImportCandidate> Candidates);

/// <summary>What an import did.</summary>
public sealed record SkillImportResult(IReadOnlyList<SkillRecord> Imported, IReadOnlyList<string> Refused, string? Revision);

/// <summary>What the Skills page loads: the library, the drafts, the folder and the curator's idle limit.</summary>
/// <param name="Root">The skills folder, as the user would write it.</param>
/// <param name="Issues">SKILL.md files the loader could not use, and why.</param>
/// <param name="StaleDays">How long an agent skill may sit unused before the curator offers to archive it.</param>
public sealed record SkillsSnapshot(
    IReadOnlyList<SkillRecord> Skills,
    IReadOnlyList<SkillDraft> Drafts,
    string Root,
    IReadOnlyList<string> Issues,
    int StaleDays = 90);
