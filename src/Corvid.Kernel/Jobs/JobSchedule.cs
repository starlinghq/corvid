using System.Globalization;
using System.Text.RegularExpressions;

namespace Corvid.Kernel.Jobs;

public enum ScheduleKind
{
    /// <summary>Runs once, at <see cref="JobSchedule.At"/>.</summary>
    Once,
    /// <summary>Runs every <see cref="JobSchedule.Every"/>, counted from the first run.</summary>
    Interval,
    /// <summary>Runs on a <see cref="CronExpression"/> in the schedule's time zone.</summary>
    Cron,
}

/// <summary>
/// When a job runs (plan §13), parsed from Hermes' schedule grammar: a delay (<c>in 30m</c>, <c>2h</c>), an interval
/// (<c>every 30m</c>, <c>every 2 hours</c>), a day and time (<c>at 9am</c>, <c>tomorrow at 9am</c>, <c>daily at 9am</c>,
/// <c>weekdays at 9am</c>, <c>mondays at 17:30</c>, <c>on mon, wed and fri at 8pm</c>), a cron expression
/// (<c>0 9 * * 1-5</c>, <c>@daily</c>) or an ISO 8601 time (<c>2026-11-01T09:00</c>, with an offset or in the
/// schedule's zone). The canonical form (<see cref="Canonical"/>) no longer depends on when it was parsed, so it is what
/// the job store keeps.
/// </summary>
public sealed partial class JobSchedule
{
    /// <summary>The shortest interval: a job that wakes more often than this is a busy loop, not a schedule.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    public const string Grammar =
        "a delay ('in 30m', '2h'), an interval ('every 30m', 'every 2 hours'), a day and time ('at 9am', 'tomorrow at 9am', " +
        "'daily at 9am', 'weekdays at 9am', 'mondays at 17:30'), a cron expression ('0 9 * * 1-5', '@daily') or an ISO time " +
        "('2026-11-01T09:00')";

    private static readonly Dictionary<string, string> Macros = new(StringComparer.Ordinal)
    {
        ["@hourly"] = "0 * * * *",
        ["hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@weekly"] = "0 0 * * 0",
        ["@monthly"] = "0 0 1 * *",
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
    };

    private static readonly string[] DayNames = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private JobSchedule(ScheduleKind kind, TimeZoneInfo zone, DateTimeOffset? at = null, TimeSpan? every = null, CronExpression? cron = null)
    {
        Kind = kind;
        TimeZone = zone;
        At = at;
        Every = every;
        Cron = cron;
    }

    public ScheduleKind Kind { get; }

    public TimeZoneInfo TimeZone { get; }

    public DateTimeOffset? At { get; }

    public TimeSpan? Every { get; }

    public CronExpression? Cron { get; }

    public bool Recurring => Kind != ScheduleKind.Once;

    /// <summary><c>once 2026-11-01T09:00:00.0000000+00:00</c>, <c>every 1800s</c> or <c>cron 0 9 * * 1-5</c>.</summary>
    public string Canonical => Kind switch
    {
        ScheduleKind.Once => "once " + At!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ScheduleKind.Interval => "every " + ((long)Every!.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s",
        _ => "cron " + Cron!.Text,
    };

    public override string ToString() => Canonical;

    /// <summary>The first run: the time itself for a one-shot, one interval from now, or the next cron match.</summary>
    public DateTimeOffset? First(DateTimeOffset now) => Kind switch
    {
        ScheduleKind.Once => At,
        ScheduleKind.Interval => now + Every!.Value,
        _ => Cron!.Next(now, TimeZone),
    };

    /// <summary>
    /// The run after the one scheduled for <paramref name="scheduled"/>, strictly after <paramref name="now"/>: runs missed
    /// while Corvid was not running collapse into the one that is happening, rather than firing in a burst. Null for a
    /// one-shot.
    /// </summary>
    public DateTimeOffset? Following(DateTimeOffset scheduled, DateTimeOffset now)
    {
        switch (Kind)
        {
            case ScheduleKind.Once:
                return null;
            case ScheduleKind.Interval:
            {
                long every = Every!.Value.Ticks;
                long behind = Math.Max(0, (now - scheduled).Ticks);
                return scheduled + TimeSpan.FromTicks((behind / every + 1) * every);
            }

            default:
                return Cron!.Next(now > scheduled ? now : scheduled, TimeZone);
        }
    }

    /// <summary>
    /// G-13's preview: the next <paramref name="count"/> runs a job with this schedule would have if it were created at
    /// <paramref name="now"/>, as <see cref="JobStore.Add"/> and the scheduler would time them (the first run, then each
    /// following one). Fewer for a one-shot (one) or a cron line that stops matching; nothing is written.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Upcoming(DateTimeOffset now, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var runs = new List<DateTimeOffset>(count);
        var next = First(now);
        while (next is { } at && runs.Count < count)
        {
            runs.Add(at);
            next = Following(at, at);
        }
        return runs;
    }

