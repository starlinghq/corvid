using System.Globalization;
using System.Text;

namespace Corvid.App.Core.Review;

/// <summary>
/// The one message Review's comments go out as (F4): each comment quotes its file, line and the line's text, in the
/// order given (Review passes them by file, then line), so the model reads them as a reviewer wrote them.
/// </summary>
public static class ReviewMessage
{
    public static string Compose(IReadOnlyList<ReviewComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count == 0)
            return "";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Review comments ({comments.Count}):");
        foreach (var comment in comments)
        {
            text.Append("\n\n");
            var side = comment.Side == DiffSide.Old ? " (before the change)" : "";
            text.Append(CultureInfo.InvariantCulture, $"`{comment.Path}` line {comment.Line}{side}:\n");
            var quoted = comment.LineText.Trim();
            if (quoted.Length > 0)
                text.Append("> ").Append(quoted).Append('\n');
            text.Append(comment.Text.Trim());
        }

        return text.ToString();
    }
}
