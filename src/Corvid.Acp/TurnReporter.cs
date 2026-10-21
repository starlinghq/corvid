using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Checkpoints;

namespace Corvid.Acp;

/// <summary>
/// One prompt turn as <c>session/update</c> notifications: streamed text as <c>agent_message_chunk</c>, each tool call as
/// <c>tool_call</c> when it starts and <c>tool_call_update</c> when it finishes (with its input, result and, for file edits,
/// the diff from the write ledger). The feed calls in from the turn's threads; every notification is produced on one ordered
/// chain, so the client sees them in order and all of them before the prompt's response.
/// </summary>
internal sealed class TurnReporter : ITurnObserver
{
    private readonly AcpConnectionContext _context;
    private readonly CorvidSession _session;
    private readonly ToolPresentation _tools;
    private readonly Lock _gate = new();
    private readonly StringBuilder _generation = new();
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private Task _chain = Task.CompletedTask;
    private long _read;

    public TurnReporter(AcpConnectionContext context, CorvidSession session, ToolPresentation tools, long head)
    {
        _context = context;
        _session = session;
        _tools = tools;
        _read = head;
    }

    /// <summary>Text the generator streamed in its latest call; the delivered answer is compared with it, so nothing is sent twice.</summary>
    public string Streamed
    {
        get
        {
            lock (_gate)
                return _generation.ToString();
        }
    }

    public void GenerationStarted() => Then(() =>
    {
        lock (_gate)
            _generation.Clear();
    });

    public void Text(string delta) => Then(() =>
    {
        lock (_gate)
            _generation.Append(delta);
        _context.Update(_session.Id, new AgentMessageChunk(new TextContent(delta)));
    });

    public void ToolStarted(string callId, string tool, bool approval) => Then(() => Announce(callId, tool, approval));

    public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed) => Then(async () =>
    {
        Announce(callId, tool, approval: false);
        var (call, result, before, after) = ReadNew(events => (events.Calls.GetValueOrDefault(callId), events.Results.GetValueOrDefault(callId),
            events.Before.GetValueOrDefault(callId), events.After.GetValueOrDefault(callId)));
        var content = new List<ToolCallContent>();
        if (before is not null && after is { Paths: { Length: > 0 } paths })
            content.AddRange(await _tools.LedgerDiffsAsync(before.Revision, after.Revision, paths).ConfigureAwait(false));
        if (content.Count == 0 && result is not null)
            content.Add(new ContentToolCallContent(new TextContent(ToolPresentation.ResultText(result.Result))));
        _context.Update(_session.Id, new ToolCallUpdate(callId)
        {
            Title = ToolPresentation.Title(tool, call?.Arguments),
            Status = outcome == "succeeded" ? ToolStatuses.Completed : ToolStatuses.Failed,
            RawInput = call?.Arguments,
            RawOutput = result?.Result,
            Locations = _tools.Locations(call?.Arguments),
            Content = content.Count > 0 ? [.. content] : null,
        });
    });

    /// <summary>
    /// Makes sure a call is known to the client before a permission request names it: a prompt for a call the feed has not
    /// announced (or an approval outside any call) is announced here. Returns once everything queued so far is sent.
    /// </summary>
    public Task AnnounceAsync(string callId, string title, string kind)
    {
        Then(() =>
        {
            lock (_gate)
                if (!_announced.Add(callId))
                    return;
            _context.Update(_session.Id, new ToolCall(callId, title, kind, ToolStatuses.Pending));
        });
        return DrainAsync();
    }

    /// <summary>An issued call, read from the log.</summary>
    public ToolCallIssued? Call(string callId) => ReadNew(events => events.Calls.GetValueOrDefault(callId));

    /// <summary>Waits until every notification queued so far is sent.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
            return _chain;
    }

    private void Announce(string callId, string tool, bool approval)
    {
        lock (_gate)
            if (!_announced.Add(callId))
                return;
        _context.Update(_session.Id, new ToolCall(callId, tool, _tools.Kind(tool), approval ? ToolStatuses.Pending : ToolStatuses.InProgress));
    }

    private void Then(Action step) => Then(() =>
    {
        step();
        return Task.CompletedTask;
    });

    private void Then(Func<Task> step)
    {
        lock (_gate)
            _chain = _chain.ContinueWith(async _ =>
            {
                try
                {
                    await step().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A report that cannot be built (a checkpoint the store cannot read) must not fail the turn.
                    _context.Log($"corvid acp: could not report a tool call: {exception.Message}");
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private readonly Dictionary<string, ToolCallIssued> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckpointTaken> _before = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckpointTaken> _after = new(StringComparer.Ordinal);

    private T ReadNew<T>(Func<(Dictionary<string, ToolCallIssued> Calls, Dictionary<string, ToolResult> Results,
        Dictionary<string, CheckpointTaken> Before, Dictionary<string, CheckpointTaken> After), T> pick)
    {
        lock (_calls)
        {
            // The turn's own events only: the reporter starts at the head the turn began from.
            while (true)
            {
                var page = _context.Runtime.Log.Read(_session.Id, _read, limit: 256);
                if (page.Count == 0)
                    break;
                foreach (var item in page)
                {
                    switch (item.Payload)
                    {
                        case ToolCallIssued call:
                            _calls[call.CallId] = call;
                            break;
                        case ToolResult result:
                            _results[result.CallId] = result;
                            break;
                        case CheckpointTaken { CallId: { } id, Kind: CheckpointKinds.Before } taken:
                            _before[id] = taken;
                            break;
                        case CheckpointTaken { CallId: { } id, Kind: CheckpointKinds.After } taken:
                            _after[id] = taken;
                            break;
                    }
                }
                _read = page[^1].Sequence;
            }
            return pick((_calls, _results, _before, _after));
        }
    }
}
