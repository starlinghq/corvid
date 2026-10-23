using System.Collections.Immutable;
using System.Globalization;
using Corvid.Kernel.Jobs;
using Corvid.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/cron</c> (C-035): the durable scheduler's jobs (C-012). <c>list</c> shows them with their next run in the job's own
/// zone and how the last run went; <c>runs</c> lists recent runs from the store (C-041); <c>pause</c>, <c>resume</c> and
/// <c>remove</c> change one. Every subcommand is the user acting directly, so there is no approval step (the model's
/// <c>cron_pause</c>, <c>cron_resume</c> and <c>cron_remove</c> ask). The scheduler in this process wakes on a change
/// (<see cref="JobStore.Changed"/>); one in another process picks it up within its poll interval.
/// </summary>
public static class CronCommands
{
    public const string Usage = "[list] [--all] | runs [<id>] | pause <id> | resume <id> | remove <id>";

    /// <summary>How many runs <c>/cron runs</c> shows.</summary>
    public const int RunsShown = 20;

    private const int DetailChars = 60;

    internal static ValueTask<CommandOutput> RunAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Run(context.Runtime.Builtin.Jobs, invocation.Arguments));

    internal static CommandOutput Run(JobStore jobs, ImmutableArray<string> arguments)
    {
        try
        {
            return arguments switch
            {
                [] or ["list"] => List(jobs, all: false),
                ["--all"] or ["list", "--all"] => List(jobs, all: true),
                ["runs"] => Runs(jobs, null),
                ["runs", var id] => Runs(jobs, id),
                ["pause", var id] => jobs.Pause(id)
                    ? CommandOutput.Success($"Paused {id}: it will not run until /cron resume {id}.")
                    : CommandOutput.Error($"There is no active, unpaused job '{id}'; /cron lists them."),
                ["resume", var id] => jobs.Resume(id) is { } resumed
                    ? CommandOutput.Success($"Resumed {id}: " + (resumed.NextRunAt is { } next
                        ? $"next run {CronTools.Local(next, resumed)}." : "its schedule has no run left."))
                    : CommandOutput.Error($"There is no paused job '{id}'; /cron lists them."),
                ["remove", var id] => jobs.Remove(id)
                    ? CommandOutput.Success($"Removed {id}: it will not run again. Its runs stay recorded.")
                    : CommandOutput.Error($"There is no active job '{id}'; /cron lists them."),
                _ => CommandOutput.Error("Usage: /cron " + Usage),
            };
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException)
        {
            return CommandOutput.Error(exception.Message);
        }
    }

    private static CommandOutput List(JobStore jobs, bool all)
    {
        var listed = jobs.List(includeFinished: all);
        if (listed.Count == 0)
            return CommandOutput.Text(all ? "No jobs have been scheduled." : "No active jobs. /cron --all shows finished and removed ones too.");
        return new CommandOutput
        {
            Title = "Scheduled jobs",
            Table = CommandTable.Of(["id", "name", "schedule", "state", "next run", "last run", "flags"], listed.Select(job => new[]
            {
                job.JobId,
                job.Name,
                job.Schedule,
                job.State == JobState.Active && job.Paused ? "paused" : job.State.ToString().ToLowerInvariant(),
                job.NextRunAt is { } next ? CronTools.Local(next, job) : "-",
                jobs.Runs(job.JobId, 1) is [var last] ? $"{CronTools.Local(last.StartedAt, job)} {last.Status}" : "-",
                Flags(job),
            })),
            Lines = ["/cron runs [<id>] for run history; /cron pause <id>, /cron resume <id> or /cron remove <id>."],
        };
    }

    // C-041: the newest runs, across all jobs or for one, each with its session so `corvid chat --session` can open it.
    private static CommandOutput Runs(JobStore jobs, string? id)
    {
        ScheduledJob? only = null;
        if (id is not null && (only = jobs.Get(id)) is null)
            return CommandOutput.Error($"There is no job '{id}'; /cron --all lists them.");
        var runs = only is null ? jobs.RecentRuns(RunsShown) : jobs.Runs(only.JobId, RunsShown);
        if (runs.Count == 0)
            return CommandOutput.Text(only is null ? "No job has run yet." : $"{only.JobId} ({only.Name}) has not run yet.");

        var known = new Dictionary<string, ScheduledJob?>(StringComparer.Ordinal);
        if (only is not null)
            known[only.JobId] = only;
        ScheduledJob? Job(string jobId)
        {
            if (!known.TryGetValue(jobId, out var job))
                known[jobId] = job = jobs.Get(jobId);
            return job;
        }

        string[] columns = only is null
            ? ["run", "job", "scheduled for", "started", "took", "status", "detail", "session"]
            : ["run", "scheduled for", "started", "took", "status", "detail", "session"];
        return new CommandOutput
        {
            Title = only is null ? "Recent job runs" : $"Runs of {only.JobId} ({only.Name})",
            Table = CommandTable.Of(columns, runs.Select(run =>
            {
                var job = Job(run.JobId);
                string Time(DateTimeOffset at) =>
                    job is null ? at.ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture) : CronTools.Local(at, job);
                var row = new List<string> { run.RunId };
                if (only is null)
                    row.Add(job?.Name ?? run.JobId);
                row.AddRange([Time(run.ScheduledFor), Time(run.StartedAt), Took(run), run.Status, Detail(run), run.SessionId]);
                return row.ToArray();
            })),
            Lines = [$"Newest first, at most {RunsShown}. corvid chat --session <session> opens a run's conversation."],
        };
    }

    private static string Took(JobRun run)
    {
        if (run.FinishedAt is not { } finished)
            return "-";
        var took = finished - run.StartedAt;
        return took < TimeSpan.FromSeconds(1) ? "<1s"
            : took < TimeSpan.FromMinutes(1) ? string.Create(CultureInfo.InvariantCulture, $"{(int)took.TotalSeconds}s")
            : took < TimeSpan.FromHours(1) ? string.Create(CultureInfo.InvariantCulture, $"{(int)took.TotalMinutes}m{took.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)took.TotalHours}h{took.Minutes:00}m");
    }

    // One line: the run's detail, else the start of its output, cut to about sixty characters.
    internal static string Detail(JobRun run)
    {
        var text = run.Detail ?? run.Output;
        if (string.IsNullOrWhiteSpace(text))
            return "-";
        var line = string.Join(' ', text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length <= DetailChars)
            return line;
        var cut = DetailChars - 1;
        // Never split a surrogate pair.
        if (char.IsHighSurrogate(line[cut - 1]))
            cut--;
        return line[..cut].TrimEnd() + "…";
    }

    private static readonly char[] LineBreaks = ['\r', '\n', '\t'];

    private static string Flags(ScheduledJob job)
    {
        var flags = new List<string>();
        if (job.NoAgent)
            flags.Add("no_agent");
        if (job.Script is not null)
            flags.Add("script");
        if (job.CreatedTainted)
            flags.Add("tainted");
        return flags.Count == 0 ? "-" : string.Join(", ", flags);
    }
}
