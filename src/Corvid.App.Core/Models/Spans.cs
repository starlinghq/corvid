namespace Corvid.App.Core.Models;

/// <summary>
/// A piece of a line the Models page or the first run draws: plain, in the code font (<c>.mono</c>), semibold, or in the
/// second text colour (<c>.muted</c>). The view rebuilds the runs when the theme changes, so a muted run repaints.
/// </summary>
public sealed record TextRun(string Text, bool Code = false, bool Bold = false, bool Muted = false);

/// <summary>
/// Turns a line with <c>`code`</c>, <c>**bold**</c> and <c>~muted~</c> marks into <see cref="TextRun"/>s, for the lines
/// that mix paths and commands into prose ("defaults, no <c>~/.config/corvid/corvid.json</c> yet"). Nothing else is
/// interpreted; the text is data. Inside code, only the closing backtick ends it. A backslash makes the next character
/// literal, so a name or path from outside (a server name, a catalogue entry, a Hermes path) is put into a line through
/// <see cref="Escape"/> and cannot restyle it. The line is cleaned first (<see cref="SafeText.Clean"/>): a value meant to
/// be approved is revealed (<see cref="SafeText.Reveal"/>) before it is escaped, and then nothing is left to clean.
/// </summary>
public static class Spans
{
    public static IReadOnlyList<TextRun> Parse(string? markup)
    {
        var runs = new List<TextRun>();
        if (string.IsNullOrEmpty(markup))
            return runs;
        markup = SafeText.Clean(markup);
        var text = new System.Text.StringBuilder();
        bool code = false, bold = false, muted = false;
        for (var i = 0; i < markup.Length; i++)
        {
            var c = markup[i];
            if (c == '\\' && i + 1 < markup.Length && IsMark(markup[i + 1]))
            {
                text.Append(markup[++i]);
            }
            else if (c == '`')
            {
                Flush(runs, text, code, bold, muted);
                code = !code;
            }
            else if (!code && c == '*' && i + 1 < markup.Length && markup[i + 1] == '*')
            {
                Flush(runs, text, code, bold, muted);
                bold = !bold;
                i++;
            }
            else if (!code && c == '~')
            {
                Flush(runs, text, code, bold, muted);
                muted = !muted;
            }
            else
            {
                text.Append(c);
            }
        }

        Flush(runs, text, code, bold, muted);
        return runs;
    }

    /// <summary>
    /// A value from outside put into a line: its marks (and backslashes) taken literally, so a backtick, a tilde or two
    /// stars in a name draw as themselves rather than restyling what follows.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var escaped = new System.Text.StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (IsMark(c))
                escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>The text without its marks, for screen readers and tooltips.</summary>
    public static string Plain(string? markup) => string.Concat(Parse(markup).Select(run => run.Text));

    private static bool IsMark(char c) => c is '`' or '*' or '~' or '\\';

    private static void Flush(List<TextRun> runs, System.Text.StringBuilder text, bool code, bool bold, bool muted)
    {
        if (text.Length == 0)
            return;
        runs.Add(new TextRun(text.ToString(), code, bold, muted));
        text.Clear();
    }
}
