using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Corvid.App.Core;

/// <summary>
/// Text from the model, a tool, an MCP server or a file name is data (tui.md §3.7, docs/plan/ui.md §8). Before the app
/// draws it, terminal escape sequences, control characters and the invisible characters that can make text read
/// differently from what it is (bidirectional overrides, zero-width and format characters: the "Trojan source" set) are
/// taken out (<see cref="Clean"/>), or, where a person approves the text, shown (<see cref="Reveal"/>). Line breaks and
/// tabs stay. The text is read by Unicode scalar (rune), so characters outside the basic plane, such as the tag
/// characters, are caught too.
/// </summary>
public static partial class SafeText
{
    /// <summary>
    /// The text with escapes and invisible characters removed; the same instance when there was nothing to remove. The
    /// joiners (U+200C, U+200D) and variation selectors stay: Persian, Indic scripts and emoji need them to read right.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!Needs(text, keepJoiners: true))
            return text;
        var withoutEscapes = text.Contains('\u001b', StringComparison.Ordinal) ? EscapeSequence().Replace(text, "") : text;
        var clean = new StringBuilder(withoutEscapes.Length);
        foreach (var rune in withoutEscapes.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t' || !IsHidden(rune, keepJoiners: true))
                clean.Append(rune.ToString());
        }

        return clean.ToString();
    }

    /// <summary>
    /// For text a person approves (a command, a path, a proposed edit): nothing is removed, so what is shown is what will
    /// run. Controls become their visible pictures (␍, ␛, ␇) and every other hidden character a code in brackets
    /// (⟨U+202E⟩, ⟨U+E0041⟩), so it is seen rather than silently dropped. With <paramref name="keepJoiners"/> the joiners
    /// and variation selectors are left as they are (diff lines of ordinary Persian or emoji text stay readable).
    /// </summary>
    public static string Reveal(string? text, bool keepJoiners = false)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!Needs(text, keepJoiners))
            return text;
        var shown = new StringBuilder(text.Length + 8);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is '\n' or '\t' || !IsHidden(rune, keepJoiners))
                shown.Append(rune.ToString());
            else if (value < 0x20)
                shown.Append((char)(0x2400 + value));
            else if (value == 0x7f)
                shown.Append('␡');
            else
                shown.Append(CultureInfo.InvariantCulture, $"⟨U+{value:X4}⟩");
        }

        return shown.ToString();
    }

    /// <summary>Whether <see cref="Reveal"/> would change the text: it holds something a reader cannot see.</summary>
    public static bool HasHidden(string? text) => !string.IsNullOrEmpty(text) && Needs(text, keepJoiners: false);

    private static bool Needs(string text, bool keepJoiners)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is not ('\n' or '\t') && IsHidden(rune, keepJoiners))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Characters that draw as nothing, or move other text: C0 and C1 controls (but line feed and tab), DEL, every format
    /// character (bidirectional marks, embeddings, overrides and isolates, zero-width space and joiners, word joiner,
    /// soft hyphen, the invisible operators, the interlinear annotation marks, the byte-order mark, the tag characters),
    /// the combining grapheme joiner, the Hangul fillers and the variation selectors.
    /// </summary>
    private static bool IsHidden(Rune rune, bool keepJoiners)
    {
        var value = rune.Value;
        if (value < 0x20 || value is 0x7f or (>= 0x80 and <= 0x9f))
            return true;
        if (IsJoinerOrSelector(value))
            return !keepJoiners;
        return value is 0x034f or 0x115f or 0x1160 or 0x3164 or 0xffa0
            || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;
    }

    // ZWNJ and ZWJ shape words and emoji; the variation selectors choose a glyph's presentation.
    private static bool IsJoinerOrSelector(int value) =>
        value is 0x200c or 0x200d or (>= 0xfe00 and <= 0xfe0f) or (>= 0xe0100 and <= 0xe01ef);

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ST), and two-character escapes.
    [GeneratedRegex(@"\u001b(?:\[[0-?]*[ -/]*[@-~]|\][^\u0007\u001b]*(?:\u0007|\u001b\\)?|[@-_])")]
    private static partial Regex EscapeSequence();
}
