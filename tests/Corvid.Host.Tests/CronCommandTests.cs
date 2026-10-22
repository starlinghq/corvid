using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
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
}
