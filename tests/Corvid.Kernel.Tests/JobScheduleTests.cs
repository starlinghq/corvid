using Corvid.Kernel.Jobs;

namespace Corvid.Kernel.Tests;

public sealed class JobScheduleTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // Monday 19 October 2026, 10:15 in London (BST, UTC+1).
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 15, 0, TimeSpan.Zero);

    private static DateTimeOffset LondonTime(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0), London.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

    [Theory]
    [InlineData("in 30m", 30)]
    [InlineData("30m", 30)]
    [InlineData("in 2 hours", 120)]
    [InlineData("in 1h30m", 90)]
    [InlineData("in 1 hour and 15 minutes", 75)]
    [InlineData("In 45 Minutes.", 45)]
    [InlineData("2d", 2880)]
    public void Delays_run_once_after_the_delay(string text, int minutes)
    {
        var schedule = JobSchedule.Parse(text, Now, London);

        Assert.Equal(ScheduleKind.Once, schedule.Kind);
        Assert.Equal(Now.AddMinutes(minutes), schedule.First(Now));
        Assert.Null(schedule.Following(schedule.At!.Value, schedule.At.Value));
    }

    [Theory]
    [InlineData("every 30m", 30)]
    [InlineData("every 2 hours", 120)]
    [InlineData("every hour", 60)]
    [InlineData("every day", 1440)]
    [InlineData("every 1h 30m", 90)]
    public void Intervals_repeat_from_the_first_run(string text, int minutes)
    {
        var schedule = JobSchedule.Parse(text, Now, London);

        Assert.Equal(ScheduleKind.Interval, schedule.Kind);
        var first = schedule.First(Now)!.Value;
        Assert.Equal(Now.AddMinutes(minutes), first);
        Assert.Equal(first.AddMinutes(minutes), schedule.Following(first, first));
        // Missed runs collapse: three intervals late, the next run is the next slot after now, not a burst of three.
        Assert.Equal(first.AddMinutes(minutes * 4), schedule.Following(first, first.AddMinutes(minutes * 3 + 1)));
    }

    [Theory]
    [InlineData("daily at 9am", "0 9 * * *")]
    [InlineData("every day at 09:30", "30 9 * * *")]
    [InlineData("weekdays at 9am", "0 9 * * 1-5")]
    [InlineData("every weekday at 8:15 pm", "15 20 * * 1-5")]
    [InlineData("weekends at noon", "0 12 * * 0,6")]
    [InlineData("mondays at 17:30", "30 17 * * 1")]
    [InlineData("every friday at midnight", "0 0 * * 5")]
    [InlineData("on mon, wed and fri at 8pm", "0 20 * * 1,3,5")]
    [InlineData("tues, thurs at 7am", "0 7 * * 2,4")]
    [InlineData("0 9 * * 1-5", "0 9 * * 1-5")]
    [InlineData("*/15 9-17 * * mon-fri", "*/15 9-17 * * mon-fri")]
    [InlineData("@daily", "0 0 * * *")]
    [InlineData("hourly", "0 * * * *")]
    public void Days_and_times_and_cron_become_cron(string text, string cron)
    {
        var schedule = JobSchedule.Parse(text, Now, London);

        Assert.Equal(ScheduleKind.Cron, schedule.Kind);
        Assert.Equal("cron " + cron, schedule.Canonical);
    }

    [Fact]
    public void Cron_times_are_wall_clock_times_in_the_zone()
    {
        var weekdays = JobSchedule.Parse("weekdays at 9am", Now, London);

        // Monday 10:15 is past 9am, so the next is Tuesday at 9am BST.
        Assert.Equal(LondonTime(2026, 10, 20, 9, 0), weekdays.First(Now));
        // Friday's run is followed by Monday's, which is after the clocks go back (25 October): 9am GMT.
        Assert.Equal(LondonTime(2026, 10, 26, 9, 0), weekdays.Following(LondonTime(2026, 10, 23, 9, 0), LondonTime(2026, 10, 23, 9, 0)));
        Assert.Equal(TimeSpan.Zero, weekdays.Following(LondonTime(2026, 10, 23, 9, 0), LondonTime(2026, 10, 23, 9, 0))!.Value.Offset);
    }

    [Fact]
    public void Daylight_saving_gaps_are_skipped_and_repeats_run_once()
    {
        var nightly = JobSchedule.Parse("daily at 1:30am", Now, London);

        // 29 March 2026: 01:00–02:00 does not exist in London, so that day has no 1:30.
        var beforeSpring = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 1, 30, 0, TimeSpan.FromHours(1)), nightly.First(beforeSpring));
        // 25 October 2026: 01:00–02:00 happens twice; the run is at the first 1:30 (BST) and not again at the second.
        var beforeAutumn = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var first = nightly.First(beforeAutumn)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(1)), first);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 1, 30, 0, TimeSpan.Zero), nightly.Following(first, first.AddMinutes(30)));
    }

    [Fact]
    public void One_shot_days_and_times()
    {
        Assert.Equal(LondonTime(2026, 10, 20, 9, 0), JobSchedule.Parse("tomorrow at 9am", Now, London).At);
        Assert.Equal(LondonTime(2026, 10, 19, 17, 0), JobSchedule.Parse("today at 5pm", Now, London).At);
        // "at 9am" is the next 9am: today's has passed.
        Assert.Equal(LondonTime(2026, 10, 20, 9, 0), JobSchedule.Parse("at 9am", Now, London).At);
        Assert.Equal(LondonTime(2026, 10, 19, 11, 0), JobSchedule.Parse("at 11:00", Now, London).At);
    }

    [Fact]
    public void Iso_times_with_or_without_an_offset()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), JobSchedule.Parse("2026-11-01T09:00Z", Now, London).At);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(2)), JobSchedule.Parse("2026-11-01T09:00+02:00", Now, London).At);
        // Without an offset it is wall-clock time in the zone (GMT by then).
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), JobSchedule.Parse("2026-11-01 09:00", Now, London).At);
    }

    [Theory]
    [InlineData("")]
    [InlineData("whenever")]
    [InlineData("in 30 parsecs")]
    [InlineData("every 10s")]
    [InlineData("today at 9am")]
    [InlineData("2020-01-01T00:00Z")]
    [InlineData("at 25:00")]
    [InlineData("at 13pm")]
    [InlineData("caturdays at 9am")]
    [InlineData("61 * * * *")]
    [InlineData("0 9 * * 8")]
    [InlineData("5-1 * * * *")]
    public void Invalid_or_past_schedules_are_refused_with_a_reason(string text)
    {
        var error = Assert.Throws<FormatException>(() => JobSchedule.Parse(text, Now, London));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void The_canonical_form_round_trips_without_the_parse_time()
    {
        foreach (var text in (string[])["in 30m", "every 2 hours", "weekdays at 9am", "2026-11-01T09:00Z"])
        {
            var parsed = JobSchedule.Parse(text, Now, London);
            var restored = JobSchedule.FromCanonical(parsed.Canonical, London);
            Assert.Equal(parsed.Canonical, restored.Canonical);
            Assert.Equal(parsed.First(Now), restored.First(Now));
        }

        Assert.Throws<FormatException>(() => JobSchedule.FromCanonical("weekdays at 9am", London));
    }

    [Fact]
    public void Cron_day_fields_follow_vixie_cron()
    {
        // Both restricted: the 1st of the month or any Friday.
        var either = CronExpression.Parse("0 12 1 * fri");
        var start = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero), either.Next(start, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), either.Next(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        // 7 is Sunday; steps from a start; names for months.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 5, 0, TimeSpan.Zero), CronExpression.Parse("5/30 0 * * 7").Next(start, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), CronExpression.Parse("0 0 1 feb *").Next(start, TimeZoneInfo.Utc));
        // A date that never comes.
        Assert.Null(CronExpression.Parse("0 0 30 feb *").Next(start, TimeZoneInfo.Utc));
    }

    /// <summary>G-13: the preview lists the runs a new job would have, timed as the store and scheduler time them.</summary>
    [Fact]
    public void Upcoming_lists_the_runs_a_new_job_would_have()
    {
        var weekdays = JobSchedule.Parse("weekdays at 9am", Now, London);
        Assert.Equal(
            [LondonTime(2026, 10, 20, 9, 0), LondonTime(2026, 10, 21, 9, 0), LondonTime(2026, 10, 22, 9, 0), LondonTime(2026, 10, 23, 9, 0),
             LondonTime(2026, 10, 26, 9, 0)],
            weekdays.Upcoming(Now, 5));

        var every = JobSchedule.Parse("every 2 hours", Now, London);
        Assert.Equal([Now.AddHours(2), Now.AddHours(4), Now.AddHours(6)], every.Upcoming(Now, 3));

        // A one-shot runs once, however many are asked for.
        var once = JobSchedule.Parse("in 30m", Now, London);
        Assert.Equal([Now.AddMinutes(30)], once.Upcoming(Now, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => once.Upcoming(Now, 0));
    }
}
