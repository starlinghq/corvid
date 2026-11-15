namespace Corvid.Host.Sessions;

/// <summary>
/// The text a session is found by in the session index (C-044): what it is about, in a few hundred characters, so "the
/// sandbox flake one" finds the session rather than one turn of it. Built from the session's description (the log, read
/// once per head) and the branch of its worktree, whose name often says what the work is (<c>corvid/sandbox-flake</c>).
/// </summary>
public static class SessionIndexText
{
    /// <summary>How much of the first message (the task, usually) the card keeps.</summary>
    public const int FirstMessageChars = 400;

    /// <summary>How much of the latest answer (where the work is now) the card keeps.</summary>
    public const int LastAnswerChars = 600;

    /// <summary>
    /// The card: the title, the first message and the latest answer (each cut, without control or format characters) and
    /// the branch, one per line; blank parts are left out. User, model and tool text: it is indexed and embedded, never put
    /// in a prompt.
    /// </summary>
    /// <param name="branch">The session's worktree branch, or null when it works in a plain folder.</param>
    public static string Of(SessionDescription description, string? branch)
    {
        ArgumentNullException.ThrowIfNull(description);
        var parts = new[]
        {
            description.Title,
            Cut(description.FirstMessage, FirstMessageChars),
            Cut(description.LastAnswer, LastAnswerChars),
            branch,
        };
        return string.Join('\n', parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()).Distinct(StringComparer.Ordinal));
    }

    private static string? Cut(string? text, int length) =>
        text is null ? null : Memory.TextBudget.Prefix(SessionCatalogue.Visible(text).Trim(), length);
}
