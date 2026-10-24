using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Palette;

/// <summary>
/// What find-in-session reads of a transcript block: its words and who said them. Every block's text is already cleaned
/// of hidden characters by the block itself; this only chooses which of its fields a person would search for.
/// </summary>
public static class TranscriptText
{
    /// <summary>The searchable text of a block, or empty for blocks that are chrome (chips, headers).</summary>
    public static string Of(TranscriptBlock block) => block switch
    {
        UserMessageBlock user => user.Text,
        AnswerBlock answer => answer.Text,
        ThoughtBlock thought => thought.Text,
        ToolCardBlock tool => string.Join(" ", new[] { tool.ToolName, tool.Target, tool.StatusText, tool.Output }.Where(part => part.Length > 0)),
        NoticeBlock notice => $"{notice.Text} {notice.Detail}".Trim(),
        CommandBlock command => $"{command.Line} {command.Title}".Trim(),
        QuestionBlock question => $"{question.Title} {question.Text} {question.AnswerText}".Trim(),
        SwitchBlock row => row.Text,
        TaintBlock taint => taint.Text,
        DetectionBlock detection => $"{detection.Title} {detection.Reason} {detection.Detail}".Trim(),
        CompactionBlock compaction => compaction.Text,
        RewindBlock rewind => $"{rewind.Text} {rewind.Detail}".Trim(),
        _ => "",
    };

    /// <summary>Who the block is from, for the result's detail line.</summary>
    public static string Who(TranscriptBlock block) => block switch
    {
        UserMessageBlock => "you",
        AnswerBlock => "corvid",
        ThoughtBlock => "thought",
        ToolCardBlock tool => tool.ToolName,
        CommandBlock => "command",
        QuestionBlock => "question",
        _ => "note",
    };

    /// <summary>
    /// One line of context around the first match: whitespace folded, at most <paramref name="width"/> characters, with an
    /// ellipsis where it was cut.
    /// </summary>
    public static string Snippet(string text, string query, int width = 96)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= width)
            return line;
        var at = Math.Max(0, line.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase));
        var start = Math.Clamp(at - width / 3, 0, line.Length - width);
        var cut = line.Substring(start, width);
        return (start > 0 ? "…" : "") + cut + (start + width < line.Length ? "…" : "");
    }
}
