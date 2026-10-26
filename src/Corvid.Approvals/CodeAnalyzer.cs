namespace Corvid.Approvals;

/// <summary>
/// A7: describes a code tool's script (<see cref="CodeAction"/>). Nothing is mined from the source: a script names paths,
/// URLs and commands in strings, comments and computed values alike, and reading them as targets would claim knowledge
/// Corvid does not have. The analysis has kind <c>code</c>, the source as its text (what a card shows), one command with no
/// targets and one unresolved reason, so the action always asks and offers "once" only: no remembered rule and no
/// <c>risk</c> answer runs a script unasked. The source is still scanned by the hardline backstop (Vault's directory name,
/// a disk formatter), as an interpreter's inline code is.
/// </summary>
internal static class CodeAnalyzer
{
    public const string Kind = "code";

    public static ActionAnalysis Analyze(CodeAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var command = new AnalyzedCommand(action.Tool, action.Language, ActionEffects.Execute | ActionEffects.Write, [],
            [$"'{action.Tool}' runs a {action.Language} script whose effects are known only as it runs (its tool calls are each approved as they are made)"],
            [], false, action.Code, [action.Tool, action.Language]);
        return new ActionAnalysis(Kind, action.Code, [command], null) { OpaqueScripts = [action.Code] };
    }
}
