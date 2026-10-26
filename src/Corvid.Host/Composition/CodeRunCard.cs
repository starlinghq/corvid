using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Host.Sessions;

namespace Corvid.Host.Composition;

/// <summary>
/// A7: what an approval card adds for a code mode run (<c>execute_code</c>), the same on every surface: the language and
/// the run's limits, that each tool the script calls is asked on its own, and the whole source. The source is built from
/// the prompt's in-memory arguments (what runs, not the log's scrubbed copy) and comes back with hidden characters shown as
/// escapes (<see cref="TerminalText.Visible"/>), so the person reads every character they approve; it is never cut.
/// </summary>
public static class CodeRunCard
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>Whether the prompt is for a code mode run.</summary>
    public static bool IsCodeRun(ApprovalPrompt prompt) =>
        prompt?.Action is ToolAction { Tool.Name: CodeRunPresentation.ToolName };

    /// <summary>The run the prompt is for (language and source as the call holds them); null for any other prompt.</summary>
    public static CodeRunView? Run(ApprovalPrompt prompt) =>
        prompt?.Action is ToolAction { Tool.Name: CodeRunPresentation.ToolName } action
            ? CodeRunPresentation.Read(action.Tool.Name, action.Arguments)
            : null;

    /// <summary>
    /// The card's lines for a code run; empty for any other prompt. <paramref name="limits"/> are the run's (the runtime's
    /// <see cref="CorvidRuntime.CodeLimits"/>); none given, the defaults.
    /// </summary>
    public static IReadOnlyList<string> Lines(ApprovalPrompt prompt, Corvid.Tools.CodeMode.CodeModeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!IsCodeRun(prompt))
            return [];
        if (Run(prompt) is not { } run)
            return ["Code run: the call names no script, so nothing would run."];
        var lines = new List<string>
        {
            $"Code run: {TerminalText.Visible(run.DisplayLanguage)}, {run.Lines} line{(run.Lines == 1 ? "" : "s")}; {CodeRunPresentation.LimitsText(limits)}.",
            CodeRunPresentation.EachCallAsked,
        };
        if (TerminalText.HasHidden(run.Source) || run.Source.Contains(LineSeparator, StringComparison.Ordinal) || run.Source.Contains(ParagraphSeparator, StringComparison.Ordinal))
            lines.Add("The source holds hidden characters, shown below as escapes.");
        lines.Add("Source:");
        lines.AddRange(SourceLines(run.Source).Select(line => "  " + line));
        return lines;
    }

    /// <summary>
    /// The source as the terminal shows it: one line per <c>\n</c> (a <c>\r\n</c> counts as one), and every other character
    /// a reader could miss as an escape: controls (<c>\r</c> alone among them), format and variation-selector characters, and
    /// the line and paragraph separators, which some languages read as line breaks and others do not.
    /// </summary>
    public static IReadOnlyList<string> SourceLines(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var shown = TerminalText.Choice(source.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Replace(LineSeparator.ToString(), "\\u2028", StringComparison.Ordinal)
            .Replace(ParagraphSeparator.ToString(), "\\u2029", StringComparison.Ordinal);
        return shown.TrimEnd('\n').Split('\n');
    }

    /// <summary>The card lines a surface's approval prompt adds: a spawn's children (<see cref="DelegationCard"/>) and a code run's.</summary>
    public static async Task<IReadOnlyList<string>> WithDelegationAsync(CorvidRuntime runtime, ApprovalPrompt prompt, CancellationToken cancellationToken,
        bool whole)
    {
        var lines = await DelegationCard.LinesAsync(runtime, prompt, cancellationToken, whole).ConfigureAwait(false);
        var code = Lines(prompt, runtime.CodeLimits);
        return code.Count == 0 ? lines : [.. lines, .. code];
    }
}
