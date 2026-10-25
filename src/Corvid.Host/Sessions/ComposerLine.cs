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

    /// <summary>The line's mode, or null for an ordinary message.</summary>
    public static ComposerLine? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = text.Trim();
        // The app's text box breaks lines with \r, a paste may bring \n.
        if (line.Length == 0 || line.AsSpan().IndexOfAny('\r', '\n') >= 0 || line.StartsWith("![", StringComparison.Ordinal))
            return null;
        if (line.StartsWith("!!", StringComparison.Ordinal))
            return ShellOf(line[2..], local: true);
        if (line.StartsWith('!'))
            return ShellOf(line[1..], local: false);
        if (line.StartsWith('#'))
        {
            var fact = line[1..].Trim();
            return fact.Length > 0 ? new Memory(fact) : new Empty("Type the fact to remember after #, for example: # the release branch is main.");
        }
        return null;
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
    /// <param name="snapshot">The session after the line's turn.</param>
    /// <param name="after">The log's head before the line was posted: only the turn that follows it counts (its user
    /// message, then the call that turn issued and its result), so an earlier line's result is never reported as this one's.</param>
    public static (string? Output, int? ExitCode, string? Error) LastShellResult(Corvid.Kernel.Events.SessionSnapshot snapshot, long after = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = snapshot.Transcript.SkipWhile(entry => entry.FromSequence <= after || entry.Content is not Corvid.Abstractions.Events.UserMessage)
            .ToList();
        // This turn: from its user message to the next one.
        var turn = entries.Take(1).Concat(entries.Skip(1).TakeWhile(entry => entry.Content is not Corvid.Abstractions.Events.UserMessage))
            .Select(entry => entry.Content).ToList();
        var issued = turn.OfType<Corvid.Abstractions.Events.ToolCallIssued>()
            .Where(call => call.CallId.StartsWith(Corvid.Kernel.Turns.TurnRunner.UserShellCall, StringComparison.Ordinal))
            .Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
        var result = turn.OfType<Corvid.Abstractions.Events.ToolResult>().LastOrDefault(item => issued.Contains(item.CallId));
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

    private static ComposerLine ShellOf(string rest, bool local)
    {
        var command = rest.Trim();
        return command.Length > 0 ? new Shell(command, local)
            : new Empty($"Type a command after {(local ? "!!" : "!")}, for example: {(local ? "!!" : "!")}git status.");
    }
}
