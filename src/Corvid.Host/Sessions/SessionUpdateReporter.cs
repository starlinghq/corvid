using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Events;

namespace Corvid.Host.Sessions;

/// <summary>
/// One session's live updates while its turns run (K-10, moved from Corvid.Acp's <c>TurnReporter</c> so every surface
/// reports alike): subscribed to the runtime's <see cref="TurnFeed"/> for the session, it turns streamed text into
/// <see cref="AnswerChunk"/>s, each tool call into <see cref="ToolCallStarted"/> when it starts and
/// <see cref="ToolCallChanged"/> when it finishes (with its input, result and, for file edits, the diff from the write
/// ledger), and the fabric's decisions into <see cref="DecisionsMade"/>; and it reads the session's new log events as it
/// goes, for the user's message, the task list and checkpoints. The feed calls in from the turn's threads; every update is
/// produced on one ordered chain, so <c>emit</c> is called once at a time, in order, and all of them before
/// <see cref="DrainAsync"/> or <see cref="FinishAsync"/> completes.
/// </summary>
public sealed class SessionUpdateReporter : ITurnObserver
{
    private readonly CorvidRuntime _runtime;
    private readonly string _sessionId;
    private readonly ToolPresentation _tools;
    private readonly Action<SessionStreamUpdate> _emit;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly StringBuilder _generation = new();
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallIssued> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckpointTaken> _before = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckpointTaken> _after = new(StringComparer.Ordinal);
    private readonly List<SessionStreamUpdate> _fromLog = [];
    private Task _chain = Task.CompletedTask;
    private long _read;

    /// <param name="head">The log position the turn starts from: only later events are the turn's.</param>
    /// <param name="emit">Called with each update, one at a time and in order, on a pool thread.</param>
    /// <param name="log">Told why a report could not be built (a checkpoint the store cannot read); it never fails the turn.</param>
    public SessionUpdateReporter(CorvidRuntime runtime, string sessionId, ToolPresentation tools, long head, Action<SessionStreamUpdate> emit,
        Action<string>? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(emit);
        _runtime = runtime;
        _sessionId = sessionId;
        _tools = tools;
        _read = head;
        _emit = emit;
        _log = log;
        _time = time ?? TimeProvider.System;
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
        CatchUp();
    });

    public void Text(string delta) => Then(() =>
    {
        lock (_gate)
            _generation.Append(delta);
        Emit(new AnswerChunk(delta));
    });

    public void ToolStarted(string callId, string tool, bool approval) => Then(() =>
    {
        CatchUp();
        Announce(callId, tool, approval);
    });

    public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed) => Then(async () =>
    {
        Announce(callId, tool, approval: false);
        var (call, result, before, after) = ReadNew(() => (_calls.GetValueOrDefault(callId), _results.GetValueOrDefault(callId),
            _before.GetValueOrDefault(callId), _after.GetValueOrDefault(callId)));
        FlushLog();
        IReadOnlyList<FileDiff> diffs = [];
        if (before is not null && after is { Paths: { Length: > 0 } paths })
            diffs = await _tools.LedgerDiffsAsync(before.Revision, after.Revision, paths).ConfigureAwait(false);
        Emit(new ToolCallChanged(callId)
        {
            Title = ToolPresentation.Title(tool, call?.Arguments),
            Tool = tool,
            Kind = _tools.Kind(tool),
            Status = outcome == "succeeded" ? ToolCallState.Completed : ToolCallState.Failed,
            Outcome = outcome,
            Input = call?.Arguments,
            Output = result?.Result,
            OutputText = result is null ? null : ToolPresentation.ResultText(result.Result),
            Locations = _tools.Locations(call?.Arguments),
            Diffs = [.. diffs],
            Elapsed = elapsed,
        });
    });

    public void Decided(CheckpointDecisions decisions) => Then(() => Emit(new DecisionsMade(decisions)));

    /// <summary>
    /// Makes sure a call is known before a permission request names it: a prompt for a call the feed has not announced (or
    /// an approval outside any call) is announced here, pending. Returns once everything queued so far is emitted.
    /// </summary>
    public Task AnnounceAsync(string callId, string title, string kind)
    {
        Then(() =>
        {
            lock (_gate)
                if (!_announced.Add(callId))
                    return;
            Emit(new ToolCallStarted(callId, title, kind, ToolCallState.Pending));
        });
        return DrainAsync();
    }

    /// <summary>An issued call of this turn, read from the log.</summary>
    public ToolCallIssued? Call(string callId) => ReadNew(() => _calls.GetValueOrDefault(callId));

    /// <summary>Waits until every update queued so far is emitted.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
            return _chain;
    }

    /// <summary>
    /// Ends the turn's report once its delivery is in: the log events not yet read (the user's message of a turn that
    /// reported nothing, a last task list), then the delivered answer when it was not streamed as it reads (a fixed
    /// message, an answer the generator did not stream). Returns when all of it is emitted.
    /// </summary>
    public Task FinishAsync(string? answer)
    {
        Then(() =>
        {
            CatchUp();
            if (answer is not null && !string.Equals(answer.Trim(), Streamed.Trim(), StringComparison.Ordinal))
                Emit(new AnswerChunk(answer));
        });
        return DrainAsync();
    }

    private void Announce(string callId, string tool, bool approval)
    {
        lock (_gate)
            if (!_announced.Add(callId))
                return;
        Emit(new ToolCallStarted(callId, tool, _tools.Kind(tool), approval ? ToolCallState.Pending : ToolCallState.InProgress) { Tool = tool });
    }

    private void Emit(SessionStreamUpdate update) => _emit(update with { At = update.At == default ? _time.GetUtcNow() : update.At });

    /// <summary>Reads the log up to now and emits what it holds for surfaces, in log order.</summary>
    private void CatchUp()
    {
        ReadNew(() => 0);
        FlushLog();
    }

    private void FlushLog()
    {
        SessionStreamUpdate[] pending;
        lock (_calls)
        {
            pending = [.. _fromLog];
            _fromLog.Clear();
        }
        foreach (var update in pending)
            Emit(update);
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
                    _log?.Invoke(exception.Message);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private T ReadNew<T>(Func<T> pick)
    {
        lock (_calls)
        {
            // The turn's own events only: the reporter starts at the head the turn began from.
            while (true)
            {
                var page = _runtime.Log.Read(_sessionId, _read, limit: 256);
                if (page.Count == 0)
                    break;
                foreach (var item in page)
                {
                    switch (item.Payload)
                    {
                        case UserMessage user:
                            _fromLog.Add(new PromptSent(user.Text, item.Sequence) { At = item.RecordedAt });
                            break;
                        case TodosSet todos:
                            _fromLog.Add(new PlanChanged(todos.Items) { At = item.RecordedAt });
                            break;
                        case ToolCallIssued call:
                            _calls[call.CallId] = call;
                            break;
                        case ToolResult result:
                            _results[result.CallId] = result;
                            break;
                        case CheckpointTaken taken:
                            if (taken is { CallId: { } before, Kind: CheckpointKinds.Before })
                                _before[before] = taken;
                            else if (taken is { CallId: { } after, Kind: CheckpointKinds.After })
                                _after[after] = taken;
                            _fromLog.Add(new CheckpointLogged(taken, item.Sequence) { At = item.RecordedAt });
                            break;
                    }
                }
                _read = page[^1].Sequence;
            }
            return pick();
        }
    }
}
