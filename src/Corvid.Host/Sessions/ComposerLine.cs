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
    /// The latest <c>!</c> line's result in the session: its standard output and error joined (or the refusal's text) and
    /// its exit code, null when it did not run.
    /// </summary>
    public static (string? Output, int? ExitCode) LastShellResult(Corvid.Kernel.Events.SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = snapshot.Transcript.Select(entry => entry.Content).OfType<Corvid.Abstractions.Events.ToolResult>()
            .LastOrDefault(item => item.CallId.StartsWith("user-shell", StringComparison.Ordinal));
        if (result is null)
            return (null, null);
        var value = result.Result;
        if (result.IsError || value.ValueKind != System.Text.Json.JsonValueKind.Object)
            return (ToolPresentation.ResultText(value), null);
        string Text(string name) => value.TryGetProperty(name, out var part) && part.ValueKind == System.Text.Json.JsonValueKind.String ? part.GetString()! : "";
        var output = string.Join("\n", new[] { Text("stdout"), Text("stderr") }.Where(part => part.Length > 0).Select(part => part.TrimEnd('\n')));
        int? exit = value.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var number) ? number : null;
        return (output, exit);
    }

    private static ComposerLine ShellOf(string rest, bool local)
    {
        var command = rest.Trim();
        return command.Length > 0 ? new Shell(command, local)
            : new Empty($"Type a command after {(local ? "!!" : "!")}, for example: {(local ? "!!" : "!")}git status.");
    }
}
