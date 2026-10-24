using System.Text;

namespace Corvid.App.Core.Keys;

/// <summary>The modifier keys of a gesture.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// One key with its modifiers, as <c>keybindings.json</c> writes it (docs/plan/tui.md §4.7): "Ctrl+K", "Ctrl+Shift+F",
/// "Alt+P", "F1", "?", "Ctrl+;". The key is a name the head maps to its own key codes: a letter, a digit, F1 to F12, or one
/// of <see cref="NamedKeys"/>. Parsing is forgiving about case, spaces and the order of modifiers; <see cref="ToString"/>
/// always writes Ctrl, Shift, Alt, then the key, so two spellings of one gesture compare equal.
/// </summary>
public sealed record KeyGesture(KeyModifiers Modifiers, string Key)
{
    /// <summary>The keys that are not a letter, a digit or a function key.</summary>
    public static IReadOnlyList<string> NamedKeys { get; } =
        ["Enter", "Esc", "Tab", "Space", "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown", "Backspace",
         "Delete", ";", ",", ".", "/", "?", "'", "[", "]", "`", "-", "="];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["return"] = "Enter", ["escape"] = "Esc", ["semicolon"] = ";", ["comma"] = ",", ["period"] = ".", ["slash"] = "/",
        ["question"] = "?", ["del"] = "Delete", ["pgup"] = "PageUp", ["pgdn"] = "PageDown", ["↵"] = "Enter",
    };

    public override string ToString()
    {
        var text = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl))
            text.Append("Ctrl+");
        if (Modifiers.HasFlag(KeyModifiers.Shift))
            text.Append("Shift+");
        if (Modifiers.HasFlag(KeyModifiers.Alt))
            text.Append("Alt+");
        return text.Append(Key).ToString();
    }

    /// <summary>The gesture as key caps: "Ctrl", "Shift", "F".</summary>
    public IReadOnlyList<string> Caps => [.. SplitCaps(ToString())];

    /// <summary>
    /// Reads a gesture; false with a sentence saying why when the text is not one. A bare letter or digit with no Ctrl or
    /// Alt is refused, since it would fire while a person types (only "?" and the function keys stand alone).
    /// </summary>
    public static bool TryParse(string? text, out KeyGesture gesture, out string problem)
    {
        gesture = new KeyGesture(KeyModifiers.None, "");
        problem = "";
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            problem = "Type a key, such as Ctrl+K.";
            return false;
        }

        // "Ctrl++" names the plus key; anything else splits on '+'.
        var parts = trimmed.EndsWith("++", StringComparison.Ordinal)
            ? [.. trimmed[..^2].Split('+', StringSplitOptions.TrimEntries), "+"]
            : trimmed.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "cmd" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" or "option" or "meta" => KeyModifiers.Alt,
                _ => KeyModifiers.None,
            };
            if (modifier == KeyModifiers.None)
            {
                problem = $"\"{SafeText.Clean(part)}\" is not Ctrl, Shift or Alt.";
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryKey(parts[^1], out var key))
        {
            problem = parts[^1].Length == 0
                ? "The gesture ends in '+' with no key after it."
                : $"\"{SafeText.Clean(parts[^1])}\" is not a key: use a letter, a digit, F1 to F12, or one of {string.Join(" ", NamedKeys)}.";
            return false;
        }

        var standsAlone = key == "?" || key.Length > 1 && key[0] == 'F' && char.IsAsciiDigit(key[1]);
        if ((modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) == 0 && !standsAlone)
        {
            problem = $"{new KeyGesture(modifiers, key)} would fire while you type; add Ctrl or Alt.";
            return false;
        }

        gesture = new KeyGesture(modifiers, key);
        return true;
    }

    public static KeyGesture Parse(string text) =>
        TryParse(text, out var gesture, out var problem) ? gesture : throw new FormatException(problem);

    private static bool TryKey(string text, out string key)
    {
        key = "";
        if (text.Length == 0)
            return false;
        if (Aliases.TryGetValue(text, out var alias))
        {
            key = alias;
            return true;
        }

        if (text.Length == 1 && char.IsAsciiLetterOrDigit(text[0]))
        {
            key = text.ToUpperInvariant();
            return true;
        }

        if (text.Length is 2 or 3 && (text[0] is 'F' or 'f') && int.TryParse(text.AsSpan(1), out var number) && number is >= 1 and <= 12 && text[1] != '0')
        {
            key = $"F{number}";
            return true;
        }

        if (text == "+")
        {
            key = "+";
            return true;
        }

        var named = NamedKeys.FirstOrDefault(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
        if (named is null)
            return false;
        key = named;
        return true;
    }

    private static IEnumerable<string> SplitCaps(string text)
    {
        if (text.EndsWith("++", StringComparison.Ordinal))
            return [.. text[..^2].Split('+'), "+"];
        return text.Split('+');
    }
}
