using System.Collections.ObjectModel;
using System.Globalization;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Skills;

/// <summary>The Skills page's tabs, in their order (Ctrl+1 to Ctrl+4).</summary>
public enum SkillsTabKind
{
    Review,
    Library,
    History,
    Import,
}

/// <summary>A tab of the page head, with the count it carries ("Review 1", "Library 8").</summary>
public sealed class SkillsTab : ObservableObject
{
    private bool _isSelected;
    private int _count;

    internal SkillsTab(SkillsTabKind kind, string label, bool countsWaiting, Action<SkillsTabKind> open)
    {
        Kind = kind;
        Label = label;
        CountsWaiting = countsWaiting;
        Open = new RelayCommand(() => open(kind));
    }

    public SkillsTabKind Kind { get; }

    public string Label { get; }

    /// <summary>The count is of what waits on the user (Review), not of what is there (Library).</summary>
    public bool CountsWaiting { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int Count
    {
        get => _count;
        set
        {
            if (!SetProperty(ref _count, value))
                return;
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public string BadgeText => _count > 0 ? _count.ToString(CultureInfo.InvariantCulture) : "";

    public string AutomationName => _count <= 0 ? Label : CountsWaiting ? $"{Label}, {_count} waiting" : $"{Label}, {_count}";

    public RelayCommand Open { get; }
}

/// <summary>
/// A row of the library table: name, author, revision, last use, uses and the latest evals; pinned and outside-content
/// tags. The name is revealed (pin, archive and evals act on it); the description is cleaned.
/// </summary>
public sealed class SkillRowView : ObservableObject
{
    private bool _isSelected;

    internal SkillRowView(SkillRecord record, TimeProvider time, int staleDays, Action<SkillRowView> select)
    {
        Record = record;
        Name = SafeText.Reveal(record.Name);
        Description = SafeText.Clean(record.Description);
        Author = SafeText.Clean(record.Author);
        AuthorTone = SkillsText.AuthorTone(record.Author);
        AuthorMeaning = SkillsText.AuthorMeaning(record.Author);
        Revision = SkillsText.Short(record.Revision);
        LastUsed = SkillsText.When(record.LastUsed, time);
        IdleDays = SkillsText.IdleDays(record.LastUsed, time);
        UsesText = record.Uses.ToString("#,0", CultureInfo.InvariantCulture);
        EvalText = record.EvalCases == 0 ? "none" : SkillsText.Score(record.LastEval);
        EvalTone = record.EvalCases == 0 ? Tone.Grey : SkillsText.ScoreTone(record.LastEval);
        // A skill never used has no idle time to measure (it may have been approved a minute ago).
        IsStale = record.AgentOwned && !record.Pinned && record.LastUsed is not null && IdleDays >= staleDays;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public SkillRecord Record { get; }

    public string Name { get; }

    public string Description { get; }

    public string Author { get; }

    public Tone AuthorTone { get; }

    public string AuthorMeaning { get; }

    public string Revision { get; }

    public string LastUsed { get; }

    public int IdleDays { get; }

    public string UsesText { get; }

    public string EvalText { get; }

    public Tone EvalTone { get; }

    public bool HasEvals => Record.EvalCases > 0;

    public bool IsPinned => Record.Pinned;

    /// <summary>From a hub: loading it taints the session.</summary>
    public bool IsOutside => !Record.Trusted;

    /// <summary>Agent-authored, unpinned and idle past the curator's limit: offered for archiving.</summary>
    public bool IsStale { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    public string AutomationName =>
        $"{Name}, by {Author}, revision {Revision}, last used {LastUsed}, {SkillsText.Uses(Record.Uses)}, evals {EvalText}"
        + (IsPinned ? ", pinned" : "") + (IsOutside ? ", outside content" : "") + (IsStale ? ", stale" : "");

    /// <summary>Whether the filter's words all appear in the name, the description or the author.</summary>
    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        var haystack = $"{Record.Name} {Record.Description} {Record.Author}";
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A label and a value of the detail panel's key-value list.</summary>
public sealed record SkillFact(string Key, string Value, bool IsCode = false);

/// <summary>An eval run of the open skill: revision, when, passed of total, and each case's steps.</summary>
public sealed class EvalRunView
{
    internal EvalRunView(EvalRun run, TimeZoneInfo zone, bool isCurrent)
    {
        Run = run;
        Revision = SkillsText.Short(run.Revision);
        When = SkillsText.Stamp(run.At, zone);
        Score = SkillsText.Score(run.Score);
        Tone = SkillsText.ScoreTone(run.Score);
        IsCurrent = isCurrent;
        Cases = [.. run.Cases.Select(c => new EvalCaseView(c))];
    }

    public EvalRun Run { get; }

    public string Revision { get; }

    public string When { get; }

    public string Score { get; }

    public Tone Tone { get; }

    /// <summary>The run is of the skill's current revision.</summary>
    public bool IsCurrent { get; }

    public string Verdict => Run.Score.AllPassed ? "passed" : "failed";

    public string Label => IsCurrent ? $"{Revision} · current" : Revision;

    public IReadOnlyList<EvalCaseView> Cases { get; }

    public string AutomationName => $"revision {Revision}{(IsCurrent ? ", current" : "")}, {When}, {Score} passed";
}

/// <summary>One eval case: its id, passed or failed, and for a failure what was expected and what ran.</summary>
public sealed class EvalCaseView(EvalCaseResult result)
{
    public string Case { get; } = SafeText.Clean(result.Case);

    public bool Passed { get; } = result.Outcome.Passed;

    public string Verdict => Passed ? "passed" : "failed";

    public Tone Tone => Passed ? Tone.Green : Tone.Red;

    public string Detail { get; } = result.Outcome.Passed
        ? SkillsText.Tools(result.Outcome.Actual)
        : $"expected {SkillsText.Tools(result.Outcome.Expected)}; called {SkillsText.Tools(result.Outcome.Actual)}"
          + (result.Outcome.Detail.Length > 0 ? " · " + SafeText.Clean(result.Outcome.Detail) : "");
}

/// <summary>
/// The open skill beside the library: its SKILL.md rendered, what it is and who wrote it, its eval history and the
/// actions <c>/skills</c> offers on one skill (pin or unpin, run evals, its history, archive when stale).
/// </summary>
public sealed class SkillDetailView : ObservableObject
{
    private readonly SkillsViewModel _owner;
    private bool _isConfirmingArchive;

    internal SkillDetailView(SkillsViewModel owner, SkillRowView row, TimeProvider time, int staleDays)
    {
        _owner = owner;
        Row = row;
        var record = row.Record;
        Document = MarkdownParser.Parse(SkillsText.Body(record.Content));
        var used = record.LastUsed is null ? "never"
            : SkillsText.When(record.LastUsed, time) + (record.LastSession is { } session ? " in " + SafeText.Clean(session) : "");
        var facts = new List<SkillFact>
        {
            new("author", $"{row.Author} · {row.AuthorMeaning}"),
            new("revision", row.Revision.Length == 0 ? "uncommitted" : row.Revision, IsCode: true),
            new("folder", SafeText.Reveal(record.Path.Length == 0 ? record.Name : record.Path) + "/", IsCode: true),
            new("last used", $"{used} · {SkillsText.Uses(record.Uses)}"),
        };
        if (record.RequiresTools.Count > 0)
            facts.Add(new("requires", SkillsText.Tools(record.RequiresTools), IsCode: true));
        facts.Add(new("evals", record.EvalCases == 0 ? "no evals/ cases" : $"{record.EvalCases} case{(record.EvalCases == 1 ? "" : "s")}"));
        Facts = facts;
        StaleNote = row.IsStale
            ? $"Idle {row.IdleDays} days, agent-authored and not pinned: the curator offers to archive it (idle limit {staleDays} days)."
            : "";

        TogglePinCommand = new AsyncRelayCommand(ct => _owner.SetPinnedAsync(this, !row.IsPinned, ct));
        RunEvalsCommand = new AsyncRelayCommand(ct => _owner.RunEvalsAsync(this, ct), () => record.EvalCases > 0);
        ShowHistoryCommand = new AsyncRelayCommand(ct => _owner.ShowHistoryAsync(record.Name, ct));
        StartArchiveCommand = new RelayCommand(() => IsConfirmingArchive = true, () => CanArchive);
        ConfirmArchiveCommand = new AsyncRelayCommand(ct => _owner.ArchiveAsync([record.Name], ct), () => CanArchive);
        CancelArchiveCommand = new RelayCommand(() => IsConfirmingArchive = false);
    }

    public SkillRowView Row { get; }

    public string Name => Row.Name;

    public string Description => Row.Description;

    public MarkdownDocument Document { get; }

    public IReadOnlyList<SkillFact> Facts { get; }

    public ObservableCollection<EvalRunView> EvalRuns { get; } = [];

    public bool HasEvalRuns => EvalRuns.Count > 0;

    public bool HasNoEvalRuns => EvalRuns.Count == 0;

    public string EvalsEmptyText => Row.HasEvals ? "No eval run yet. Run evals replays each case with the skill loaded." : "This skill has no evals/ cases.";

    /// <summary>The latest run's cases, with each failure's expected and actual steps.</summary>
    public IReadOnlyList<EvalCaseView> LatestCases => EvalRuns.Count > 0 ? EvalRuns[0].Cases : [];

    public string LatestTitle => EvalRuns.Count > 0 ? $"Cases at {EvalRuns[0].Revision}" : "";

    public string PinLabel => Row.IsPinned ? "Unpin" : "Pin";

    public string PinHint => Row.IsPinned ? "Pinned: the curator never touches it." : "Pin to keep the curator from changing or archiving it.";

    public bool CanArchive => Row.IsStale;

    public string StaleNote { get; }

    public bool IsStale => Row.IsStale;

    public bool IsConfirmingArchive
    {
        get => _isConfirmingArchive;
        set => SetProperty(ref _isConfirmingArchive, value);
    }

    public AsyncRelayCommand TogglePinCommand { get; }

    public AsyncRelayCommand RunEvalsCommand { get; }

    public AsyncRelayCommand ShowHistoryCommand { get; }

    public RelayCommand StartArchiveCommand { get; }

    public AsyncRelayCommand ConfirmArchiveCommand { get; }

    public RelayCommand CancelArchiveCommand { get; }

    internal void SetRuns(IEnumerable<EvalRunView> runs)
    {
        EvalRuns.Clear();
        foreach (var run in runs)
            EvalRuns.Add(run);
        OnPropertyChanged(nameof(HasEvalRuns));
        OnPropertyChanged(nameof(HasNoEvalRuns));
        OnPropertyChanged(nameof(LatestCases));
        OnPropertyChanged(nameof(LatestTitle));
    }
}

/// <summary>A row of the history table: revision, author, when and the change's subject.</summary>
public sealed class CommitRowView : ObservableObject
{
    private bool _isSelected;

    internal CommitRowView(SkillCommit commit, TimeZoneInfo zone, Action<CommitRowView> select)
    {
        Commit = commit;
        Revision = SkillsText.Short(commit.Revision);
        Author = SafeText.Clean(commit.Author);
        AuthorTone = SkillsText.AuthorTone(commit.Author);
        When = SkillsText.Stamp(commit.Date, zone);
        Subject = SafeText.Clean(commit.Subject);
        SelectCommand = new RelayCommand(() => select(this));
    }

    public SkillCommit Commit { get; }

    public string Revision { get; }

    public string Author { get; }

    public Tone AuthorTone { get; }

    public string When { get; }

    public string Subject { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    public string AutomationName => $"{Revision}, {Author}, {When}: {Subject}";
}

/// <summary>A file a commit changed, as unified diff rows (lines revealed: the diff is what a revert puts back).</summary>
public sealed class CommitFileView
{
    internal CommitFileView(SkillCommitFile file)
    {
        Path = SafeText.Reveal(file.Path);
        var diff = FileDiff.Compute(file.Path, file.OldText, file.NewText ?? "");
        Rows = diff.Unified();
        Counts = file.NewText is null ? $"deleted · -{diff.Removed}" : file.OldText is null ? $"new · +{diff.Added}" : diff.CountsText;
    }

    public string Path { get; }

    public string Counts { get; }

    public IReadOnlyList<DiffRow> Rows { get; }
}

/// <summary>The open commit: its files' diffs and Revert, which asks once before writing a commit that undoes it.</summary>
public sealed class CommitDetailView : ObservableObject
{
    private bool _isConfirmingRevert;

    internal CommitDetailView(SkillsViewModel owner, SkillCommitDetail detail, TimeZoneInfo zone)
    {
        Detail = detail;
        Revision = SkillsText.Short(detail.Commit.Revision);
        Subject = SafeText.Clean(detail.Commit.Subject);
        Byline = $"{SafeText.Clean(detail.Commit.Author)} · {SkillsText.Stamp(detail.Commit.Date, zone)}";
        Files = [.. detail.Files.Select(f => new CommitFileView(f))];
        RevertNote = $"Revert {Revision}? A new commit, authored user, undoes it; the files it touched become yours, so the agent no longer changes them.";
        StartRevertCommand = new RelayCommand(() => IsConfirmingRevert = true);
        ConfirmRevertCommand = new AsyncRelayCommand(ct => owner.RevertAsync(this, ct));
        CancelRevertCommand = new RelayCommand(() => IsConfirmingRevert = false);
    }

    public SkillCommitDetail Detail { get; }

    public string Revision { get; }

    public string Subject { get; }

    public string Byline { get; }

    public IReadOnlyList<CommitFileView> Files { get; }

    public string RevertNote { get; }

    public bool IsConfirmingRevert
    {
        get => _isConfirmingRevert;
        set
        {
            if (SetProperty(ref _isConfirmingRevert, value))
                OnPropertyChanged(nameof(IsReading));
        }
    }

    public bool IsReading => !_isConfirmingRevert;

    public RelayCommand StartRevertCommand { get; }

    public AsyncRelayCommand ConfirmRevertCommand { get; }

    public RelayCommand CancelRevertCommand { get; }
}

/// <summary>A skill an import found: its name (revealed: it is what gets imported), its folder, and the scanner's verdict.</summary>
public sealed class ImportCandidateView(ImportCandidate candidate)
{
    public ImportCandidate Candidate { get; } = candidate;

    public string Name { get; } = SafeText.Reveal(candidate.Name);

    public string Path { get; } = SafeText.Reveal(candidate.Path) + "/";

    public string Description { get; } = SafeText.Clean(candidate.Description);

    /// <summary>A name holding a hidden character is refused whatever the scanner said.</summary>
    public bool IsRefused { get; } = candidate.IsRefused || SafeText.HasHidden(candidate.Name);

    public string Verdict => IsRefused ? "refused" : "passes";

    public Tone VerdictTone => IsRefused ? Tone.Red : Tone.Green;

    /// <summary>"credential_read · SKILL.md:14 · cat ~/.ssh/id_ed25519", each excerpt revealed.</summary>
    public IReadOnlyList<string> Findings { get; } =
    [
        .. candidate.Findings.Select(f => $"{SafeText.Clean(f.Rule)} · {SafeText.Reveal(f.File)}:{f.Line} · {SafeText.Reveal(f.Excerpt)}"),
        .. SafeText.HasHidden(candidate.Name) && !candidate.IsRefused ? ["the name holds hidden characters"] : Array.Empty<string>(),
    ];

    public bool HasFindings => Findings.Count > 0;

    public string AutomationName => $"{Name}, {Verdict}" + (HasFindings ? ": " + string.Join("; ", Findings) : "");
}

/// <summary>
/// A stale skill in the Review tab's side panel, with its own Archive…, which asks first as the open skill's does: archiving
/// takes the skill out of the library.
/// </summary>
public sealed class StaleSkillView : ObservableObject
{
    private readonly Action<StaleSkillView> _confirming;
    private bool _isConfirming;

    internal StaleSkillView(SkillRowView row, Func<CancellationToken, Task> archive, Action<StaleSkillView> confirming, RelayCommand open)
    {
        Row = row;
        _confirming = confirming;
        StartArchiveCommand = new RelayCommand(() => IsConfirming = true);
        ArchiveCommand = new AsyncRelayCommand(archive);
        CancelArchiveCommand = new RelayCommand(() => IsConfirming = false);
        OpenCommand = open;
    }

    public SkillRowView Row { get; }

    public string Name => Row.Name;

    public string Detail => $"idle {Row.IdleDays} days · {SkillsText.Uses(Row.Record.Uses)} · {Row.Author}";

    /// <summary>Archive… was pressed: the row asks before it archives.</summary>
    public bool IsConfirming
    {
        get => _isConfirming;
        set
        {
            if (!SetProperty(ref _isConfirming, value))
                return;
            OnPropertyChanged(nameof(IsIdle));
            if (value)
                _confirming(this);
        }
    }

    public bool IsIdle => !_isConfirming;

    /// <summary>"Archive python-venv-setup? It moves to .archive/; git keeps it."</summary>
    public string ConfirmText => $"Archive {Name}? It moves to .archive/; git keeps it.";

    /// <summary>Archive…: asks.</summary>
    public RelayCommand StartArchiveCommand { get; }

    /// <summary>The question's Archive.</summary>
    public AsyncRelayCommand ArchiveCommand { get; }

    /// <summary>The question's Back.</summary>
    public RelayCommand CancelArchiveCommand { get; }

    public RelayCommand OpenCommand { get; }

    public string AutomationName => $"{Name}, {Detail}";
}
