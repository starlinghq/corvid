using System.Globalization;
using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Sessions;

/// <summary>
/// A session as every list shows it (the CLI's <c>/sessions</c>, ACP's <c>session/list</c>, the app's rail and Board),
/// read from the log in one pass: its title, when it last changed, how many turns it holds and a line of its latest answer.
/// </summary>
/// <param name="Head">The log's head for the session when it was read.</param>
public sealed record SessionDescription(string Id, long Head)
{
    /// <summary>The latest <c>SessionTitled</c> (G-6): generated after the first exchange, or the user's rename; null when there is none.</summary>
    public SessionTitled? Titled { get; init; }

    /// <summary>The first message the user sent, as typed (local-only <c>!!</c> lines left out).</summary>
    public string? FirstMessage { get; init; }

    /// <summary>The latest answer.</summary>
    public string? LastAnswer { get; init; }

    /// <summary>Messages the user sent, each a turn.</summary>
    public int Turns { get; init; }

    public DateTimeOffset LastActivity { get; init; }

    /// <summary>The latest recap (G-6), or null.</summary>
    public SessionRecapped? Recap { get; init; }

    /// <summary>
    /// The title a list shows: the title event's, else the first message's first line, cut to <see cref="SessionCatalogue.TitleLength"/>;
    /// null for a session with neither. Model and user text: a surface cleans it of hidden characters before showing it.
    /// </summary>
    public string? Title => Titled is { } titled ? SessionCatalogue.Line(titled.Title) : SessionCatalogue.Line(FirstMessage);

    /// <summary>The latest answer's first line, cut like a title.</summary>
    public string? Preview => SessionCatalogue.Line(LastAnswer);
}

/// <summary>Reads sessions for lists (G-6): one shared description, so every surface titles a session the same way.</summary>
public static class SessionCatalogue
{
    /// <summary>The longest title a list shows; longer ones end in an ellipsis.</summary>
    public const int TitleLength = 80;

    /// <summary>The session's description, or null when the log holds nothing for it.</summary>
    public static SessionDescription? Describe(IEventLog log, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var head = log.GetHead(sessionId, cancellationToken);
        if (head == 0)
            return null;
        SessionTitled? titled = null;
        SessionRecapped? recap = null;
        string? first = null, answer = null;
        var turns = 0;
        var last = default(DateTimeOffset);
        foreach (var item in SessionProjection.ReadRange(log, sessionId, 0, head, cancellationToken))
        {
            // A rename or a recap is about the session, not activity in it: the list's "time since activity" stays.
            if (item.Payload is not (SessionTitled or SessionRecapped) || last == default)
                last = item.RecordedAt;
            switch (item.Payload)
            {
                case UserMessage user:
                    turns++;
                    if (user.LocalOnly != true)
                        first ??= user.Text;
                    break;
                case AssistantMessage assistant:
                    answer = assistant.Text;
                    break;
                case SessionTitled value:
                    titled = value;
                    break;
                case SessionRecapped value:
                    recap = value;
                    break;
            }
        }
        return new SessionDescription(sessionId, head)
        {
            Titled = titled,
            FirstMessage = first,
            LastAnswer = answer,
            Turns = turns,
            LastActivity = last,
            Recap = recap,
        };
    }

    /// <summary>
    /// The text's lines joined into one, without control or format characters (so nothing in a title is invisible in a
    /// terminal list), trimmed and cut to <see cref="TitleLength"/>; null for null or blank text.
    /// </summary>
    public static string? Line(string? text)
    {
        if (text is null)
            return null;
        var line = string.Join(' ', Visible(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length == 0)
            return null;
        return line.Length > TitleLength ? line[..(TitleLength - 1)] + "…" : line;
    }

    /// <summary>
    /// Model or user text for a terminal: line breaks as <c>\n</c>, and no control or format character, so nothing in it is
    /// invisible or moves the cursor. The app cleans text its own way (SafeText), revealing rather than dropping.
    /// </summary>
    public static string Visible(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var visible = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            if (c == '\n' || char.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format))
                visible.Append(c);
        }
        return visible.ToString();
    }
}

/// <summary>Settings for <see cref="SessionTitler"/>.</summary>
public sealed record SessionTitlerOptions
{
    /// <summary>Write a title with the local generator after a session's first exchange (K-9). Renames and recaps work either way.</summary>
    public bool Generate { get; init; } = true;

    /// <summary>How long a person must have been away from a session for a surface to recap it on return; zero turns that off.</summary>
    public TimeSpan RecapAfter { get; init; } = TimeSpan.FromHours(1);

    public int TitleTokens { get; init; } = 96;

    public int RecapTokens { get; init; } = 384;

    /// <summary>The most event lines a recap prompt holds: the first and last of the range, with a count of the rest.</summary>
    public int RecapLines { get; init; } = 60;
}

