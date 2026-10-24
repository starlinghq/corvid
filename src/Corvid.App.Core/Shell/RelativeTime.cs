using System.Globalization;

namespace Corvid.App.Core.Shell;

/// <summary>The rail's short times since a session's last activity, as the mockups print them: "12 s", "2 m", "1 h", "3 d".</summary>
public static class RelativeTime
{
    public static string Since(DateTimeOffset now, DateTimeOffset then)
    {
        var elapsed = now - then;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var (value, unit) = elapsed switch
        {
            { TotalSeconds: < 60 } => ((int)elapsed.TotalSeconds, "s"),
            { TotalMinutes: < 60 } => ((int)elapsed.TotalMinutes, "m"),
            { TotalHours: < 24 } => ((int)elapsed.TotalHours, "h"),
            _ => ((int)elapsed.TotalDays, "d"),
        };
        return string.Create(CultureInfo.InvariantCulture, $"{value} {unit}");
    }

    /// <summary>A job run is listed by when it started on the clock ("09:00"), as the scheduler names it.</summary>
    public static string ClockTime(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
