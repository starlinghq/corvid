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
    private readonly Dictionary<string, ApprovalAssessed> _assessed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly List<SessionStreamUpdate> _fromLog = [];
    // K-2: each running call's latest tail not yet reported, the calls with a report queued, and the calls that finished.
    private readonly Dictionary<string, (string Tool, string Tail)> _tails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tailDue = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private Task _chain = Task.CompletedTask;
    private long _read;
    private readonly Func<bool>? _tainted;
    // The chain's own: the taint last reported, and what the step being run says brought it in.
    private bool? _taintReported;
    private string? _taintSource;
    private bool _finishing;

    /// <param name="head">The log position the turn starts from: only later events are the turn's.</param>
    /// <param name="emit">Called with each update, one at a time and in order, on a pool thread.</param>
    /// <param name="log">Told why a report could not be built (a checkpoint the store cannot read); it never fails the turn.</param>
    /// <param name="tainted">
    /// The session's taint, for a surface that shows it: read as each report is queued, so in order with the turn rather than
    /// when the chain gets to it, and reported as <see cref="TaintNoted"/> after the update it came with when it changed. A
    /// call's result that brought outside content in names it as the source.
    /// </param>
    public SessionUpdateReporter(CorvidRuntime runtime, string sessionId, ToolPresentation tools, long head, Action<SessionStreamUpdate> emit,
        Action<string>? log = null, TimeProvider? time = null, Func<bool>? tainted = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(emit);
        _runtime = runtime;
        _sessionId = sessionId;
        // A6 (G-4): calls are shown in the workspace the session works in.
        _tools = tools.ForSessionOrSelf(sessionId);
        _read = head;
        _emit = emit;
        _log = log;
        _time = time ?? TimeProvider.System;
        _tainted = tainted;
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

    public void Thought(string delta) => Then(() => Emit(new ThoughtChunk(delta)));

    public void Usage(long? inputTokens, long? outputTokens) => Then(() => Emit(new UsageReported(inputTokens, outputTokens)));

    public void ToolStarted(string callId, string tool, bool approval) => Then(() =>
    {
        CatchUp();
        Announce(callId, tool, approval);
    });

    public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed) => Then(async () =>
    {
        // A tail still queued for the call is stale now: the finished call brings its result.
        lock (_gate)
        {
            _finished.Add(callId);
            _tails.Remove(callId);
        }
        Announce(callId, tool, approval: false);
        var (call, result, before, after, assessed, asked) = ReadNew(() => (_calls.GetValueOrDefault(callId), _results.GetValueOrDefault(callId),
            _before.GetValueOrDefault(callId), _after.GetValueOrDefault(callId), _assessed.GetValueOrDefault(callId), _asked.Contains(callId)));
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
            Generator = call?.Generator,
            Output = result?.Result,
            OutputText = result is null ? null : ToolPresentation.ResultText(result.Result),
            Locations = _tools.Locations(call?.Arguments),
            Diffs = [.. diffs],
            Elapsed = elapsed,
            Assessment = assessed,
            // A read-only call that was asked (a network read in a tainted turn) is the prompter's to close.
            ReadOnly = asked ? null : call?.ReadOnly,
            CheckpointBefore = before?.CheckpointId,
        });
        // The kernel taints the session as the call's result comes in, before the call ends: this call brought it in.
        _taintSource = $"{tool} {ToolPresentation.Target(call?.Arguments)}".Trim();
    });

    /// <summary>
    /// The turn began a step: <see cref="TurnStepped"/>, after what the log holds so far (the steers the step checkpoint took
    /// before it, the previous step's calls).
    /// </summary>
    public void Progress(int step, int maxSteps) => Then(() =>
    {
        CatchUp();
        Emit(new TurnStepped(step, maxSteps));
    });

    /// <summary>
    /// K-2: a running call's tail, as <see cref="ToolCallChanged"/> with <see cref="ToolCallChanged.Tail"/> (the call announced
    /// first). Tails that arrive while one is queued replace it, so a chatty command adds at most one queued report per call
    /// and the surface sees the latest; none after the call finished.
    /// </summary>
    public void ToolOutput(string callId, string tool, string tail)
    {
        lock (_gate)
        {
            if (_finished.Contains(callId))
                return;
            _tails[callId] = (tool, tail);
            if (!_tailDue.Add(callId))
                return;
        }
        Then(() =>
        {
            (string Tool, string Tail) latest;
            lock (_gate)
            {
                _tailDue.Remove(callId);
                if (_finished.Contains(callId) || !_tails.Remove(callId, out latest))
                    return;
            }
            Announce(callId, latest.Tool, approval: false);
            Emit(new ToolCallChanged(callId)
            {
                Tool = latest.Tool,
                Kind = _tools.Kind(latest.Tool),
                Status = ToolCallState.InProgress,
                Tail = latest.Tail,
            });
        });
    }

    public void Decided(CheckpointDecisions decisions) => Then(() =>
    {
        // The ingress checkpoint decides around the moment the prompt is logged: the prompt goes first when it is there.
        CatchUp();
        Emit(new DecisionsMade(decisions));
    });

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
    /// <summary>
    /// A5: reads the log now and emits what it holds, so a question the turn just asked (its <c>ClarificationAsked</c>) is
    /// shown before the surface waits for the answer. Returns once it is emitted.
    /// </summary>
    public Task CatchUpAsync()
    {
        Then(CatchUp);
        return DrainAsync();
    }

    public Task FinishAsync(string? answer)
    {
        Then(() =>
        {
            CatchUp();
            // The last step: a taint still waiting for the call that brought it in is reported now, with what is known.
            _finishing = true;
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
        {
            // Read now, as the feed calls in: the chain may run this step after the turn has moved on (a busy machine), and the
            // session's taint then would be a later call's, or the next message's.
            var tainted = _tainted?.Invoke();
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
                try
                {
                    NoteTaint(tainted);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _log?.Invoke(exception.Message);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>After a step: the taint read when it was queued, reported when it changed, with the source the step named.</summary>
    private void NoteTaint(bool? tainted)
    {
        if (tainted is not { } now || now == _taintReported)
            return;
        // A taint read before any finished call named its source (the feed's start of a call can reach the chain after the
        // call's result tainted the session) waits for the step that names it, or for the turn's end.
        // Only a taint a call of this turn can have brought in waits: one the session had when the turn began (an External
        // message, a tainted fork, a child) has no call to name and is reported at once.
        bool callsStarted;
        lock (_gate)
            callsStarted = _announced.Count > 0;
        if (now && _taintSource is null && !_finishing && callsStarted)
            return;
        // The source is the last call that finished, kept until a taint is reported: the session can turn tainted just after
        // that call's step was queued, so the taint is read with the next step (the answer's text), which names no call.
        var source = _taintSource;
        _taintSource = null;
        _taintReported = now;
        Emit(new TaintNoted(now, now ? source : null));
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
                            _fromLog.Add(new PromptSent(user.Text, item.Sequence) { At = item.RecordedAt, From = user.From, Images = user.Images ?? [] });
                            break;
                        // K-3: a steer the step checkpoint took; K-6: the question a clarifying turn ended on.
                        case UserSteered steer:
                            _fromLog.Add(new SteerSent(steer.Text, item.Sequence) { At = item.RecordedAt });
                            break;
                        case ClarificationAsked question:
                            _fromLog.Add(new QuestionRaised(question.QuestionId, question.Text, question.Options.IsDefault ? [] : question.Options)
                            {
                                At = item.RecordedAt,
                            });
                            break;
                        // A5: a question the person answered inside the turn.
                        case ClarificationAnswered answered:
                            _fromLog.Add(new QuestionClosed(answered.QuestionId, answered.Answer) { At = item.RecordedAt });
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
                        case ApprovalAssessed assessed:
                            _assessed[assessed.CallId] = assessed;
                            break;
                        case ApprovalRequested { CallId: { } askedFor }:
                            _asked.Add(askedFor);
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