/// <summary>
/// Session titles and recaps (G-6, extending K-9; docs/plan/ui.md §4.5, §4.6). A title is written by the local generator
/// after the session's first exchange, from its first message and answer, and a person can rename a session at any time;
/// both are <c>SessionTitled</c> events, and a generated title is appended only while the session has none, so it never
/// replaces a rename. A recap is a few lines about the events since the last one, written on request (<c>/recap</c>, the
/// app's Recap) or by a surface when a person comes back after <see cref="SessionTitlerOptions.RecapAfter"/>.
/// <para>
/// Only a local generator writes them: titles and recaps leave the machine never (docs/plan/tui.md §4.5), so with a cloud
/// default generator nothing is generated and <see cref="Unavailable"/> says why. The generator runs at background
/// priority. What it writes is model output: lists clean it of hidden characters before showing it, the log scrubs
/// credentials from it, and it never reaches a prompt. Local-only <c>!!</c> lines are left out of what it reads.
/// </para>
/// </summary>
public sealed class SessionTitler
{
    private const int MaxAppendAttempts = 8;
    private const int LineChars = 300;

    private const string TitlePrompt =
        "You name conversations for a list. Reply with a title of at most eight words for the conversation below, in the "
        + "language it is written in: no quotes, no punctuation at the end, nothing else.";

    private const string RecapPrompt =
        "You write recaps for a person coming back to a conversation with a coding assistant. In at most three short lines, "
        + "say what was asked, what was done and what is still open, from the events below. Reply with the recap only.";

    private readonly IEventLog _log;
    private readonly IChatClient? _generator;
    private readonly TimeProvider _time;

    /// <param name="generator">The local generator at background priority; null when there is none (a cloud default).</param>
    /// <param name="unavailable">Why there is no generator, for <see cref="Unavailable"/>.</param>
    public SessionTitler(IEventLog log, IChatClient? generator, SessionTitlerOptions? options = null, string? unavailable = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _generator = generator;
        Options = options ?? new SessionTitlerOptions();
        Unavailable = generator is null ? unavailable ?? "no local generator is bound" : null;
        _time = time ?? TimeProvider.System;
    }

    public SessionTitlerOptions Options { get; }

    /// <summary>Why titles and recaps cannot be generated here, or null when they can.</summary>
    public string? Unavailable { get; }

    /// <summary>
    /// The generator a titler may use: the binding's client when its model runs on this machine (or a server on it), else
    /// null with the reason.
    /// </summary>
    public static (IChatClient? Client, string? Unavailable) Local(ModelBinding? binding) => binding switch
    {
        null => (null, "no generator is bound"),
        { Profile.Privacy: PrivacyClass.Cloud } => (null,
            $"the default generator, {binding.Profile.Id}, is a cloud model, and titles and recaps are written only on this machine"),
        { Service: IChatClient client } => (client, null),
        _ => (null, $"{binding.Profile.Id} is not a chat model"),
    };

    /// <summary>
    /// Renames the session (G-6): a <c>SessionTitled</c> by the user. The title is one line, trimmed and cut to
    /// <see cref="SessionCatalogue.TitleLength"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The title is blank.</exception>
    public SessionTitled Rename(string sessionId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var line = SessionCatalogue.Line(title) ?? throw new ArgumentException("A title needs some text.", nameof(title));
        var titled = new SessionTitled(line, TitleSources.User);
        return (SessionTitled)Append(sessionId, titled, _ => true)!.Payload;
    }

