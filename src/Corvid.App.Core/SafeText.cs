using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Corvid.App.Core;

/// <summary>
/// Text from the model, a tool, an MCP server or a file name is data (tui.md §3.7, docs/plan/ui.md §8). Before the app
/// draws it, terminal escape sequences, control characters and the bidirectional overrides that can make text read
/// differently from what it is (the "Trojan source" characters) are taken out; line breaks and tabs stay.
/// </summary>
public static partial class SafeText
{
    /// <summary>The text with escapes and invisible controls removed; the same instance when there was nothing to remove.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!NeedsCleaning(text))
            return text;
        var withoutEscapes = text.Contains('\u001b', StringComparison.Ordinal) ? EscapeSequence().Replace(text, "") : text;
        var clean = new StringBuilder(withoutEscapes.Length);
        foreach (var c in withoutEscapes)
        {
            if (c is '\n' or '\t')
                clean.Append(c);
            else if (c == '\r')
                continue;
            else if (!IsInvisibleControl(c))
                clean.Append(c);
        }

        return clean.ToString();
    }

    /// <summary>
    /// For text a person approves (a command, a path, a proposed edit): nothing is removed, so what is shown is what will
    /// run. Controls become their visible pictures (␍, ␛, ␇), and bidirectional and zero-width marks become a code in
    /// brackets (⟨U+202E⟩), so a hidden character is seen rather than silently dropped. Line breaks and tabs stay.
    /// </summary>
    public static string Reveal(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!NeedsCleaning(text))
            return text;
        var shown = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\n' or '\t')
                shown.Append(c);
            else if (c < ' ')
                shown.Append((char)('␀' + c));
            else if (c == '\u007f')
                shown.Append('␡');
            else if (IsInvisibleControl(c))
                shown.Append(CultureInfo.InvariantCulture, $"⟨U+{(int)c:X4}⟩");
            else
                shown.Append(c);
        }

        return shown.ToString();
    }

    private static bool NeedsCleaning(string text)
    {
        foreach (var c in text)
        {
            if (c is '\n' or '\t')
                continue;
            if (c == '\r' || IsInvisibleControl(c))
                return true;
        }

        return false;
    }

    // C0 and C1 controls, the bidirectional embeddings, overrides and isolates, the marks that steer them, and the
    // zero-width characters (space, joiners, word joiner, byte-order mark) that hide inside a word.
    private static bool IsInvisibleControl(char c) =>
        c < ' ' || c is '\u007f' || c is >= '\u0080' and <= '\u009f'
        || c is >= '‪' and <= '‮' || c is >= '⁦' and <= '⁩' || c is '‎' or '‏' or '؜'
        || c is >= '​' and <= '‍' || c is '⁠' or '﻿';

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ST), and two-character escapes.
    [GeneratedRegex(@"\u001b(?:\[[0-?]*[ -/]*[@-~]|\][^\u0007\u001b]*(?:\u0007|\u001b\\)?|[@-_])")]
    private static partial Regex EscapeSequence();
}
