using System.Globalization;
using System.Text;

namespace Corvid.Host.Commands;

/// <summary>Untrusted text for a terminal: what a model, a tool or a server wrote, with nothing hidden in it.</summary>
public static class TerminalText
{
    /// <summary>
    /// Control and format characters (escape sequences, bidirectional overrides, zero-width characters) written as
    /// <c>\uXXXX</c>, so none can move the cursor, recolour the terminal or hide text; line breaks and tabs stay.
    /// </summary>
    public static string Visible(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t') || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            else
                builder.Append(c);
        }
        return builder.ToString();
    }
}