    /// <summary>
    /// Writes a title for a session that has none, from its first message and answer; null when the session already has a
    /// title, has no answered exchange yet, or nothing can be generated here. Never throws for the generator's failure: a
    /// title is a convenience.
    /// </summary>
    public async Task<SessionTitled?> TitleAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_generator is null || !Options.Generate)
            return null;
        var snapshot = SessionProjection.Load(_log, sessionId, cancellationToken: cancellationToken);
        if (snapshot.Title is not null)
            return null;
        string? message = null, answer = null;
        var transcript = snapshot.Transcript;
        for (var i = 0; i < transcript.Length; i++)
        {
            // A ! line's message (the user's own shell call follows it) names a command, not what the session is about.
            if (transcript[i].Content is UserMessage { LocalOnly: not true } user && message is null
                && !(i + 1 < transcript.Length && transcript[i + 1].Content is ToolCallIssued { CallId: var call } && call.StartsWith(Corvid.Kernel.Turns.TurnRunner.UserShellCall, StringComparison.Ordinal)))
                message = user.Text;
            else if (transcript[i].Content is AssistantMessage assistant && message is not null)
            {
                answer = assistant.Text;
                break;
            }
        }
        if (message is null || answer is null)
            return null;
        string? title;
        try
        {
            var response = await _generator.GetResponseAsync(
                [new ChatMessage(ChatRole.System, TitlePrompt), new ChatMessage(ChatRole.User, $"User: {Cut(message, 1_000)}\nAssistant: {Cut(answer, 1_000)}")],
                new ChatOptions { MaxOutputTokens = Options.TitleTokens, Temperature = 0.2f }, cancellationToken).ConfigureAwait(false);
            title = Clean(response.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        if (title is null)
            return null;
        // Only while the session still has no title: a rename that arrived while the generator ran wins.
        return Append(sessionId, new SessionTitled(title, TitleSources.Generated), current => current.Title is null)?.Payload as SessionTitled;
    }

    /// <summary>
    /// A recap of the session's events since its last recap (all of them when it has none), appended as
    /// <c>SessionRecapped</c>. Null when nothing happened since the last recap.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing can be generated here (<see cref="Unavailable"/>), or the generator failed.</exception>
    public async Task<SessionRecapped?> RecapAsync(string sessionId, string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (_generator is null)
            throw new InvalidOperationException("No recap: " + Unavailable + ".");
        var snapshot = SessionProjection.Load(_log, sessionId, cancellationToken: cancellationToken);
        var from = snapshot.Recap?.ThroughSequence ?? 0;
        var lines = Lines(snapshot, from);
        if (lines.Count == 0)
            return null;
        string text;
        try
        {
            var response = await _generator.GetResponseAsync(
                [new ChatMessage(ChatRole.System, RecapPrompt), new ChatMessage(ChatRole.User, string.Join('\n', Window(lines)))],
                new ChatOptions { MaxOutputTokens = Options.RecapTokens, Temperature = 0.2f }, cancellationToken).ConfigureAwait(false);
            text = response.Text.Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The local generator could not write a recap: " + exception.Message, exception);
        }
        if (text.Length == 0)
            throw new InvalidOperationException("The local generator wrote an empty recap.");
        var through = snapshot.Head;
        return Append(sessionId, new SessionRecapped(text, from, through, source), _ => true)?.Payload as SessionRecapped;
    }

    /// <summary>
    /// Whether a surface opening the session now should recap it (docs/plan/ui.md §4.5, "on return after an absence"): it
    /// has an answered exchange, something happened since its last recap, and the person last saw it (or, never having seen
    /// it here, it last changed) at least <see cref="SessionTitlerOptions.RecapAfter"/> ago.
    /// </summary>
    /// <param name="now">The surface's clock; this titler's when null.</param>
    public bool ShouldRecapOnReturn(SessionDescription session, DateTimeOffset? lastSeen, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_generator is null || Options.RecapAfter <= TimeSpan.Zero || session.LastAnswer is null)
            return false;
        if (session.Recap is { } recap && recap.ThroughSequence >= session.Head)
            return false;
        var since = lastSeen ?? session.LastActivity;
        return (now ?? _time.GetUtcNow()) - since >= Options.RecapAfter;
    }

    private StoredEvent? Append(string sessionId, SessionEvent payload, Func<SessionSnapshot, bool> still)
    {
        var events = new[] { EventData.Create(payload) };
        for (var attempt = 1; ; attempt++)
        {
            var head = _log.GetHead(sessionId);
            if (!still(SessionProjection.Load(_log, sessionId, head)))
                return null;
            try
            {
                return _log.Append(sessionId, head, events)[0];
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }

    /// <summary>One line per conversational event after <paramref name="from"/>, local-only <c>!!</c> lines left out.</summary>
    private static List<string> Lines(SessionSnapshot snapshot, long from)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var entry in snapshot.Transcript)
        {
            if (entry.ThroughSequence <= from)
                continue;
            switch (entry.Content)
            {
                case UserMessage { LocalOnly: true } or ToolCallIssued { LocalOnly: true } or ToolResult { LocalOnly: true }:
                    break;
                case UserMessage user:
                    lines.Add("user: " + Cut(user.Text, LineChars));
                    break;
                case AssistantMessage assistant:
                    lines.Add("assistant: " + Cut(assistant.Text, LineChars));
                    break;
                case ToolCallIssued call:
                    names[call.CallId] = call.ToolName;
                    lines.Add(("called " + call.ToolName + " " + Cut(ToolPresentation.Target(call.Arguments) ?? "", 120)).TrimEnd());
                    break;
                case ToolResult result:
                    lines.Add($"{names.GetValueOrDefault(result.CallId, "tool")} {(result.IsError ? "failed" : "finished")}");
                    break;
                case Compacted compacted:
                    lines.Add("summary of earlier events: " + Cut(compacted.Summary.Goal ?? compacted.Summary.NextStep ?? "", LineChars));
                    break;
            }
        }
        return lines;
    }

    private IEnumerable<string> Window(List<string> lines)
    {
        var limit = Math.Max(10, Options.RecapLines);
        if (lines.Count <= limit)
            return lines;
        var head = limit / 6;
        var tail = limit - head;
        return [.. lines.Take(head), string.Create(CultureInfo.InvariantCulture, $"… {lines.Count - head - tail} events left out …"), .. lines.TakeLast(tail)];
    }

    /// <summary>The model's reply as a title: its first line without quotes, markdown or a label, cut; null when nothing is left.</summary>
    internal static string? Clean(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        var line = reply.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null)
            return null;
        static string Strip(string text) => text.Trim().Trim('#', '*', '_', '`', '"', '\'', '“', '”', '‘', '’', ' ').Trim();
        line = Strip(SessionCatalogue.Visible(line));
        if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            line = Strip(line[6..]);
        return SessionCatalogue.Line(line.TrimEnd('.', '!', '?', ':', ';', ',').Trim());
    }

    private static string Cut(string text, int length)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length > length ? flat[..length] + "…" : flat;
    }
}
