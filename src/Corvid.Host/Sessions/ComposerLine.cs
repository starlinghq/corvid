namespace Corvid.Host.Sessions;

/// <summary>
/// The composer's mode lines (A4, docs/plan/tui.md §4.1, docs/plan/ui.md §4.1), read the same way by the CLI and the app:
/// <c>!command</c> runs a shell command through the approval pipeline, the sandbox and the checkpoints, with its output in
/// the conversation; <c>!!command</c> runs it with the output kept from the model; <c>#fact</c> stages a memory fact
/// through the gated write path, so without a calibrated decider it waits in <c>/memory pending</c>.
/// <para>
/// Only a message of one line is a mode line: text of more than one line (pasted notes under a Markdown heading) and a
/// Markdown image (<c>![</c>) are messages. A slash command is read before this.
/// </para>
/// <para>
/// tui.md §4.1 names the marks but not where a message ends and a mode line begins; the rule here keeps ordinary messages
/// that happen to start with a mark as messages. <c>#</c> is a mode only when a space follows it (<c># the release branch
/// is main</c>), so <c>#123 is failing again</c>, <c>#include &lt;stdio.h&gt; not found</c> and <c>#[derive(Debug)]</c> are
/// messages. <c>!</c> and <c>!!</c> are a mode only when a space or a command word follows: letters, digits and
/// <c>_ . ~ / + -</c> up to a space or the line's end (<c>!git status</c>, <c>! ls -la</c>, <c>!./build.sh</c>), so
/// <c>!important: keep the API stable</c> and <c>!!!</c> are messages. A line starting with <c>\#</c> or <c>\!</c> is
/// always a message, sent without the backslash (<see cref="MessageText"/>).
/// </para>
/// </summary>
public abstract record ComposerLine
{
    private ComposerLine()
    {
    }

    /// <summary><c>!</c> or <c>!!</c> and a command.</summary>
    public sealed record Shell(string Command, bool LocalOnly) : ComposerLine;

    /// <summary><c>#</c> and a fact to remember.</summary>
    public sealed record Memory(string Text) : ComposerLine;

    /// <summary>A mode line with nothing after its mark; <see cref="Message"/> says what to type.</summary>
    public sealed record Empty(string Message) : ComposerLine;

    /// <summary>The line's mode, or null for an ordinary message (sent as <see cref="MessageText"/> gives it).</summary>
    public static ComposerLine? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = OneLine(text);
        if (line is null || line.StartsWith("![", StringComparison.Ordinal))
            return null;
        if (line.StartsWith("!!", StringComparison.Ordinal))
            return ShellOf(line[2..], local: true);
        if (line.StartsWith('!'))
            return ShellOf(line[1..], local: false);
        if (line.StartsWith('#'))
        {
            var rest = line[1..];
            // "#123", "#include" and "#[derive" are messages: the mode needs a space after the mark.
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
                return null;
            var fact = rest.Trim();
            return fact.Length > 0 ? new Memory(fact)
                : new Empty("Type the fact to remember after # and a space, for example: # the release branch is main.");
        }
        return null;
    }

    /// <summary>
    /// The text to send for a message: a one-line <c>\#</c> or <c>\!</c> line without its backslash (the escape that sends a
    /// line starting with a mark as a message), anything else unchanged. Read when the message is posted, never before it
    /// is queued: a queued line is parsed again when its turn starts.
    /// </summary>
    public static string MessageText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return OneLine(text) is { Length: > 1 } line && line[0] == '\\' && line[1] is '#' or '!' ? line[1..] : text;
    }

    // The trimmed line; null when the text is blank or holds more than one line (the app's text box breaks lines with \r,
    // a paste may bring \n).
    private static string? OneLine(string text)
    {
        var line = text.Trim();
        return line.Length == 0 || line.AsSpan().IndexOfAny('\r', '\n') >= 0 ? null : line;
    }

    /// <summary>What a <c>#</c> line did, in a sentence, and whether it went wrong (the CLI's output, the app's notice).</summary>
    /// <param name="withId">Name the staged write's id, which <c>/memory approve</c> takes (the CLI); the app's notice has a review button instead.</param>
    public static (string Text, bool Problem) Describe(Corvid.Memory.MemoryWriteResult result, string fact, bool withId = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        var quoted = "\"" + (SessionCatalogue.Line(fact) ?? fact) + "\"";
        return result.Status switch
        {
            Corvid.Memory.MemoryWriteStatus.Staged => ($"Staged for review: {quoted}" + (withId && result.Pending is { } pending ? $" ({pending.PendingId})" : "")
                + ". /memory pending lists it; the Memory page reviews it.", false),
            Corvid.Memory.MemoryWriteStatus.Applied => ($"Remembered: {quoted} ({result.Operation ?? "written"}, as the decider allowed).", false),
            Corvid.Memory.MemoryWriteStatus.Noop => ($"Already known: {quoted}. Nothing was written.", false),
            _ => ($"Not remembered: {result.Reason ?? "the write was refused"}. Nothing was stored.", true),
        };
    }

    /// <summary>
    /// The latest <c>!</c> line's result in the session: its standard output and error joined (or the refusal's text), its
    /// exit code, null when it did not run, and when the call failed its error kind
    /// (<see cref="Corvid.Kernel.Turns.ToolErrorKinds"/>: <c>not run</c> when it never ran, <c>tool failed</c> when the sandbox
    /// could not run it), null when the command ran (whatever its exit code).
    /// </summary>
    public static (string? Output, int? ExitCode, string? Error) LastShellResult(Corvid.Kernel.Events.SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = snapshot.Transcript.Select(entry => entry.Content).OfType<Corvid.Abstractions.Events.ToolResult>()
            .LastOrDefault(item => item.CallId.StartsWith(Corvid.Kernel.Turns.TurnRunner.UserShellCall, StringComparison.Ordinal));
        if (result is null)
            return (null, null, null);
        var value = result.Result;
        if (result.IsError || value.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            var kind = value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty("error", out var error)
                && error.ValueKind == System.Text.Json.JsonValueKind.String ? error.GetString() : null;
            return (ToolPresentation.ResultText(value), null, kind ?? Corvid.Kernel.Turns.ToolErrorKinds.ToolFailed);
        }
        string Text(string name) => value.TryGetProperty(name, out var part) && part.ValueKind == System.Text.Json.JsonValueKind.String ? part.GetString()! : "";
        var output = string.Join("\n", new[] { Text("stdout"), Text("stderr") }.Where(part => part.Length > 0).Select(part => part.TrimEnd('\n')));
        int? exit = value.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var number) ? number : null;
        return (output, exit, null);
    }

    // A mark alone asks what to type; a mark before a space or a command word is the mode; anything else ("!important:",
    // "!!!") is a message.
    private static ComposerLine? ShellOf(string rest, bool local)
    {
        var command = rest.Trim();
        if (command.Length == 0)
            return new Empty($"Type a command after {(local ? "!!" : "!")}, for example: {(local ? "!!" : "!")}git status.");
        return char.IsWhiteSpace(rest[0]) || IsCommandWord(rest) ? new Shell(command, local) : null;
    }

    // The first word is a command's name or path: letters, digits and _ . ~ / + - up to a space or the end.
    private static bool IsCommandWord(string rest)
    {
        var end = 0;
        for (; end < rest.Length && !char.IsWhiteSpace(rest[end]); end++)
            if (!(char.IsAsciiLetterOrDigit(rest[end]) || rest[end] is '_' or '.' or '~' or '/' or '+' or '-'))
                return false;
        return end > 0;
    }
}
