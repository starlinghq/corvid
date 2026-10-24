using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>A changed file in the dock's Changes (<c>.files li</c>): its path, "+12" and "−3"; or "not readable by Corvid".</summary>
public sealed record ChangedFileView(string Path, string AddedText, string RemovedText, bool IsReadable)
{
    public string Note => IsReadable ? "" : "not readable by Corvid";

    public string AutomationName => IsReadable ? $"{Path}, {AddedText} {RemovedText}" : $"{Path}, {Note}";

    internal static ChangedFileView From(FileChange change) => new(
        SafeText.Clean(change.Path),
        change.Readable ? string.Create(CultureInfo.InvariantCulture, $"+{change.Added}") : "",
        change.Readable ? string.Create(CultureInfo.InvariantCulture, $"−{change.Removed}") : "",
        change.Readable);
}

/// <summary>A task in the dock's Tasks, as the model keeps it.</summary>
public sealed record TaskView(string Text, PlanEntryStatus Status)
{
    public bool IsDone => Status == PlanEntryStatus.Completed;

    public bool IsActive => Status == PlanEntryStatus.InProgress;

    /// <summary>"done", "in progress" or "to do", so the state is in words as well as a mark.</summary>
    public string StatusText => Status switch
    {
        PlanEntryStatus.Completed => "done",
        PlanEntryStatus.InProgress => "in progress",
        _ => "to do",
    };
}

/// <summary>
/// A decision in the Decisions panel (Approval.png): "risk → <b>3, ask</b>" on the left and "0.86 · acted" on the right.
/// </summary>
/// <param name="Answer">What it answered: "task", "3, ask", "shell" (monospace when <see cref="AnswerIsCode"/>).</param>
public sealed record DecisionRowView(string Question, string Answer, bool AnswerIsCode, string Value, bool IsAsk, bool IsUnsure)
{
    public string Label => $"{Question} → {Answer}";

    public string AutomationName => $"{Label}, {Value}";
}

/// <summary>A checkpoint's group in the Decisions panel: "pre-execute · 14:04:12" and "Kev-0.8B · 240 ms".</summary>
public sealed record DecisionGroupView(string Header, string Detail, IReadOnlyList<DecisionRowView> Rows);

/// <summary>A line of the dock's Session panel: "workspace" and "corvid · main".</summary>
public sealed record SessionFact(string Label, string Value);

public sealed partial class SessionViewModel
{
    /// <summary>The Decisions panel's footnote, as the mockup words it.</summary>
    public const string DecisionsFootnote =
        "Confidence is the calibrated probability of the acted answer. An unsure answer below its floor never acts on its own.";

    private IReadOnlyList<ChangedFileView> _turnFiles = [];
    private IReadOnlyList<ChangedFileView> _sessionFiles = [];
    private IReadOnlyList<TaskView> _tasks = [];
    private IReadOnlyList<DecisionGroupView> _decisionGroups = [];
    private IReadOnlyList<SessionFact> _facts = [];
    private ToolCardBlock? _outputCard;

    // ---- Changes ----

    /// <summary>"This turn": files the running or last turn changed.</summary>
    public IReadOnlyList<ChangedFileView> TurnFiles { get => _turnFiles; private set => SetProperty(ref _turnFiles, value); }

    /// <summary>"2 files".</summary>
    public string TurnFilesSummary => SessionText.Plural(_turnFiles.Count, "file");

    /// <summary>"Session": every file the session changed, unreadable ones last.</summary>
    public IReadOnlyList<ChangedFileView> SessionFiles { get => _sessionFiles; private set => SetProperty(ref _sessionFiles, value); }

    /// <summary>"since turn 1 · 3 files", counting the files Corvid can read.</summary>
    public string SessionFilesSummary =>
        $"since turn {_state.ChangesSinceTurn.ToString(CultureInfo.InvariantCulture)} · {SessionText.Plural(ChangesCount, "file")}";

    /// <summary>The Changes tab's badge: files changed in the session that Corvid can read.</summary>
    public int ChangesCount => _sessionFiles.Count(file => file.IsReadable);

    /// <summary>The dock's "Open Review".</summary>
    public RelayCommand OpenReviewCommand { get; private set; } = null!;

    // ---- Context ----

