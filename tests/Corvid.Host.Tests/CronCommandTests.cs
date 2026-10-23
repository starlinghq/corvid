using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
using Corvid.Tools.Builtin;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary><c>/cron</c> (C-035) over the runtime's durable job store.</summary>
public sealed class CronCommandTests
{
    private static ValueTask<CommandOutput> Run(TestHost host, CorvidRuntime runtime, CorvidSession session, string line)
    {
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        return commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Jobs_are_listed_paused_resumed_and_removed()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var jobs = runtime.Builtin.Jobs;

        Assert.StartsWith("No active jobs.", (await Run(host, runtime, session, "/cron")).Lines[0], StringComparison.Ordinal);
        var inbox = jobs.Add(new NewJob("inbox", "check the inbox", "every 30m"));
        var disk = jobs.Add(new NewJob("disk", "", "every 2 hours", Script: "df -h", NoAgent: true, CreatedTainted: true));

        var list = await Run(host, runtime, session, "/cron list");
        Assert.Equal(["id", "name", "schedule", "state", "next run", "last run", "flags"], list.Table!.Columns);
        Assert.Equal([inbox.JobId, "inbox", "every 30m", "active"], list.Table.Rows[0].Take(4));
        Assert.Equal("-", list.Table.Rows[0][5]);
        Assert.Equal("-", list.Table.Rows[0][6]);
        Assert.Equal("no_agent, script, tainted", list.Table.Rows[1][6]);

        var paused = await Run(host, runtime, session, $"/cron pause {inbox.JobId}");
        Assert.Equal(OutputTone.Success, paused.Tone);
        Assert.True(jobs.Get(inbox.JobId)!.Paused);
        var row = (await Run(host, runtime, session, "/cron")).Table!.Rows[0];
        Assert.Equal(("paused", "-"), (row[3], row[4]));
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, $"/cron pause {inbox.JobId}")).Tone);

        var resumed = await Run(host, runtime, session, $"/cron resume {inbox.JobId}");
        Assert.Equal(OutputTone.Success, resumed.Tone);
        Assert.Contains("next run", resumed.Lines[0], StringComparison.Ordinal);
        Assert.False(jobs.Get(inbox.JobId)!.Paused);
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, $"/cron resume {inbox.JobId}")).Tone);

        Assert.Equal(OutputTone.Success, (await Run(host, runtime, session, $"/cron remove {disk.JobId}")).Tone);
        Assert.Single((await Run(host, runtime, session, "/cron")).Table!.Rows);
        var all = await Run(host, runtime, session, "/cron --all");
        Assert.Equal("removed", all.Table!.Rows.Single(item => item[0] == disk.JobId)[3]);
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, $"/cron remove {disk.JobId}")).Tone);
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, "/cron bogus")).Tone);
    }

    [Fact]
    public async Task The_last_run_shows_its_time_and_status()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var jobs = runtime.Builtin.Jobs;
        var job = jobs.Add(new NewJob("soon", "say hi", "in 1m"));
        var claimed = Assert.Single(jobs.ClaimDue(DateTimeOffset.UtcNow.AddMinutes(2)));
        jobs.Finish(claimed.Run.RunId, JobRunStatus.Ran, DateTimeOffset.UtcNow);

        var row = (await Run(host, runtime, session, "/cron --all")).Table!.Rows.Single(item => item[0] == job.JobId);
        Assert.Equal("completed", row[3]);
        Assert.EndsWith(" ran", row[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_lists_recent_runs_across_jobs_or_for_one()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var jobs = runtime.Builtin.Jobs;

        Assert.Equal("No job has run yet.", (await Run(host, runtime, session, "/cron runs")).Lines[0]);
        var inbox = jobs.Add(new NewJob("inbox", "check the inbox", "every 30m"));
        var disk = jobs.Add(new NewJob("disk", "", "every 2 hours", Script: "df -h", NoAgent: true));
        Assert.Equal($"{inbox.JobId} (inbox) has not run yet.", (await Run(host, runtime, session, $"/cron runs {inbox.JobId}")).Lines[0]);

        var now = DateTimeOffset.UtcNow;
        var first = jobs.ClaimDue(now.AddHours(2));
        Assert.Equal(2, first.Count);
        foreach (var claimed in first)
        {
            jobs.Finish(claimed.Run.RunId, claimed.Job.NoAgent ? JobRunStatus.ScriptOnly : JobRunStatus.Ran, claimed.Run.StartedAt.AddSeconds(42),
                output: claimed.Job.NoAgent ? "Filesystem Size Used\n/dev/sda1 100G 42G" : null,
                detail: claimed.Job.NoAgent ? null : "answered: " + new string('a', 100));
        }
        // A later run of the inbox job, still running.
        var later = Assert.Single(jobs.ClaimDue(now.AddHours(2).AddMinutes(31)));

        var all = await Run(host, runtime, session, "/cron runs");
        Assert.Equal("Recent job runs", all.Title);
        Assert.Equal(["run", "job", "scheduled for", "started", "took", "status", "detail", "session"], all.Table!.Columns);
        Assert.Equal(3, all.Table.Rows.Length);
        Assert.Equal((later.Run.RunId, "inbox", "-", JobRunStatus.Running, "-", later.Run.SessionId),
            (all.Table.Rows[0][0], all.Table.Rows[0][1], all.Table.Rows[0][4], all.Table.Rows[0][5], all.Table.Rows[0][6], all.Table.Rows[0][7]));
        Assert.Equal(CronTools.Local(later.Run.StartedAt, inbox), all.Table.Rows[0][3]);
        var script = all.Table.Rows.Single(item => item[1] == "disk");
        Assert.Equal(("42s", JobRunStatus.ScriptOnly, "Filesystem Size Used /dev/sda1 100G 42G"), (script[4], script[5], script[6]));
        var answered = all.Table.Rows.Single(item => item[1] == "inbox" && item[5] == JobRunStatus.Ran);
        Assert.Equal(60, answered[6].Length);
        Assert.EndsWith("…", answered[6], StringComparison.Ordinal);

        var one = await Run(host, runtime, session, $"/cron runs {inbox.JobId}");
        Assert.Equal($"Runs of {inbox.JobId} (inbox)", one.Title);
        Assert.Equal(["run", "scheduled for", "started", "took", "status", "detail", "session"], one.Table!.Columns);
        Assert.Equal(2, one.Table.Rows.Length);
        Assert.Equal(later.Run.RunId, one.Table.Rows[0][0]);
        Assert.DoesNotContain(one.Table.Rows, item => item[0] == script[0]);

        // A removed job's runs stay listed; an unknown id is an error; a stray argument is a usage error.
        Assert.True(jobs.Remove(disk.JobId));
        Assert.Single((await Run(host, runtime, session, $"/cron runs {disk.JobId}")).Table!.Rows);
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, "/cron runs job-none")).Tone);
        var usage = await Run(host, runtime, session, "/cron runs a b");
        Assert.Equal(OutputTone.Error, usage.Tone);
        Assert.Equal("Usage: /cron " + CronCommands.Usage, usage.Lines[0]);
        Assert.Equal("[list] [--all] | runs [<id>] | pause <id> | resume <id> | remove <id>", CronCommands.Usage);
        Assert.Contains("/cron runs", (await Run(host, runtime, session, "/cron")).Lines[0], StringComparison.Ordinal);
    }
}
