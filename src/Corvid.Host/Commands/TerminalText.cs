using System.Globalization;
using System.Text;

namespace Corvid.Host.Commands;

/// <summary>Untrusted text for a terminal: what a model, a tool or a server wrote, with nothing hidden in it.</summary>
public static class TerminalText
{
    /// <summary>
    /// Control and format characters (escape sequences, bidirectional overrides, zero-width characters, the tag
    /// characters outside the basic plane) written as <c>\uXXXX</c>, so none can move the cursor, recolour the terminal or
    /// hide text; line breaks and tabs stay. Read by Unicode scalar, so a character beyond U+FFFF is one code
    /// (<c>1</c>), never two halves.
    /// </summary>
    public static string Visible(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsControlOrFormat(rune))
                builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
            else
                builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>
    /// A choice the model offered for the person to pick (K-6's <c>ask_user</c> options), as shown: <see cref="Visible"/>,
    /// and the variation selectors too (<c>️</c>, <c>0</c>), which can carry text nobody sees. What is shown
    /// is everything the choice holds.
    /// </summary>
    public static string Choice(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHidden(rune))
                builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
            else
                builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>
    /// A choice the model offered, as the person's answer: only what a reader sees, with the control, format and
    /// variation-selector characters (the ones <see cref="Choice"/> shows as codes) taken out and the ends trimmed. Picking
    /// a choice never sends, as the person's words, characters they could not see. Empty when nothing visible is left.
    /// </summary>
    public static string ChosenAnswer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsHidden(rune))
                builder.Append(rune.ToString());
        }
        return builder.ToString().Trim();
    }

    /// <summary>Whether the text holds a character <see cref="Choice"/> would show as a code.</summary>
    public static bool HasHidden(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHidden(rune))
                return true;
        }
        return false;
    }

    private static bool IsControlOrFormat(Rune rune) =>
        rune.Value is not ('\n' or '\t') && (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format);

    private static bool IsHidden(Rune rune) =>
        IsControlOrFormat(rune) || rune.Value is (>= 0xfe00 and <= 0xfe0f) or (>= 0x180b and <= 0x180f) or (>= 0xe0100 and <= 0xe01ef);
}
