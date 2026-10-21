using System.Collections.Immutable;
using Corvid.Kernel.Jobs;
using Corvid.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/cron</c> (C-035): the durable scheduler's jobs (C-012). <c>list</c> shows them with their next run in the job's own
/// zone and how the last run went; <c>pause</c>, <c>resume</c> and <c>remove</c> change one. Every subcommand is the user
/// acting directly, so there is no approval step (the model's <c>cron_remove</c> asks). A scheduler in another process
/// picks a change up within its poll interval.
/// </summary>
public static class CronCommands
{
    public const string Usage = "[list] [--all] | pause <id> | resume <id> | remove <id>";

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
            Lines = ["/cron pause <id>, /cron resume <id> or /cron remove <id>."],
        };
    }

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
