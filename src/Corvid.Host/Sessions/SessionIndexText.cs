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

    /// <summary>How much of the Board's status line (C-043) the card keeps; a status line is one short line.</summary>
    public const int StatusChars = 200;

    /// <summary>
    /// The card: the title, the first message, the latest answer and the Board's status line while it is current for that
    /// answer (C-043; not after an undo or rollback, which may have taken back what it summed up), each cut, without
    /// control or format characters, and the branch, one per line; blank parts are left out. Only a title event's title counts: an untitled session's title is
    /// its first message's opening, which the card holds already. User, model and tool text: it is indexed and embedded,
    /// never put in a prompt.
    /// </summary>
    /// <param name="description">The session as the conversation still holds it: after an undo or rollback, its first
    /// message and latest answer are the visible ones (<see cref="SessionFinder"/> sees to that).</param>
    /// <param name="branch">The session's worktree branch, or null when it works in a plain folder.</param>
    public static string Of(SessionDescription description, string? branch)
    {
        ArgumentNullException.ThrowIfNull(description);
        var parts = new[]
        {
            description.Titled is { } titled ? SessionCatalogue.Line(titled.Title) : null,
            Cut(description.FirstMessage, FirstMessageChars),
            Cut(description.LastAnswer, LastAnswerChars),
            description.Status is { } status && !description.Rewound && status.ThroughSequence >= description.LastAnswerSequence
                ? Cut(status.Text, StatusChars)
                : null,
            branch,
        };
        return string.Join('\n', parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()).Distinct(StringComparer.Ordinal));
    }

    private static string? Cut(string? text, int length) =>
        text is null ? null : Memory.TextBudget.Prefix(SessionCatalogue.Visible(text).Trim(), length);
}
