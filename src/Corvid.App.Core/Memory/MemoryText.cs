using System.Globalization;

namespace Corvid.App.Core.Memory;

/// <summary>How a span of the Memory page's mixed text is set: bold, muted (the second text colour), small, monospace.</summary>
[Flags]
public enum MemorySpanStyle
{
    None = 0,
    Bold = 1,
    Muted = 2,
    Small = 4,
    Code = 8,
}

/// <summary>
/// A span of text in one style, for the lines that mix them ("<b>Mark</b> prefers <b>CI failures…</b>", a fact with a
/// path in monospace and its date muted and small). The page draws a list of spans as runs in one wrapping text block.
/// </summary>
public sealed record MemorySpan(string Text, MemorySpanStyle Style = MemorySpanStyle.None)
{
    public bool IsBold => Style.HasFlag(MemorySpanStyle.Bold);

    public bool IsMuted => Style.HasFlag(MemorySpanStyle.Muted);

    public bool IsSmall => Style.HasFlag(MemorySpanStyle.Small);

    public bool IsCode => Style.HasFlag(MemorySpanStyle.Code);
}

/// <summary>The Memory page's wording: dates, clocks, budgets and the spans of facts with <c>`code`</c> in them.</summary>
public static class MemoryText
{
    /// <summary>"2026-10-24", whatever the culture.</summary>
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"09:00" in the zone the clock reads.</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"1,804", with the comma whatever the culture.</summary>
    public static string Count(int value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>A whole percentage: "90%".</summary>
    public static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A similarity score: "0.58".</summary>
    public static string Score(double score) => score.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>When a block was written: "updated 14:02" today, "updated yesterday", "updated 2026-10-20", or "set by you".</summary>
    public static string Updated(CoreBlock block, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(time);
        if (block.SetByUser)
            return "set by you";
        var zone = time.LocalTimeZone;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(block.UpdatedAt, zone).DateTime);
        if (day == today)
            return "updated " + Clock(block.UpdatedAt, zone);
        return day == today.AddDays(-1) ? "updated yesterday" : "updated " + Date(day);
    }

    /// <summary>A block's line under its meter: "local-only · revision 14 · updated 14:02".</summary>
    public static string BlockDetail(CoreBlock block, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(block);
        var parts = new List<string>(3);
        if (block.LocalOnly)
            parts.Add("local-only");
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"revision {block.Revision}"));
        parts.Add(Updated(block, time));
        return string.Join(" · ", parts);
    }

    /// <summary>The validity of a fact: "from 2026-10-16", or "2026-10-19 to 2026-10-24" once it stopped holding.</summary>
    public static string Valid(FactRecord fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return fact.ValidUntil is { } until ? $"{Date(fact.ValidFrom)} to {Date(until)}" : $"from {Date(fact.ValidFrom)}";
    }

    /// <summary>Text with its <c>`code`</c> marks removed, for places that draw it in one font.</summary>
    public static string Plain(string text) => (text ?? "").Replace("`", "", StringComparison.Ordinal);

    /// <summary>Splits text at <c>`code`</c> marks into plain spans in <paramref name="style"/> and code spans.</summary>
    public static IReadOnlyList<MemorySpan> Spans(string text, MemorySpanStyle style = MemorySpanStyle.None)
    {
        var spans = new List<MemorySpan>();
        var parts = (text ?? "").Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;
            // Odd parts sit between a pair of marks; an unpaired mark leaves the rest plain.
            var code = i % 2 == 1 && i < parts.Length - 1;
            spans.Add(new MemorySpan(parts[i], code ? style | MemorySpanStyle.Code : style));
        }

        return spans;
    }
}
