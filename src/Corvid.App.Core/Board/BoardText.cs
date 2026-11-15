using System.Globalization;
using System.Text.RegularExpressions;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Board;

/// <summary>
/// The Board's words for a session (Board.dc.html <c>.scard</c>), from its summary and, when watched, its folded state.
/// Pure functions of their inputs and the clock, so the copy is tested without a view. What a session reports (its title,
/// workspace, branch, question, the running call, the model) is data and drawn cleaned (<see cref="SafeText.Clean"/>).
/// </summary>
public static partial class BoardText
{
    /// <summary>A card's title: the session's, or for a job run the job and its start ("nightly-tidy · run of 09:00").</summary>
    public static string Title(SessionSummary summary, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(zone);
        if (summary.Job is { } job && summary.RunAt is { } run)
            return $"{SafeText.Clean(job)} · run of {RelativeTime.ClockTime(run, zone)}";
        return SafeText.Clean(summary.Title);
    }

    /// <summary>
    /// The line under the title: workspace, branch and id, with the fork's parent and what drives a read-only session
    /// ("open in a terminal (<c>corvid chat --session cli-c3d2</c>)"); for a job run its id and schedule.
    /// </summary>
    public static IReadOnlyList<MarkdownRun> Meta(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Job is not null)
            return [new MarkdownRun(SafeText.Clean(Join(summary.Id, summary.Schedule)))];
        var runs = new List<MarkdownRun> { new(SafeText.Clean(Join(summary.Workspace, summary.Branch, summary.Id, summary.ForkOf is { } fork ? $"fork of {fork}" : null))) };
        if (summary.ReadOnly && Holder(summary.Holder) is { Length: > 0 } holder)
        {
            runs.Add(new MarkdownRun(" · "));
            runs.AddRange(CommandRuns(holder));
        }

