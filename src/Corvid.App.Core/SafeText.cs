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
    /// joiners (U+200C, U+200D) and variation selectors stay where they do their work: Persian, Indic scripts and emoji
    /// need them to read right.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!Needs(text, keepJoiners: true))
            return text;
        var withoutEscapes = text.Contains('\u001b', StringComparison.Ordinal) ? EscapeSequence().Replace(text, "") : text;
        var clean = new StringBuilder(withoutEscapes.Length);
        foreach (var (rune, hidden) in Scan(withoutEscapes, keepJoiners: true))
        {
            if (!hidden)
                clean.Append(rune.ToString());
        }

        return clean.ToString();
    }

    /// <summary>
    /// For text a person approves (a command, a path, a proposed edit): nothing is removed, so what is shown is what will
    /// run. Controls become their visible pictures (␍, ␛, ␇) and every other hidden character a code in brackets
    /// (⟨U+202E⟩, ⟨U+E0041⟩), so it is seen rather than silently dropped. With <paramref name="keepJoiners"/> the joiners
    /// and variation selectors that do their work are left as they are (diff lines of ordinary Persian or emoji text stay
    /// readable); a stray or repeated one is still shown.
    /// </summary>
    public static string Reveal(string? text, bool keepJoiners = false)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!Needs(text, keepJoiners))
            return text;
        var shown = new StringBuilder(text.Length + 8);
        foreach (var (rune, hidden) in Scan(text, keepJoiners))
        {
            var value = rune.Value;
            if (!hidden)
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
        foreach (var (_, hidden) in Scan(text, keepJoiners))
        {
            if (hidden)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Each rune with whether it is hidden. With <paramref name="keepJoiners"/> a joiner or selector is kept only where it
    /// does its work, so a run of them cannot carry a message nobody sees: one variation selector straight after a
    /// character it can change, a joiner between two characters outside ASCII (or after the selector of an emoji it
    /// joins, or after an Indic virama). A selector after a selector or after a character it cannot change, a joiner
    /// at an edge, beside a space, beside ASCII or beside another joiner is hidden.
    /// </summary>
    private static IEnumerable<(Rune Rune, bool Hidden)> Scan(string text, bool keepJoiners)
    {
        if (!keepJoiners)
        {
            foreach (var rune in text.EnumerateRunes())
                yield return (rune, rune.Value is not ('\n' or '\t') && IsHidden(rune, keepJoiners: false));
            yield break;
        }

        var runes = new List<Rune>(text.Length);
        foreach (var rune in text.EnumerateRunes())
            runes.Add(rune);
        var keptSelector = false;
        for (var i = 0; i < runes.Count; i++)
        {
            var value = runes[i].Value;
            bool hidden;
            if (value is '\n' or '\t')
                hidden = false;
            else if (!IsJoinerOrSelector(value))
                hidden = IsHidden(runes[i], keepJoiners: true);
            else if (value is 0x200c or 0x200d)
                hidden = !(i > 0 && ((IsBase(runes[i - 1]) && runes[i - 1].Value > 0x7f) || keptSelector)
                    && ((i + 1 < runes.Count && IsBase(runes[i + 1]) && runes[i + 1].Value > 0x7f) || IsVirama(runes[i - 1].Value)));
            else
                hidden = !(i > 0 && IsBase(runes[i - 1]) && Selects(value, runes[i - 1].Value));
            keptSelector = !hidden && value is not (0x200c or 0x200d) && IsJoinerOrSelector(value);
            yield return (runes[i], hidden);
        }
    }

    // Text and emoji presentation (U+FE0E, U+FE0F) only after a character with both; Mongolian's free selectors after a
    // Mongolian letter; the standardized ones (U+FE00 to U+FE0D) after the Myanmar, mathematical and CJK characters
    // that have variants. The ideographic selectors (U+E0100 on) are always shown: one after each ideograph would carry
    // a byte of hidden text, and ordinary text seldom uses them.
    private static bool Selects(int selector, int previous) => selector switch
    {
        0xfe0e or 0xfe0f => IsEmojiBase(previous),
        >= 0x180b and <= 0x180f => previous is >= 0x1820 and <= 0x18aa,
        >= 0xfe00 and <= 0xfe0d => previous is (>= 0x1000 and <= 0x109f) or (>= 0x2200 and <= 0x22ff) or (>= 0x2a00 and <= 0x2aff)
            or (>= 0x2e80 and <= 0x9fff) or (>= 0xf900 and <= 0xfaff) or (>= 0x20000 and <= 0x3ffff),
        _ => false,
    };

    // Characters that have a text and an emoji presentation: keycap bases, the copyright and registered signs, the symbol
    // blocks and the emoji planes.
    private static bool IsEmojiBase(int value) =>
        value is (>= '0' and <= '9') or '#' or '*' or 0xa9 or 0xae or 0x203c or 0x2049 or 0x3030 or 0x303d or 0x3297 or 0x3299
            or (>= 0x2100 and <= 0x2bff) or (>= 0x1f000 and <= 0x1faff);

    // The Indic viramas: a joiner after one picks a half form or a chillu, also at a word's end.
    private static bool IsVirama(int value) =>
        value is 0x094d or 0x09cd or 0x0a4d or 0x0acd or 0x0b4d or 0x0bcd or 0x0c4d or 0x0ccd or 0x0d4d or 0x0dca;

    // A character a joiner or selector can work on: something drawn, not a space, a control or another joiner.
    private static bool IsBase(Rune rune) =>
        !IsJoinerOrSelector(rune.Value) && !Rune.IsWhiteSpace(rune) && !IsHidden(rune, keepJoiners: true);

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
        return value is 0x034f or 0x115f or 0x1160 or 0x17b4 or 0x17b5 or 0x3164 or 0xffa0
            || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;
    }

    // ZWNJ and ZWJ shape words and emoji; the variation selectors (and Mongolian's free ones) choose a glyph's form.
    private static bool IsJoinerOrSelector(int value) =>
        value is 0x200c or 0x200d or (>= 0x180b and <= 0x180d) or 0x180f or (>= 0xfe00 and <= 0xfe0f) or (>= 0xe0100 and <= 0xe01ef);

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ST), and two-character escapes.
    [GeneratedRegex(@"\u001b(?:\[[0-?]*[ -/]*[@-~]|\][^\u0007\u001b]*(?:\u0007|\u001b\\)?|[@-_])")]
    private static partial Regex EscapeSequence();
}
