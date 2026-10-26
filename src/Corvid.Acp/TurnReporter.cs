using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;

namespace Corvid.Acp;

/// <summary>
/// One prompt turn as <c>session/update</c> notifications: the host's session update stream (K-10,
/// <see cref="SessionUpdateReporter"/>) mapped onto ACP. Streamed text is <c>agent_message_chunk</c>, each tool call
/// <c>tool_call</c> when it starts and <c>tool_call_update</c> when it finishes (with its input, result and, for file
/// edits, the diff from the write ledger). The host's reporter produces every update on one ordered chain, so the client
/// sees them in order and all of them before the prompt's response. A running call's output tail (K-2) is a
/// <c>tool_call_update</c> in progress with the tail as its content (A5). Updates ACP's turn has no notification for here (the
/// user's message, the task list, decisions, checkpoints) are not sent; a question the turn ended on goes as
/// <c>_corvid/question</c> after it (<see cref="AcpMethods.CorvidQuestion"/>).
/// </summary>
internal sealed class TurnReporter : ITurnObserver
{
    private readonly SessionUpdateReporter _reporter;

    public TurnReporter(AcpConnectionContext context, CorvidSession session, ToolPresentation tools, long head)
    {
        var sessionId = session.Id;
        _reporter = new SessionUpdateReporter(context.Runtime, sessionId, tools, head, update =>
        {
            if (Live(update) is { } notification)
                context.Update(sessionId, notification);
        }, reason => context.Log($"corvid acp: could not report a tool call: {reason}"));
    }

    /// <summary>Text the generator streamed in its latest call; the delivered answer is compared with it, so nothing is sent twice.</summary>
    public string Streamed => _reporter.Streamed;

    public void GenerationStarted() => _reporter.GenerationStarted();

    public void Text(string delta) => _reporter.Text(delta);

    public void ToolStarted(string callId, string tool, bool approval) => _reporter.ToolStarted(callId, tool, approval);

    public void ToolStarted(string callId, string tool, bool approval, string? parentCallId) => _reporter.ToolStarted(callId, tool, approval, parentCallId);

