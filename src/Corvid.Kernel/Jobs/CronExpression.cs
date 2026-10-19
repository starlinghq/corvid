using System.Globalization;

namespace Corvid.Kernel.Jobs;

/// <summary>
/// A five-field cron expression (minute, hour, day of month, month, day of week) with lists, ranges, steps and names
/// (<c>jan</c>–<c>dec</c>, <c>sun</c>–<c>sat</c>; 7 is also Sunday). As in Vixie cron, when both day fields are
/// restricted a day matches either; when one is <c>*</c> only the other applies.
/// </summary>
public sealed class CronExpression
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronExpression(string text, bool[] minutes, bool[] hours, bool[] days, bool[] months, bool[] weekdays, bool anyDay, bool anyWeekday)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
        _anyDay = anyDay;
        _anyWeekday = anyWeekday;
    }

    /// <summary>The expression with its fields separated by single spaces, lower case.</summary>
    public string Text { get; }

    public override string ToString() => Text;

    /// <exception cref="FormatException">Not five valid fields.</exception>
    public static CronExpression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] fields = text.Trim().ToLowerInvariant().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException($"A cron expression has five fields (minute hour day-of-month month day-of-week); '{text}' has {fields.Length}.");
        var weekdays = Field(fields[4], 0, 7, DayNames, "day of week");
        // 7 is Sunday too.
        if (weekdays[7])
            weekdays[0] = true;
        return new CronExpression(string.Join(' ', fields),
            Field(fields[0], 0, 59, null, "minute"), Field(fields[1], 0, 23, null, "hour"), Field(fields[2], 1, 31, null, "day of month"),
            Field(fields[3], 1, 12, MonthNames, "month"), weekdays[..7], fields[2] == "*", fields[4] == "*");
    }

    /// <summary>True when <paramref name="text"/> looks like five cron fields, so a parse error is worth reporting as one.</summary>
    public static bool LooksLikeCron(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] fields = text.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 5 && fields.All(field => field.All(c => char.IsAsciiLetterOrDigit(c) || c is '*' or ',' or '-' or '/'))
            && fields.Take(2).All(field => field.Any(c => char.IsAsciiDigit(c) || c == '*'));
    }

    /// <summary>
    /// The first matching wall-clock minute strictly after <paramref name="after"/>, as an instant in <paramref name="zone"/>.
    /// A minute skipped by a daylight-saving jump does not occur; a repeated one occurs once, at its first instant.
    /// </summary>
    /// <returns>Null when nothing matches within five years (for example 30 February).</returns>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        for (DateTime day = start.Date; day < start.Date.AddYears(5); day = day.AddDays(1))
        {
            if (!MatchesDay(day))
                continue;
            for (int hour = day == start.Date ? start.Hour : 0; hour < 24; hour++)
            {
                if (!_hours[hour])
                    continue;
                int firstMinute = day == start.Date && hour == start.Hour ? start.Minute : 0;
                for (int minute = firstMinute; minute < 60; minute++)
                {
                    if (_minutes[minute] && Instant(day.AddHours(hour).AddMinutes(minute), zone) is { } instant && instant > after)
                        return instant;
                }
            }
        }

        return null;
    }

    private bool MatchesDay(DateTime day)
    {
        if (!_months[day.Month])
            return false;
        bool dayOfMonth = _days[day.Day];
        bool dayOfWeek = _weekdays[(int)day.DayOfWeek];
        return _anyDay || _anyWeekday ? dayOfMonth && dayOfWeek : dayOfMonth || dayOfWeek;
    }

    /// <summary>A wall-clock time in a zone as an instant: null in a daylight-saving gap, the earlier instant when repeated.</summary>
    internal static DateTimeOffset? Instant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            return null;
        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static bool[] Field(string field, int min, int max, string[]? names, string what)
    {
        var allowed = new bool[max + 1];
        foreach (string item in field.Split(','))
        {
            if (item.Length == 0)
                throw new FormatException($"The {what} field '{field}' has an empty list item.");
            string range = item;
            int step = 1;
            int slash = item.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                range = item[..slash];
                if (!int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                    throw new FormatException($"The {what} field '{field}' has an invalid step.");
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.IndexOf('-', StringComparison.Ordinal) is var dash and > 0)
            {
                from = Value(range[..dash], min, max, names, what);
                to = Value(range[(dash + 1)..], min, max, names, what);
                if (to < from)
                    throw new FormatException($"The {what} range '{range}' runs backwards.");
            }
            else
            {
                from = Value(range, min, max, names, what);
                // "5/15" means from 5 to the end in steps of 15, as in Vixie cron.
                to = slash >= 0 ? max : from;
            }

            for (int value = from; value <= to; value += step)
                allowed[value] = true;
        }

        return allowed;
    }

    private static int Value(string text, int min, int max, string[]? names, string what)
    {
        if (names is not null && Array.IndexOf(names, text) is var index and >= 0)
            return index + (min == 1 ? 1 : 0);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new FormatException($"'{text}' is not a valid {what} ({min}–{max}).");
        return value;
    }
}