    /// <summary>Reads a canonical form back. Throws <see cref="FormatException"/> for anything else.</summary>
    public static JobSchedule FromCanonical(string canonical, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(zone);
        if (canonical.StartsWith("once ", StringComparison.Ordinal)
            && DateTimeOffset.TryParseExact(canonical[5..], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return new JobSchedule(ScheduleKind.Once, zone, at: at);
        if (canonical.StartsWith("every ", StringComparison.Ordinal) && canonical.EndsWith('s')
            && long.TryParse(canonical[6..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && seconds > 0)
            return new JobSchedule(ScheduleKind.Interval, zone, every: TimeSpan.FromSeconds(seconds));
        if (canonical.StartsWith("cron ", StringComparison.Ordinal))
            return new JobSchedule(ScheduleKind.Cron, zone, cron: CronExpression.Parse(canonical[5..]));
        throw new FormatException($"'{canonical}' is not a canonical schedule.");
    }

    /// <summary>Parses the schedule grammar (see the type's summary) relative to <paramref name="now"/>.</summary>
    /// <exception cref="FormatException">The text is not a schedule, names a time already past, or an interval under a minute.</exception>
    public static JobSchedule Parse(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(zone);
        string s = Spaces().Replace(text.Trim().TrimEnd('.').ToLowerInvariant(), " ");
        if (s.Length == 0)
            throw new FormatException("The schedule is empty; use " + Grammar + ".");

        if (Macros.TryGetValue(s, out string? macro))
            return new JobSchedule(ScheduleKind.Cron, zone, cron: CronExpression.Parse(macro));
        if (CronExpression.LooksLikeCron(s))
            return new JobSchedule(ScheduleKind.Cron, zone, cron: CronExpression.Parse(s));
        if (Iso().IsMatch(s))
            return Once(IsoTime(text.Trim(), zone), now, zone, text);

        if (Delay().Match(s) is { Success: true } delay && Duration(delay.Groups["d"].Value) is { } wait)
            return Once(now + wait, now, zone, text);

        if (Calendar().Match(s) is { Success: true } calendar)
        {
            (int hour, int minute) = TimeOfDay(calendar.Groups["time"].Value, text);
            string days = calendar.Groups["days"].Value.Trim();
            bool every = calendar.Groups["every"].Success;
            switch (days)
            {
                case "" when !every:
                case "today" or "tonight":
                case "tomorrow":
                {
                    DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;
                    DateTime day = days == "tomorrow" ? today.AddDays(1) : today;
                    DateTimeOffset? at = CronExpression.Instant(day.AddHours(hour).AddMinutes(minute), zone);
                    // "at 9am" means the next 9am; "today at 9am" after 9am is a mistake worth reporting.
                    if (days == "" && (at is null || at <= now))
                        at = CronExpression.Instant(day.AddDays(1).AddHours(hour).AddMinutes(minute), zone);
                    if (at is null)
                        throw new FormatException($"'{text}' falls in a daylight-saving gap in {zone.Id}.");
                    return Once(at.Value, now, zone, text);
                }

                default:
                {
                    string weekdays = Weekdays(days, text);
                    return new JobSchedule(ScheduleKind.Cron, zone,
                        cron: CronExpression.Parse(string.Create(CultureInfo.InvariantCulture, $"{minute} {hour} * * {weekdays}")));
                }
            }
        }

        // After the calendar forms, so 'every day at 9am' is a day and time, not an interval.
        if (Interval().Match(s) is { Success: true } interval)
        {
            string body = interval.Groups["d"].Value;
            if ((Duration(body) ?? Duration("1 " + body)) is not { } every)
                throw new FormatException($"'{text}' is not an interval; write it as 'every 30m' or 'every 2 hours'.");
            if (every < MinimumInterval)
                throw new FormatException($"'{text}' is shorter than the minimum interval of one minute.");
            return new JobSchedule(ScheduleKind.Interval, zone, every: every);
        }

        throw new FormatException($"'{text}' is not a schedule; use {Grammar}.");
    }

    private static JobSchedule Once(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone, string text) =>
        at > now
            ? new JobSchedule(ScheduleKind.Once, zone, at: at)
            : throw new FormatException($"'{text}' is in the past.");

    private static DateTimeOffset IsoTime(string text, TimeZoneInfo zone)
    {
        bool hasOffset = text.EndsWith('Z') || text.EndsWith('z') || OffsetSuffix().IsMatch(text);
        if (hasOffset)
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                ? instant
                : throw new FormatException($"'{text}' is not a valid ISO 8601 time.");
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
            throw new FormatException($"'{text}' is not a valid ISO 8601 time.");
        return CronExpression.Instant(local, zone) ?? throw new FormatException($"'{text}' falls in a daylight-saving gap in {zone.Id}.");
    }

    /// <summary>A duration as one or more number-unit pairs: <c>30m</c>, <c>1h30m</c>, <c>2 hours and 15 minutes</c>.</summary>
    private static TimeSpan? Duration(string text)
    {
        var total = TimeSpan.Zero;
        int matched = 0;
        foreach (Match part in DurationPart().Matches(text))
        {
            if (!long.TryParse(part.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n > 100_000)
                return null;
            TimeSpan unit = part.Groups["u"].Value switch
            {
                "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(1),
                "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(1),
                "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(1),
                "d" or "day" or "days" => TimeSpan.FromDays(1),
                _ => TimeSpan.FromDays(7),
            };
            total += unit * n;
            matched += part.Length;
        }

        // Every character but separators must belong to a pair, so "30 parsecs" is not 30 seconds.
        string rest = DurationPart().Replace(text, "").Replace("and", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        return matched > 0 && rest.Length == 0 && total > TimeSpan.Zero ? total : null;
    }

    private static (int Hour, int Minute) TimeOfDay(string time, string text)
    {
        switch (time)
        {
            case "noon":
                return (12, 0);
            case "midnight":
                return (0, 0);
        }

        Match match = Clock().Match(time);
        if (!match.Success)
            throw new FormatException($"'{time}' in '{text}' is not a time of day; write 9am, 9:30pm or 21:30.");
        int hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        string meridiem = match.Groups["ampm"].Value;
        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12)
                throw new FormatException($"'{time}' in '{text}' is not a time of day.");
            hour = hour % 12 + (meridiem.StartsWith('p') ? 12 : 0);
        }

        if (hour > 23 || minute > 59)
            throw new FormatException($"'{time}' in '{text}' is not a time of day.");
        return (hour, minute);
    }

    /// <summary>The cron day-of-week field for <c>day</c>, <c>weekdays</c>, <c>weekends</c> or a list of day names.</summary>
    private static string Weekdays(string days, string text)
    {
        switch (days)
        {
            case "" or "day" or "days" or "daily" or "everyday" or "every day":
                return "*";
            case "weekday" or "weekdays":
                return "1-5";
            case "weekend" or "weekends":
                return "0,6";
        }

        var found = new SortedSet<int>();
        foreach (string item in days.Split([",", " and ", " "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = item.TrimEnd('s');
            int index = Array.FindIndex(DayNames, day => name.Length >= 3 && day.StartsWith(name, StringComparison.Ordinal)
                || day.TrimEnd('s') == name || day == item);
            if (index < 0 && name is "tue" or "tues")
                index = 2;
            if (index < 0 && name is "thu" or "thur" or "thurs")
                index = 4;
            if (index < 0)
                throw new FormatException($"'{item}' in '{text}' is not a day; use day names, weekdays or weekends.");
            found.Add(index);
        }

        return string.Join(',', found);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([ t]\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?)?(z|[+-]\d{2}(:?\d{2})?)?$")]
    private static partial Regex Iso();

    [GeneratedRegex(@"([zZ]|[+-]\d{2}(:?\d{2})?)$")]
    private static partial Regex OffsetSuffix();

    [GeneratedRegex(@"^(in )?(?<d>\d+ ?[a-z]+(( and |, | |,)?\d+ ?[a-z]+)*)$")]
    private static partial Regex Delay();

    [GeneratedRegex(@"^every (?<d>.+)$")]
    private static partial Regex Interval();

    [GeneratedRegex(@"(?<n>\d+) ?(?<u>seconds|second|secs|sec|s|minutes|minute|mins|min|m|hours|hour|hrs|hr|h|days|day|d|weeks|week|w)(?![a-z])")]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"^(?<every>(every|each|on) )?(?<days>[a-z, ]*?) ?at (?<time>noon|midnight|\d{1,2}(:\d{2})? ?(am|pm|a\.m\.|p\.m\.)?)$")]
    private static partial Regex Calendar();

    [GeneratedRegex(@"^(?<h>\d{1,2})(:(?<m>\d{2}))? ?(?<ampm>am|pm|a\.m\.|p\.m\.)?$")]
    private static partial Regex Clock();
}
