using System.Globalization;

namespace Corvid.App.Core.Jobs;

/// <summary>The Jobs page's wording: times relative to today in the job's zone, how long a run took, a state in words.</summary>
public static class JobsText
{
    /// <summary>How many runs the history lists, as <c>/cron runs</c> does.</summary>
    public const int RunsShown = 20;

    /// <summary>"today 09:00", "tomorrow 09:00", "Wed 09:00" within the week, "Mon 2 Nov 09:00", or with the year when it differs.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var days = (local.Date - today).Days;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            -1 => $"yesterday {time}",
            > 1 and < 7 => local.ToString("ddd ", CultureInfo.InvariantCulture) + time,
            _ when local.Year == today.Year => local.ToString("ddd d MMM ", CultureInfo.InvariantCulture) + time,
            _ => local.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + time,
        };
    }

    /// <summary>"41 s", "2 m 05 s", "1 h 02 m", or "running" for a run without an end.</summary>
    public static string Took(JobRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.FinishedAt is not { } finished)
            return run.Status == JobRunStatus.Running ? "running" : "—";
        var took = finished - run.StartedAt;
        return took < TimeSpan.FromSeconds(1) ? "<1 s"
            : took < TimeSpan.FromMinutes(1) ? string.Create(CultureInfo.InvariantCulture, $"{(int)took.TotalSeconds} s")
            : took < TimeSpan.FromHours(1) ? string.Create(CultureInfo.InvariantCulture, $"{(int)took.TotalMinutes} m {took.Seconds:00} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)took.TotalHours} h {took.Minutes:00} m");
    }

    public static string State(JobState state) => state switch
    {
        JobState.Active => "active",
        JobState.Paused => "paused",
        JobState.Finished => "finished",
        _ => "removed",
    };

    public static Tone StateTone(JobState state) => state switch
    {
        JobState.Active => Tone.Green,
        JobState.Paused => Tone.Amber,
        _ => Tone.Grey,
    };

    public static string Status(JobRunStatus status) => status switch
    {
        JobRunStatus.Ok => "ok",
        JobRunStatus.Failed => "failed",
        JobRunStatus.Skipped => "skipped",
        _ => "running",
    };

    public static Tone StatusTone(JobRunStatus status) => status switch
    {
        JobRunStatus.Ok => Tone.Green,
        JobRunStatus.Failed => Tone.Red,
        JobRunStatus.Running => Tone.Blue,
        _ => Tone.Grey,
    };
}
