using System.Globalization;
using System.Text.RegularExpressions;

namespace Corvid.App.Core.Jobs;

/// <summary>
/// The schedule grammar the Jobs form previews (docs/plan/ui.md §4.10): "in 30m", "every 2h", "every day at 9am",
/// "weekdays at 9am", "fridays at 4pm", a five-field cron line, or an ISO time. It reads a text into what the scheduler would
/// run (a cron line, an interval or one time) and the next runs from <c>now</c> in the job's zone, so the form can say
/// "runs next at" before anything is written. The engine's own reading (G-13) replaces it; the demo uses it as is.
/// </summary>
public static partial class ScheduleGrammar
{
    /// <summary>How many runs a preview lists.</summary>
    public const int Shown = 3;

    /// <summary>How far ahead a cron line is searched for its runs: five years covers a 29 February on a given weekday.</summary>
    private const int SearchDays = 366 * 5;

    public const string Examples = "\"in 30m\", \"weekdays at 9am\", \"0 9 * * 1-5\" or \"2026-10-25T09:00\"";

    public static SchedulePreview Read(string? text, DateTimeOffset now, TimeZoneInfo zone, int count = Shown)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (string.IsNullOrWhiteSpace(text))
            return SchedulePreview.Invalid($"Say when it runs: {Examples}.");
        if (SafeText.HasHidden(text))
            return SchedulePreview.Invalid("The schedule holds hidden characters; type it again.");
        var input = Spaces().Replace(text.Trim().ToLowerInvariant(), " ");

        if (InOnce().Match(input) is { Success: true } once)
        {
            if (Span(once.Groups[1].Value, once.Groups[2].Value) is not { } after)
                return SchedulePreview.Invalid("That is too far ahead; use an ISO time.");
            var at = Minute(now + after);
            return new SchedulePreview(true, $"once, in {Amount(after)}", [at]);
        }

        if (Every().Match(input) is { Success: true } every)
        {
            if (Span(every.Groups[1].Value, every.Groups[2].Value) is not { } interval || interval < TimeSpan.FromMinutes(1))
                return SchedulePreview.Invalid("An interval runs at most once a minute.");
            var start = Minute(now);
            var runs = Enumerable.Range(1, count).Select(i => start + interval * i).ToList();
            return new SchedulePreview(true, $"every {Interval(interval)}", runs);
        }

        if (AtTime().Match(input) is { Success: true } daily)
        {
            if (Time(daily.Groups["time"].Value) is not { } time)
                return SchedulePreview.Invalid("Write the time as 9am, 4:30pm, 09:00, noon or midnight.");
            if (Days(daily.Groups["days"].Value) is not { } days)
                return SchedulePreview.Invalid($"Name the days: every day, weekdays, weekends or a weekday such as fridays.");
            return Cron($"{time.Minutes} {time.Hours} * * {days}", now, zone, count);
        }

        if (input.Split(' ') is { Length: 5 } fields && fields.All(field => CronField().IsMatch(field)))
            return Cron(input, now, zone, count);

