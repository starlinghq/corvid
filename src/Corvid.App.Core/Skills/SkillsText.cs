using System.Globalization;

namespace Corvid.App.Core.Skills;

/// <summary>The Skills page's wording: times, revisions, authors and the parts of a SKILL.md.</summary>
public static class SkillsText
{
    /// <summary>A revision as the page shows it: the first seven hex digits, as git does.</summary>
    public static string Short(string? revision) =>
        string.IsNullOrEmpty(revision) ? "" : revision.Length > 7 ? revision[..7] : revision;

    /// <summary>"13:41" in the clock's zone.</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"2026-10-20", whatever the culture.</summary>
    public static string Date(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"14:03" today, "yesterday", or the date; "never" for a skill not used yet.</summary>
    public static string When(DateTimeOffset? at, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (at is not { } value)
            return "never";
        var zone = time.LocalTimeZone;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        if (day == today)
            return Clock(value, zone);
        return day == today.AddDays(-1) ? "yesterday" : Date(value, zone);
    }

    /// <summary>"2026-10-23 16:40": a commit's or an eval run's time.</summary>
    public static string Stamp(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Whole days since the last use (or since never: <see cref="int.MaxValue"/>).</summary>
    public static int IdleDays(DateTimeOffset? lastUsed, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (lastUsed is not { } value)
            return int.MaxValue;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, time.LocalTimeZone).DateTime);
        return today.DayNumber - day.DayNumber;
    }

    /// <summary>"1 use", "12 uses".</summary>
    public static string Uses(int uses) => uses == 1 ? "1 use" : uses.ToString("#,0", CultureInfo.InvariantCulture) + " uses";

    /// <summary>The colour an author's tag takes: the agent violet, the user grey, a hub amber (outside content).</summary>
    public static Tone AuthorTone(string? author) => author switch
    {
        "agent" => Tone.Violet,
        "user" => Tone.Grey,
        _ => Tone.Amber,
    };

    /// <summary>What an author means, for the tag's tooltip and the detail panel.</summary>
    public static string AuthorMeaning(string? author) => author switch
    {
        "agent" => "the learning loop and the curator may change or archive it",
        "user" => "yours: the agent never changes it",
        null or "" => "not committed yet",
        _ => "from a hub: outside content, so loading it taints the session",
    };

    /// <summary>The body of a SKILL.md, below its frontmatter, for rendering.</summary>
    public static string Body(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return "";
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return text;
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return text;
        var after = text.IndexOf('\n', end + 4);
        return after < 0 ? "" : text[(after + 1)..].TrimStart('\n');
    }

    /// <summary>"3/3", the passed of total of the latest run, or an em dash when none ran.</summary>
    public static string Score(EvalScore? score) =>
        score is { } s ? string.Create(CultureInfo.InvariantCulture, $"{s.Passed}/{s.Total}") : "—";

    /// <summary>The tone of an eval score: green when every case passed, amber when one failed, grey with none.</summary>
    public static Tone ScoreTone(EvalScore? score) => score switch
    {
        null => Tone.Grey,
        { AllPassed: true } => Tone.Green,
        _ => Tone.Amber,
    };

    /// <summary>A tool list as the replay table shows it: "read_file, grep".</summary>
    public static string Tools(IEnumerable<string> tools) => string.Join(", ", tools.Select(t => SafeText.Clean(t)));

    /// <summary>The frontmatter's description line, as the library lists it (outside text: clean it to show it).</summary>
    public static string Description(string? content)
    {
        foreach (var line in (content ?? "").Split('\n'))
        {
            if (line.StartsWith("description:", StringComparison.Ordinal))
                return line["description:".Length..].Trim();
        }

        return "";
    }

    /// <summary>
    /// The frontmatter's <c>name:</c>, unquoted as the engine's parser reads a quoted scalar (<c>name: "tidy"</c>), or null
    /// when the frontmatter has none (or there is no frontmatter).
    /// </summary>
    public static string? Name(string? content)
    {
        var text = Lines(content);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return null;
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        foreach (var line in (end < 0 ? text[4..] : text[4..end]).Split('\n'))
        {
            if (!line.StartsWith("name:", StringComparison.Ordinal))
                continue;
            var value = line["name:".Length..].Trim();
            return value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;
        }

        return null;
    }

    /// <summary>
    /// Line breaks as one <c>\n</c>: a text box hands back <c>\r\n</c> or a bare <c>\r</c> where the person pressed Enter,
    /// and neither may count as a hidden character or as a change.
    /// </summary>
    public static string Lines(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// Whether a SKILL.md holds a character a reader cannot see: what <see cref="SafeText.Reveal"/> would mark with the
    /// joiners and variation selectors left alone, as the draft and its diff show them (ordinary Persian, Indic or emoji
    /// text reads right only with them). Such a SKILL.md is not written.
    /// </summary>
    public static bool HasHiddenContent(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(SafeText.Reveal(text, keepJoiners: true), text, StringComparison.Ordinal);

    /// <summary>
    /// What <see cref="SafeText.Reveal"/> writes in place of a hidden character: a <c>⟨U+…⟩</c> code or a control's
    /// picture (␍, ␛, ␡). An editor opened on a draft that hid characters shows these until they are deleted.
    /// </summary>
    public static bool HasRevealedMarks(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.Contains("⟨U+", StringComparison.Ordinal))
            return true;
        foreach (var c in text)
        {
            if (c is >= '␀' and <= '␡')
                return true;
        }

        return false;
    }
}
