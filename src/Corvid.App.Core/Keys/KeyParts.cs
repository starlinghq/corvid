using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Keys;

/// <summary>
/// A key's text as key caps for a hint line: "Ctrl+Shift+F" is three caps; "Esc Esc" two; "Ctrl+1 to Ctrl+7" two
/// gestures with "to" between them in plain text; "Up, Down" two caps with the comma as text.
/// </summary>
public static class KeyParts
{
    public static IReadOnlyList<HintPart> For(string? keyText)
    {
        var text = (keyText ?? "").Trim();
        if (text.Length == 0)
            return [];
        var parts = new List<HintPart>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            // "Up," is Up and a comma; "Ctrl+," is the comma key.
            var comma = word.Length > 1 && word.EndsWith(',') && !word.EndsWith("+,", StringComparison.Ordinal);
            if (comma)
                word = word[..^1];
            if (word is "to" or "or" or "then")
            {
                parts.Add(new HintPart($" {word} ", false));
                continue;
            }

            if (parts.Count > 0 && parts[^1].IsKey)
                parts.Add(new HintPart(" ", false));
            var caps = KeyGesture.TryParse(word, out var gesture, out _) ? gesture.Caps : SplitPlus(word);
            for (var c = 0; c < caps.Count; c++)
            {
                if (c > 0)
                    parts.Add(new HintPart(" ", false));
                parts.Add(new HintPart(caps[c], true));
            }

            if (comma)
                parts.Add(new HintPart(", ", false));
        }

        return parts;
    }

    private static IReadOnlyList<string> SplitPlus(string word) =>
        word.Length > 1 && word.Contains('+', StringComparison.Ordinal) && !word.EndsWith('+') ? word.Split('+') : [word];
}