        if (Iso(text.Trim(), zone) is { } instant)
        {
            if (instant <= now)
                return SchedulePreview.Invalid("That time has passed.");
            return new SchedulePreview(true, "once, at " + instant.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture), [instant]);
        }

        return SchedulePreview.Invalid($"Not a schedule Corvid reads. Try {Examples}.");
    }

    /// <summary>A five-field cron line (minute, hour, day of month, month, day of week) and its next runs in <paramref name="zone"/>.</summary>
    public static SchedulePreview Cron(string line, DateTimeOffset now, TimeZoneInfo zone, int count = Shown)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(zone);
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return SchedulePreview.Invalid("A cron line has five fields: minute, hour, day of month, month, day of week.");
        var minutes = Field(fields[0], 0, 59);
        var hours = Field(fields[1], 0, 23);
        var monthDays = Field(fields[2], 1, 31);
        var months = Field(fields[3], 1, 12);
        var weekDays = Field(fields[4], 0, 7);
        if (minutes is null || hours is null || monthDays is null || months is null || weekDays is null)
            return SchedulePreview.Invalid("A cron field is out of range: minute 0-59, hour 0-23, day 1-31, month 1-12, weekday 0-7.");
        if (weekDays[7])
            weekDays[0] = true;
        var dayRestricted = fields[2] != "*";
        var weekRestricted = fields[4] != "*";

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var from = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var runs = new List<DateTimeOffset>();
        for (var day = 0; day < SearchDays && runs.Count < count; day++)
        {
            var date = from.Date.AddDays(day);
            if (!months[date.Month])
                continue;
            var byMonth = monthDays[date.Day];
            var byWeek = weekDays[(int)date.DayOfWeek];
            var matches = dayRestricted && weekRestricted ? byMonth || byWeek : dayRestricted ? byMonth : !weekRestricted || byWeek;
            if (!matches)
                continue;
            for (var hour = 0; hour < 24 && runs.Count < count; hour++)
            {
                if (!hours[hour])
                    continue;
                for (var minute = 0; minute < 60 && runs.Count < count; minute++)
                {
                    if (!minutes[minute])
                        continue;
                    var wall = date.AddHours(hour).AddMinutes(minute);
                    if (wall < from || zone.IsInvalidTime(wall))
                        continue;
                    runs.Add(new DateTimeOffset(wall, zone.GetUtcOffset(wall)));
                }
            }
        }

        return runs.Count == 0
            ? SchedulePreview.Invalid("That cron line never runs.")
            : new SchedulePreview(true, "cron " + string.Join(' ', fields), runs);
    }

    private static bool[]? Field(string field, int low, int high)
    {
        var set = new bool[high + 1];
        foreach (var item in field.Split(','))
        {
            var step = 1;
            var range = item;
            var slash = item.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                if (!int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    return null;
                range = item[..slash];
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (low, high);
            }
            else if (range.Split('-') is [var a, var b])
            {
                if (!Number(a, out from) || !Number(b, out to) || from > to)
                    return null;
            }
            else if (Number(range, out from))
            {
                to = slash >= 0 ? high : from;
            }
            else
            {
                return null;
            }

            if (from < low || to > high)
                return null;
            for (var value = from; value <= to; value += step)
                set[value] = true;
        }

        return set;
    }

    private static bool Number(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static TimeSpan? Span(string amount, string unit)
    {
        if (!int.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 100_000)
            return null;
        return unit[0] switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ => n > 3650 ? null : TimeSpan.FromDays(n),
        };
    }

    /// <summary>"hour", "2 hours", "15 minutes": an interval as "every" reads it.</summary>
    private static string Interval(TimeSpan interval)
    {
        var amount = Amount(interval);
        return amount.StartsWith("1 ", StringComparison.Ordinal) ? amount[2..] : amount;
    }

    /// <summary>"1 hour", "2 days", "30 minutes".</summary>
    private static string Amount(TimeSpan span)
    {
        var (n, unit) = span.TotalDays >= 1 && span.TotalDays % 1 == 0 ? ((int)span.TotalDays, "day")
            : span.TotalHours >= 1 && span.TotalHours % 1 == 0 ? ((int)span.TotalHours, "hour")
            : ((int)span.TotalMinutes, "minute");
        return n == 1 ? $"1 {unit}" : $"{n.ToString(CultureInfo.InvariantCulture)} {unit}s";
    }

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset);

    /// <summary>"9am", "4:30pm", "09:00", "noon", "midnight".</summary>
    private static TimeSpan? Time(string text)
    {
        if (text == "noon")
            return TimeSpan.FromHours(12);
        if (text == "midnight")
            return TimeSpan.Zero;
        if (Clock().Match(text) is not { Success: true } match)
            return null;
        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var half = match.Groups[3].Value;
        if (half.Length > 0)
        {
            if (hour is < 1 or > 12)
                return null;
            hour = hour % 12 + (half == "pm" ? 12 : 0);
        }

        return hour is < 0 or > 23 || minute is < 0 or > 59 ? null : new TimeSpan(hour, minute, 0);
    }

    /// <summary>The cron weekday field for "every day", "weekdays", "weekends", "fridays", "every monday", "mon".</summary>
    private static string? Days(string text)
    {
        var words = text.Replace("every ", "", StringComparison.Ordinal).Replace("on ", "", StringComparison.Ordinal).Trim();
        return words switch
        {
            "day" or "daily" or "each day" => "*",
            "weekday" or "weekdays" => "1-5",
            "weekend" or "weekends" => "0,6",
            _ => DayNames.FindIndex(name => words == name || words == name + "s" || words == name[..3]) is var index and >= 0
                ? index.ToString(CultureInfo.InvariantCulture)
                : null,
        };
    }

    private static readonly List<string> DayNames = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private static DateTimeOffset? Iso(string text, TimeZoneInfo zone)
    {
        string[] withOffset = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd HH:mmzzz", "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"];
        if (DateTimeOffset.TryParseExact(text, withOffset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
            return exact;
        string[] wall = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
        if (DateTime.TryParseExact(text, wall, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
                return null;
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^in (\d+) ?(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)$")]
    private static partial Regex InOnce();

    [GeneratedRegex(@"^every (\d+) ?(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)$")]
    private static partial Regex Every();

    [GeneratedRegex(@"^(?<days>(?:every |on )?[a-z]+(?: day)?|daily) at (?<time>noon|midnight|\d{1,2}(?::\d{2})? ?(?:am|pm)?)$")]
    private static partial Regex AtTime();

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))? ?(am|pm)?$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^[0-9*,/\-]+$")]
    private static partial Regex CronField();
}