    /// <summary>"38% of 200k"; empty until usage is reported (K-5).</summary>
    public string ContextSummary => _state.ContextFraction is { } fraction && _state.ContextWindow is { } window
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(fraction * 100)}% of {SessionText.Tokens(window)}")
        : "";

    /// <summary>The gauge, 0 to 1.</summary>
    public double ContextFraction => _state.ContextFraction ?? 0;

    /// <summary>The gauge in percent, for a bar's width.</summary>
    public double ContextPercent => Math.Round(ContextFraction * 100, 1);

    /// <summary>"system 2.1k · memory 1.4k · skills 0.9k · results 61k · stubs 3k · next compaction at 70%".</summary>
    public string ContextBreakdown
    {
        get
        {
            var parts = _state.ContextBreakdown.Select(part => $"{part.Label} {SessionText.Tokens(part.Tokens)}").ToList();
            if (_state.CompactAt is { } at)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"next compaction at {Math.Round(at * 100)}%"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Within five points of the compaction threshold: the gauge turns amber.</summary>
    public bool IsContextHigh => _state.ContextFraction is { } fraction && _state.CompactAt is { } at && fraction >= at - 0.05;

    // ---- Tasks ----

    public IReadOnlyList<TaskView> Tasks { get => _tasks; private set => SetProperty(ref _tasks, value); }

    /// <summary>The Tasks tab's badge: tasks not done.</summary>
    public int OpenTasks => _tasks.Count(task => !task.IsDone);

    // ---- Decisions ----

    /// <summary>Every checkpoint's decisions, newest first.</summary>
    public IReadOnlyList<DecisionGroupView> DecisionGroups { get => _decisionGroups; private set => SetProperty(ref _decisionGroups, value); }

    public string DecisionsNote => DecisionsFootnote;

    /// <summary>"Open in Audit".</summary>
    public RelayCommand OpenAuditCommand { get; private set; } = null!;

    // ---- Output ----

    /// <summary>The tool call whose output the Output panel shows: the one chosen, else the latest with output.</summary>
    public string OutputTitle => OutputCard is { } card ? $"{card.ToolName} {card.Target}".Trim() : "";

    public string OutputText => OutputCard?.Output ?? "";

    public bool HasOutput => OutputText.Length > 0;

    private ToolCardBlock? OutputCard => _outputCard is not null && _blocks.ContainsKey(_outputCard.Id)
        ? _outputCard
        : Blocks.OfType<ToolCardBlock>().LastOrDefault(card => card.Output.Length > 0);

    // ---- Session ----

    /// <summary>The Session panel: id, workspace, project, fork, goal, mode, turns, models.</summary>
    public IReadOnlyList<SessionFact> Facts { get => _facts; private set => SetProperty(ref _facts, value); }

    /// <summary>Shows a card's full output in the Output panel.</summary>
    internal void ShowOutput(ToolCardBlock card)
    {
        _outputCard = card;
        RaiseOutput();
    }

    private void InitializePanels()
    {
        OpenReviewCommand = new RelayCommand(() => Navigate(Area.Review));
        OpenAuditCommand = new RelayCommand(() => Navigate(Area.Audit));
    }

    private void UpdatePanels(SessionView previous)
    {
        if (!previous.TurnChanges.Equals(_state.TurnChanges) || !previous.SessionChanges.Equals(_state.SessionChanges)
            || previous.ChangesSinceTurn != _state.ChangesSinceTurn)
        {
            TurnFiles = [.. _state.TurnChanges.Select(ChangedFileView.From)];
            SessionFiles = [.. _state.SessionChanges.OrderBy(change => !change.Readable).Select(ChangedFileView.From)];
            OnPropertyChanged(nameof(TurnFilesSummary));
            OnPropertyChanged(nameof(SessionFilesSummary));
            OnPropertyChanged(nameof(ChangesCount));
        }

        if (previous.ContextTokens != _state.ContextTokens || previous.ContextWindow != _state.ContextWindow
            || previous.CompactAt != _state.CompactAt || !previous.ContextBreakdown.Equals(_state.ContextBreakdown))
        {
            OnPropertyChanged(nameof(ContextSummary));
            OnPropertyChanged(nameof(ContextFraction));
            OnPropertyChanged(nameof(ContextPercent));
            OnPropertyChanged(nameof(ContextBreakdown));
            OnPropertyChanged(nameof(IsContextHigh));
        }

        if (!previous.Plan.Equals(_state.Plan))
        {
            Tasks = [.. _state.Plan.Select(entry => new TaskView(SafeText.Clean(entry.Text), entry.Status))];
            OnPropertyChanged(nameof(OpenTasks));
        }

        if (!ReferenceEquals(previous.Decisions, _state.Decisions))
            DecisionGroups = [.. _state.Decisions.Reverse().Select(Group)];

        Facts = BuildFacts();
        RaiseOutput();
    }

    private void RaiseOutput()
    {
        OnPropertyChanged(nameof(OutputTitle));
        OnPropertyChanged(nameof(OutputText));
        OnPropertyChanged(nameof(HasOutput));
    }

    private DecisionGroupView Group(DecisionGroup group)
    {
        var header = $"{SessionText.Checkpoint(group.Checkpoint)} · {SessionText.ClockSeconds(group.At, _time.LocalTimeZone)}";
        var detail = group.Latency is { } latency
            ? string.Create(CultureInfo.InvariantCulture, $"{group.Decider} · {Math.Round(latency.TotalMilliseconds)} ms")
            : group.Decider;
        return new DecisionGroupView(header, detail, [.. group.Answers.Select(answer => new DecisionRowView(
            answer.Question,
            answer.Option is { Length: > 0 } option ? $"{option}, {answer.Action}" : answer.Action,
            answer.Question == "tool",
            $"{SessionText.Confidence(answer.Confidence)} · {(answer.Acted ? "acted" : "unsure")}",
            answer.Action == "ask",
            !answer.Acted))]);
    }

    private IReadOnlyList<SessionFact> BuildFacts()
    {
        var facts = new List<SessionFact> { new("session", SessionId) };
        if (_state.Workspace is { } workspace)
            facts.Add(new("workspace", _state.Branch is { } branch ? $"{workspace} · {branch}" : workspace));
        if (_state.Project is { } project)
            facts.Add(new("project", project));
        if (HasFork)
            facts.Add(new("fork", ForkText));
        if (_state.Goal is { } goal)
            facts.Add(new("goal", goal));
        if (_state.Mode is { } mode)
            facts.Add(new("approval mode", SessionText.Mode(mode)));
        facts.Add(new("turns", _state.Turn.ToString(CultureInfo.InvariantCulture)));
        if (StatusGenerator.Length > 0)
            facts.Add(new("generator", StatusGenerator));
        if (_state.Decider is { } decider)
            facts.Add(new("decider", decider));
        if (_state.SessionCost is { } cost)
            facts.Add(new("cost", SessionText.Cost(cost)));
        return _facts.SequenceEqual(facts) ? _facts : facts;
    }
}
