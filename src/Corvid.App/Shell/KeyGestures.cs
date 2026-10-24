using Corvid.App.Core.Keys;
using Windows.System;

namespace Corvid.App.Shell;

/// <summary>
/// Turns the key map's gestures ("Ctrl+K", "Ctrl+;", "?") into the platform's key codes for a keyboard accelerator. The
/// punctuation keys are the US layout's virtual keys (VK_OEM_*), as Uno's desktop host reports them; "?" is Shift and
/// the slash key.
/// </summary>
public static class KeyGestures
{
    public static bool TryMap(KeyGesture gesture, out VirtualKey key, out VirtualKeyModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        modifiers = VirtualKeyModifiers.None;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Ctrl))
            modifiers |= VirtualKeyModifiers.Control;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Shift))
            modifiers |= VirtualKeyModifiers.Shift;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Alt))
            modifiers |= VirtualKeyModifiers.Menu;

        var name = gesture.Key;
        key = VirtualKey.None;
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0]))
            key = VirtualKey.A + (name[0] - 'A');
        else if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            key = VirtualKey.Number0 + (name[0] - '0');
        else if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var number))
            key = VirtualKey.F1 + (number - 1);
        else
        {
            (key, var shift) = name switch
            {
                "Enter" => (VirtualKey.Enter, false),
                "Esc" => (VirtualKey.Escape, false),
                "Tab" => (VirtualKey.Tab, false),
                "Space" => (VirtualKey.Space, false),
                "Up" => (VirtualKey.Up, false),
                "Down" => (VirtualKey.Down, false),
                "Left" => (VirtualKey.Left, false),
                "Right" => (VirtualKey.Right, false),
                "Home" => (VirtualKey.Home, false),
                "End" => (VirtualKey.End, false),
                "PageUp" => (VirtualKey.PageUp, false),
                "PageDown" => (VirtualKey.PageDown, false),
                "Backspace" => (VirtualKey.Back, false),
                "Delete" => (VirtualKey.Delete, false),
                ";" => ((VirtualKey)186, false),
                "=" => ((VirtualKey)187, false),
                "+" => ((VirtualKey)187, true),
                "," => ((VirtualKey)188, false),
                "-" => ((VirtualKey)189, false),
                "." => ((VirtualKey)190, false),
                "/" => ((VirtualKey)191, false),
                "?" => ((VirtualKey)191, true),
                "`" => ((VirtualKey)192, false),
                "[" => ((VirtualKey)219, false),
                "]" => ((VirtualKey)221, false),
                "'" => ((VirtualKey)222, false),
                _ => (VirtualKey.None, false),
            };
            if (shift)
                modifiers |= VirtualKeyModifiers.Shift;
        }

        return key != VirtualKey.None;
    }
}