        return Merge(runs);
    }

    /// <summary>
    /// A session's title as the narrow tray names it: without a closing qualifier after its first three words ("Fix the
    /// flaky sandbox test on CI" is "Fix the flaky sandbox test", "Draft release notes for v0.2" is "Draft release notes").
    /// </summary>
    public static string ShortTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        title = SafeText.Clean(title);
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = words.Length - 2; i >= 3; i--)
        {
            if (words[i] is "on" or "for" or "in" or "of" or "from" or "with")
                return string.Join(' ', words[..i]);
        }

        return title;
    }

    /// <summary>The state dot's colour: amber waiting, the accent running, violet a job run, grey idle.</summary>
    public static Tone StateTone(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (YourTurn(summary))
            return Tone.Amber;
        if (summary.WorkingElsewhere)
            return Tone.Blue;
        return summary.State switch
        {
            SessionState.Waiting => Tone.Amber,
            SessionState.Running => Tone.Blue,
            SessionState.Job => Tone.Violet,
            _ => Tone.Grey,
        };
    }

    /// <summary>The state in words, drawn bold: "waiting on you", "running", "idle", "held by another process", "finished".</summary>
    public static string StateLabel(SessionSummary summary, SessionView? view)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.State == SessionState.Job)
            return view is { Running: true } || summary.JobRunning ? "running" : summary.JobFailed ? "failed" : "finished";
        if (summary.WorkingElsewhere)
            return "running elsewhere";
        if (summary.ReadOnly)
            return "held by another process";
        if (YourTurn(summary))
            return "your turn";
        if (summary.State == SessionState.Idle && summary.Mark == BoardMark.Done)
            return "done";
        return summary.State switch
        {
            SessionState.Waiting => "waiting on you",
            SessionState.Running => "running",
            _ => "idle",
        };
    }

    /// <summary>
    /// What follows the state, after a dot: the approval's place and time left, the running call (its target in
    /// monospace), the last activity, a job run's end and tool calls.
    /// </summary>
    public static IReadOnlyList<MarkdownRun> StateDetail(SessionSummary summary, SessionView? view, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(zone);
        if (summary.State == SessionState.Job)
        {
            var end = TimeZoneInfo.ConvertTime(summary.LastActivity, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return [new MarkdownRun(view is null ? end : Join(end, Plural(ToolCalls(view), "tool call")))];
        }

        if (summary.ReadOnly)
            return [new MarkdownRun("read-only live view")];
        switch (summary.State)
        {
            case SessionState.Waiting when summary.PendingApprovals > 0:
                var place = $"approval 1 of {summary.PendingApprovals}";
                return [new MarkdownRun(summary.Deadline is { } deadline ? Join(place, $"{SessionText.Countdown(deadline - now)} left") : place)];
            case SessionState.Waiting:
                return [new MarkdownRun(Join("question", SafeText.Clean(summary.Question)))];
            case SessionState.Running when SafeText.Clean(summary.Activity) is { Length: > 0 } activity:
                var space = activity.IndexOf(' ', StringComparison.Ordinal);
                return space < 0
                    ? [new MarkdownRun(activity)]
                    : [new MarkdownRun(activity[..(space + 1)]), new MarkdownRun(activity[(space + 1)..], InlineStyle.Code)];
            case SessionState.Running:
                return [new MarkdownRun("working")];
            default:
                return [new MarkdownRun($"last activity {RelativeTime.Since(Later(now, summary.LastActivity), summary.LastActivity)} ago")];
        }
    }

    /// <summary>
    /// The muted line under the state: the step, model and time on the step (turns while it waits) for a live session; the
    /// turns and anything staged for an idle one; what a job run left behind.
    /// </summary>
    public static string Detail(SessionSummary summary, SessionView? view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return SafeText.Clean(DetailText(summary, view, now));
    }

    private static string DetailText(SessionSummary summary, SessionView? view, DateTimeOffset now)
    {
        var notices = view is null ? [] : LatestTurnNotices(view);
        if (summary.State == SessionState.Job)
        {
            return Join(
                notices.FirstOrDefault(notice => notice.Kind == NoticeKind.MemoryStaged) is { } staged ? StagedForReview(staged.Text) : null,
                notices.FirstOrDefault(notice => notice.Kind == NoticeKind.JobFinished) is { } finished ? Delivery(finished.Text) : null);
        }

        if (summary.State is SessionState.Running or SessionState.Waiting)
        {
            var step = summary.Step is { } number ? summary.MaxSteps is { } max ? $"step {number} of {max}" : $"step {number}" : null;
            var time = summary.State == SessionState.Waiting
                ? Plural(summary.Turns, "turn")
                : summary.StepStarted is { } started ? RelativeTime.Since(Later(now, started), started) : null;
            var question = summary.State == SessionState.Running && summary.PendingQuestions > 0 ? "a question is queued for you" : null;
            return Join(step, summary.Model, time, question);
        }

        return Join(Plural(summary.Turns, "turn"),
            notices.FirstOrDefault(notice => notice.Kind == NoticeKind.MemoryStaged)?.Text);
    }

    /// <summary>The tags at a card's foot: taint, where the generator runs, read-only, a job.</summary>
    public static IReadOnlyList<StatusTag> Tags(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Job is not null)
            return summary.JobFailed
                ? [new StatusTag("job", Tone.Violet, "A scheduled job's run, kept as it ran"), new StatusTag("failed", Tone.Red, "The run failed or was interrupted")]
                : [new StatusTag("job", Tone.Violet, "A scheduled job's run, kept as it ran")];
        var tags = new List<StatusTag>();
        if (summary.Tainted)
            tags.Add(new StatusTag("tainted", Tone.Amber, "Outside content is in context: network reads ask, egress offers only \"once\""));
        // C-043: kept in every column, so a card another process drives that fell back to Parked after a long call is not misread.
        if (summary.ReadOnly)
            tags.Add(new StatusTag("read-only", Tone.Grey, "Another process drives this session; this window can only watch"));
        else if (summary.Cloud)
            tags.Add(new StatusTag("cloud", Tone.Violet, "The generator is a cloud model: requests leave the machine"));
        else
            tags.Add(new StatusTag("local", Tone.Blue, "The generator runs on this machine"));
        return tags;
    }

    /// <summary>The header's counts: "5 sessions · 2 workspaces · 1 job run".</summary>
    public static string Counts(IReadOnlyCollection<SessionSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var workspaces = Workspaces(summaries);
        var jobs = summaries.Count(summary => summary.Job is not null);
        // A5: children are counted apart, as they show under their parents.
        var children = summaries.Count(summary => summary.Child is not null);
        return Join(Plural(summaries.Count - children, "session"), Plural(workspaces, "workspace"), jobs > 0 ? Plural(jobs, "job run") : null,
            children > 0 ? Plural(children, "child session") : null);
    }

    /// <summary>How many workspaces the sessions span, for the status bar's engine ("in-process · 2 workspaces").</summary>
    public static int Workspaces(IEnumerable<SessionSummary> summaries) =>
        summaries.Select(summary => summary.Workspace).Where(workspace => workspace.Length > 0).Distinct(StringComparer.Ordinal).Count();

    /// <summary>The status bar's right-hand item on the Board: "2 running · 1 waiting".</summary>
    public static string Activity(IEnumerable<SessionSummary> summaries)
    {
        var list = summaries.ToList();
        var running = list.Count(summary => summary.State == SessionState.Running);
        var waiting = list.Count(summary => summary.State == SessionState.Waiting);
        return Join(running > 0 ? $"{running} running" : null, waiting > 0 ? $"{waiting} waiting" : null) is { Length: > 0 } text ? text : "all idle";
    }

    /// <summary>
    /// The Board's order: what waits on the user first, then what runs, what is idle, what another process drives, and job
    /// runs last; newest activity first within each.
    /// </summary>
    public static int Rank(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Job is not null)
            return 4;
        if (summary.ReadOnly)
            return 3;
        return summary.State switch
        {
            SessionState.Waiting => 0,
            SessionState.Running => 1,
            _ => 2,
        };
    }

    /// <summary>C-043: the kanban column a session's card is in (<see cref="SessionBoard.Column"/>).</summary>
    public static BoardColumn Column(SessionSummary summary) => SessionBoard.Column(summary);

    /// <summary>C-043: "your turn" on the card and the rail (<see cref="SessionBoard.YourTurn"/>).</summary>
    public static bool YourTurn(SessionSummary summary) => SessionBoard.YourTurn(summary);

    /// <summary>
    /// C-043: the card's one line on where the session is at, the first that applies: the running call ("write_file
    /// docs/…", step x of y); the open approval or question; then the engine's <see cref="SessionSummary.StatusLine"/> (the
    /// generated line when current, the question the answer ends on, or its first line); then the latest exchange's line.
    /// Data, cleaned and on one line.
    /// </summary>
    public static string StatusLine(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string? line;
        if (summary.Job is null && summary.PendingApprovals == 0 && (summary.State == SessionState.Running || summary.WorkingElsewhere))
        {
            var step = summary.Step is { } number ? summary.MaxSteps is { } max ? $"step {number} of {max}" : $"step {number}" : null;
            line = Join(summary.Activity is { Length: > 0 } activity && activity != "waiting on you" ? activity
                : summary.WorkingElsewhere ? "working in another process" : "working", step);
        }
        else if (summary.PendingApprovals > 0)
            line = Join("approval", summary.Approval);
        else if (summary.PendingQuestions > 0)
            line = Join("question", summary.Question);
        else
            line = summary.StatusLine ?? summary.Preview;
        return OneLine(SafeText.Clean(line ?? ""));
    }

    /// <summary>C-043: a column's head with its count: "Needs you · 3".</summary>
    public static string ColumnHead(BoardColumn column, int count) => $"{ColumnName(column)} · {count}";

    /// <summary>C-043: a column's name as its head says it.</summary>
    public static string ColumnName(BoardColumn column) => column switch
    {
        BoardColumn.Running => "Running",
        BoardColumn.NeedsYou => "Needs you",
        BoardColumn.Done => "Done",
        _ => "Parked",
    };

    /// <summary>C-043: what an empty column says.</summary>
    public static string ColumnEmpty(BoardColumn column) => column switch
    {
        BoardColumn.Running => "Nothing is running.",
        BoardColumn.NeedsYou => "Nothing needs you.",
        BoardColumn.Done => "Nothing marked done. Mark done takes a card here until something new happens in it.",
        _ => "Nothing parked.",
    };

    /// <summary>
    /// C-043: the order within a column. Needs you: approvals by deadline, then questions, then your-turns, each newest
    /// first. Running: by when the step started (a job's run by its start). Parked: newest activity first. Done: newest
    /// mark first (a job's ended run by its end).
    /// </summary>
    public static IEnumerable<SessionSummary> Order(BoardColumn column, IEnumerable<SessionSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        return column switch
        {
            BoardColumn.NeedsYou => summaries
                .OrderBy(summary => summary.PendingApprovals > 0 ? 0 : summary.PendingQuestions > 0 ? 1 : 2)
                .ThenBy(summary => summary.PendingApprovals > 0 ? summary.Deadline ?? DateTimeOffset.MaxValue : DateTimeOffset.MaxValue)
                .ThenByDescending(summary => summary.LastActivity)
                .ThenBy(summary => summary.Id, StringComparer.Ordinal),
            BoardColumn.Running => summaries
                .OrderBy(summary => summary.StepStarted ?? summary.RunAt ?? summary.LastActivity)
                .ThenBy(summary => summary.Id, StringComparer.Ordinal),
            BoardColumn.Done => summaries
                .OrderByDescending(summary => summary.MarkedAt ?? summary.LastActivity)
                .ThenBy(summary => summary.Id, StringComparer.Ordinal),
            _ => summaries.OrderByDescending(summary => summary.LastActivity).ThenBy(summary => summary.Id, StringComparer.Ordinal),
        };
    }

    /// <summary>Tool calls in the latest turn.</summary>
    public static int ToolCalls(SessionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Transcript.Count(entry => entry is ToolEntry && entry.Turn == view.Turn);
    }

    /// <summary>"0", "1 turn", "13 turns".</summary>
    public static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>Joins the parts that have text with the mockups' " · ".</summary>
    public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>The notices since the latest message: what the latest turn left (staged memory, a job's delivery).</summary>
    private static List<Notice> LatestTurnNotices(SessionView view)
    {
        var start = view.Transcript.FindLastIndex(entry => entry is UserEntry);
        return [.. view.Transcript.Skip(Math.Max(0, start)).OfType<NoticeEntry>().Select(entry => entry.Notice)];
    }

    /// <summary>"memory staged: 2 facts" as a job run's outcome: "staged 2 memory facts for review".</summary>
    private static string StagedForReview(string text) =>
        StagedPattern().Match(text) is { Success: true } match
            ? $"staged {match.Groups[1].Value} memory {match.Groups[2].Value} for review"
            : text;

    /// <summary>"job nightly-tidy ran: 3 tool calls, the result delivered to …" as "delivered to …".</summary>
    private static string Delivery(string text)
    {
        var at = text.IndexOf("delivered", StringComparison.Ordinal);
        return at < 0 ? text : text[at..];
    }

    /// <summary>What drives a read-only session, without the advice after it ("…; take over once it lets go").</summary>
    private static string Holder(string? holder)
    {
        if (string.IsNullOrWhiteSpace(holder))
            return "";
        holder = SafeText.Clean(holder);
        var cut = holder.IndexOf(';', StringComparison.Ordinal);
        return (cut < 0 ? holder : holder[..cut]).Trim();
    }

    /// <summary>A command in parentheses is code: "open in a terminal (corvid chat --session cli-c3d2)".</summary>
    private static IEnumerable<MarkdownRun> CommandRuns(string text)
    {
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open + 1)
        {
            yield return new MarkdownRun(text);
            yield break;
        }

        yield return new MarkdownRun(text[..(open + 1)]);
        yield return new MarkdownRun(text[(open + 1)..close], InlineStyle.Code);
        yield return new MarkdownRun(text[close..]);
    }

    private static List<MarkdownRun> Merge(List<MarkdownRun> runs)
    {
        var merged = new List<MarkdownRun>();
        foreach (var run in runs.Where(run => run.Text.Length > 0))
        {
            if (merged.Count > 0 && merged[^1].Style == run.Style && merged[^1].Url is null && run.Url is null)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            else
                merged.Add(run);
        }

        return merged;
    }

    // The first line, for the card's one-line status; the view trims what does not fit.
    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 200 ? line[..199] + "…" : line;
    }

    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset then) => then > now ? then : now;

    [GeneratedRegex(@"memory staged: (\d+) (facts?)")]
    private static partial Regex StagedPattern();
}
