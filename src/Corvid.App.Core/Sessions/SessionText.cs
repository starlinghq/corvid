using System.Globalization;
using System.Text;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// How the session screens word numbers and times, as the mockups print them: "0.3 s" and "12 s", "1.2k tokens",
/// "$0.04", "0.94", "4:12". Invariant culture, so a screenshot is the same on every machine.
/// </summary>
public static class SessionText
{
    /// <summary>A turn's or a thought's time: whole seconds under a minute ("48 s"), then minutes ("2 m 05 s"), hours.</summary>
    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return elapsed switch
        {
            { TotalSeconds: < 60 } => Invariant($"{(int)elapsed.TotalSeconds} s"),
            { TotalMinutes: < 60 } => Invariant($"{(int)elapsed.TotalMinutes} m {elapsed.Seconds:00} s"),
            _ => Invariant($"{(int)elapsed.TotalHours} h {elapsed.Minutes:00} m"),
        };
    }

    /// <summary>A call's time: tenths under ten seconds ("0.3 s", "1.9 s"), then as <see cref="Duration"/> ("12 s").</summary>
    public static string Precise(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return elapsed.TotalSeconds < 9.95
            ? Invariant($"{elapsed.TotalSeconds:0.0} s")
            : Duration(elapsed);
    }

    /// <summary>Token counts: "40", "0.9k", "1.2k", "61k", "200k".</summary>
    public static string Tokens(int tokens)
    {
        if (tokens < 100)
            return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 10_000)
        {
            var thousands = Math.Round(tokens / 1000.0, 1, MidpointRounding.AwayFromZero);
            return Invariant($"{thousands:0.#}k");
        }

        return Invariant($"{Math.Round(tokens / 1000.0, MidpointRounding.AwayFromZero):0}k");
    }

    /// <summary>A cost in dollars: "$0.04"; under a cent, "under $0.01".</summary>
    public static string Cost(decimal cost) => cost is > 0 and < 0.01m ? "under $0.01" : Invariant($"${cost:0.00}");

    /// <summary>A calibrated confidence: "0.94".</summary>
    public static string Confidence(double confidence) => Invariant($"{confidence:0.00}");

    /// <summary>A countdown: "4:12", or "0:00" once it has run out.</summary>
    public static string Countdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        return Invariant($"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>A countdown in words, for a screen reader: "4 minutes 12 seconds".</summary>
    public static string CountdownWords(TimeSpan left)
    {
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        var minutes = seconds / 60;
        seconds %= 60;
        return minutes == 0 ? Plural(seconds, "second") : $"{Plural(minutes, "minute")} {Plural(seconds, "second")}";
    }

    /// <summary>A wall-clock time in the session's zone: "14:01".</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A wall-clock time with seconds: "14:04:12".</summary>
    public static string ClockSeconds(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"1 file", "3 files".</summary>
    public static string Plural(int count, string noun) =>
        Invariant($"{count} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>A rough token estimate for a draft, a quarter of its characters: "about 18 tokens".</summary>
    public static string TokenEstimate(string draft) =>
        draft.Length == 0 ? "" : Invariant($"about {(int)Math.Ceiling(draft.Length / 4.0)} tokens");

    /// <summary>A checkpoint's name as people read it: "pre_execute" is "pre-execute".</summary>
    public static string Checkpoint(string checkpoint) => checkpoint.Replace('_', '-');

    public static string Mode(ApprovalMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>
    /// Text with <c>`code`</c> spans as runs, for the lines the engine writes with code in them (a verdict's rule, a
    /// reason, a note). An unmatched backtick stays as it is.
    /// </summary>
    public static IReadOnlyList<MarkdownRun> Runs(string? text)
    {
        var runs = new List<MarkdownRun>();
        if (string.IsNullOrEmpty(text))
            return runs;
        text = SafeText.Clean(text);
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('`', i);
            var close = open < 0 ? -1 : text.IndexOf('`', open + 1);
            if (open < 0 || close < 0)
            {
                plain.Append(text, i, text.Length - i);
                break;
            }

            plain.Append(text, i, open - i);
            if (plain.Length > 0)
            {
                runs.Add(new MarkdownRun(plain.ToString()));
                plain.Clear();
            }

            if (close > open + 1)
                runs.Add(new MarkdownRun(text[(open + 1)..close], InlineStyle.Code));
            i = close + 1;
        }

        if (plain.Length > 0)
            runs.Add(new MarkdownRun(plain.ToString()));
        return runs;
    }

    /// <summary>The text of runs without their styles, for automation names and copies.</summary>
    public static string Plain(IEnumerable<MarkdownRun> runs) => string.Concat(runs.Select(run => run.Text));

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