    public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed) => _reporter.ToolFinished(callId, tool, outcome, elapsed);

    /// <summary>A5: a running call's output tail (K-2), as a <c>tool_call_update</c> whose content is the latest lines.</summary>
    public void ToolOutput(string callId, string tool, string tail) => _reporter.ToolOutput(callId, tool, tail);

    /// <summary>
    /// Makes sure a call is known to the client before a permission request names it: a prompt for a call the feed has not
    /// announced (or an approval outside any call) is announced here. Returns once everything queued so far is sent.
    /// </summary>
    public Task AnnounceAsync(string callId, string title, string kind) => _reporter.AnnounceAsync(callId, title, kind);

    /// <summary>An issued call, read from the log.</summary>
    public ToolCallIssued? Call(string callId) => _reporter.Call(callId);

    /// <summary>Waits until every notification queued so far is sent.</summary>
    public Task DrainAsync() => _reporter.DrainAsync();

    /// <summary>A live update as ACP sends it during a prompt turn; null for those it has no notification for.</summary>
    private static SessionUpdate? Live(SessionStreamUpdate update) => update switch
    {
        AnswerChunk chunk => new AgentMessageChunk(new TextContent(chunk.Text)),
        TurnPlanned planned => Plan(planned),
        ToolCallStarted started => new ToolCall(started.CallId, started.Title, started.Kind, Status(started.Status))
        {
            RawInput = started.Input,
            Locations = Locations(started.Locations),
        },
        ToolCallChanged changed => new ToolCallUpdate(changed.CallId)
        {
            Title = changed.Title,
            Status = changed.Status is { } status ? Status(status) : null,
            RawInput = changed.Input,
            RawOutput = changed.Output,
            Locations = Locations(changed.Locations),
            Content = !changed.Diffs.IsDefaultOrEmpty ? [.. changed.Diffs.Select(Diff)]
                : changed.OutputText is { } text ? [new ContentToolCallContent(new TextContent(text))]
                // A5: a running call's tail replaces the content shown so far, as ACP's tool_call_update content does.
                : changed.Tail is { } tail ? [new ContentToolCallContent(new TextContent(tail))]
                : null,
        },
        _ => null,
    };

    /// <summary>
    /// A replayed update as <c>session/load</c> sends it: the user's messages too, and a call's result as text (a replay
    /// asked for <see cref="ReplayOptions.Transcript"/> carries no diffs).
    /// </summary>
    internal static SessionUpdate? Replayed(SessionStreamUpdate update) => update switch
    {
        PromptSent prompt => new UserMessageChunk(new TextContent(prompt.Text)),
        AnswerChunk chunk => new AgentMessageChunk(new TextContent(chunk.Text)),
        TurnPlanned planned => Plan(planned),
        ToolCallStarted started => new ToolCall(started.CallId, started.Title, started.Kind, Status(started.Status))
        {
            RawInput = started.Input,
            Locations = Locations(started.Locations),
        },
        ToolCallChanged changed => new ToolCallUpdate(changed.CallId)
        {
            Status = changed.Status is { } status ? Status(status) : null,
            RawOutput = changed.Output,
            Content = changed.OutputText is { } text ? [new ContentToolCallContent(new TextContent(text))] : null,
        },
        _ => null,
    };

    /// <summary>
    /// A5: a child session as ACP shows it (ACP has no notion of subagents): a <c>tool_call</c> of its own, its id the
    /// child's session id, when it starts, and a <c>tool_call_update</c> with its status and answer when it ends. Goals and
    /// answers are model text, sent as text content as any tool result is.
    /// </summary>
    internal static SessionUpdate? Child(SessionStreamUpdate update) => update switch
    {
        ChildStarted started => new ToolCall(started.Child.ChildId, "Child session: " + Short(started.Child.Goal), ToolKinds.Other,
            ToolStatuses.InProgress)
        {
            Content = [new ContentToolCallContent(new TextContent(Corvid.Host.Commands.AgentCommands.Started(started.Child)))],
        },
        ChildEnded ended => new ToolCallUpdate(ended.Child.ChildId)
        {
            Status = ended.Child.Status == ChildStatuses.Answered ? ToolStatuses.Completed : ToolStatuses.Failed,
            Content = [new ContentToolCallContent(new TextContent(ended.Child.Text is { Length: > 0 } text && ended.Child.Status == ChildStatuses.Answered
                ? text
                : Corvid.Host.Commands.AgentCommands.Ended(ended.Child)))],
        },
        _ => null,
    };

    /// <summary>
    /// A7: an architect/editor turn's plan as a finished <c>think</c> step (<c>plan-{sequence}</c>), titled with who planned
    /// and who edits, the plan as its content, live and on <c>session/load</c> alike. Model text, sent as text: the client
    /// renders it, nothing runs it.
    /// </summary>
    internal static ToolCall Plan(TurnPlanned planned) =>
        new("plan-" + planned.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"Plan by {planned.Plan.Planner} · edited by {planned.Plan.Editor}" + (planned.Plan.Attempt > 1 ? " (re-plan)" : ""),
            ToolKinds.Think, ToolStatuses.Completed)
        {
            Content = [new ContentToolCallContent(new TextContent(planned.Text))],
        };

    private static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    internal static ToolCallLocation[]? Locations(ImmutableArray<string> paths) =>
        paths.IsDefaultOrEmpty ? null : [.. paths.Select(path => new ToolCallLocation(path))];

    internal static DiffToolCallContent Diff(FileDiff diff) => new(diff.Path, diff.OldText, diff.NewText);

    private static string Status(ToolCallState state) => state switch
    {
        ToolCallState.Pending => ToolStatuses.Pending,
        ToolCallState.InProgress => ToolStatuses.InProgress,
        ToolCallState.Completed => ToolStatuses.Completed,
        _ => ToolStatuses.Failed,
    };
}
