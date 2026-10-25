using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Kernel.Turns;
using Corvid.Tools;

namespace Corvid.Host.Composition;

/// <summary>
/// One child of a <c>delegate_task</c> call as its approval card shows it (A5): the plan within the parent's ceilings, or why
/// it would be refused. <see cref="Goal"/> is the model's text, untrusted.
/// </summary>
public sealed record DelegationPreview(string Goal, ChildPlan? Plan, string? Refusal);

/// <summary>A child of a session as the surfaces list it (<c>/agents</c>, the Board): from the parent's log, and live when running here.</summary>
/// <param name="Running">It runs in this process now (it can be stopped).</param>
public sealed record ChildSummary(ChildSpawned Spawned, ChildFinished? Finished, bool Running)
{
    public string ChildId => Spawned.ChildId;

    /// <summary>The <see cref="ChildStatuses"/> name, or <c>running</c> (or <c>unfinished</c> when no process runs it any more).</summary>
    public string Status => Finished?.Status ?? (Running ? "running" : "unfinished");
}

/// <summary>
/// A5: <c>delegate_task</c> (plan §14, §17 row 1.7) on top of the child sessions: the tool's runner, its approval card's
/// preview, named toolsets, and the parent's children for the surfaces (list, stop).
/// </summary>
public sealed partial class CorvidRuntime
{
    /// <summary>The built-in toolset of every tool the parent has that changes nothing and reads nothing from the network.</summary>
    public const string ReadOnlyToolset = "read_only";

    /// <summary>
    /// The tool the agent calls (registered by the factory when <c>Corvid:Tools:Delegate</c> is on and
    /// <c>Corvid:Agents:MaxDepth</c> is above 0): each task planned within the calling session's ceilings, the ones that can
    /// start opened, all run at once, and their results returned as one bounded result.
    /// </summary>
    internal static DelegateTaskTool CreateDelegateTool(Func<CorvidRuntime> runtime) =>
        new((tasks, context, cancellationToken) => runtime().DelegateAsync(tasks, context, cancellationToken));

    /// <summary>
    /// A5: what a <c>delegate_task</c> call would start, for its approval card: each child's plan (its clamped budget, its
    /// toolset with what was left out, its resolved model mix and approval mode), or the reason it would be refused. Nothing
    /// is started or logged. Called while the call waits on its approval, so the card shows what the person grants.
    /// </summary>
    public async Task<IReadOnlyList<DelegationPreview>> PreviewDelegationAsync(string sessionId, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!DelegateTaskTool.TryRead(arguments, out var tasks, out var error))
            return [new DelegationPreview("", null, "The call is malformed: " + error + ".")];
        var previews = new List<DelegationPreview>();
        foreach (var task in tasks)
        {
            try
            {
                var request = DelegationRequest(sessionId, task, offered: null, ChildSessions.ByAgent, callId: null);
                previews.Add(new(task.Goal, await PlanChildAsync(sessionId, request, cancellationToken).ConfigureAwait(false), null));
            }
            catch (ChildRefusedException exception)
            {
                previews.Add(new(task.Goal, null, exception.Message));
            }
        }
        return previews;
    }

    /// <summary>
    /// A5: a child request from one <c>delegate_task</c> task (or <c>/delegate</c>): its named toolset expanded over the
    /// parent's tools and added to its explicit ones. The rest is clamped by <see cref="PlanChildAsync"/>.
    /// </summary>
    /// <exception cref="ChildRefusedException">The parent is not open here, or the toolset is not one Corvid knows.</exception>
    public ChildRequest DelegationRequest(string parentId, DelegationTask task, IReadOnlySet<string>? offered, string by, string? callId)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!TryGetSession(parentId, out var parent))
            throw new ChildRefusedException($"Session {TerminalText.Visible(parentId)} is not open in this process.");
        var tools = task.Tools.IsDefault ? [] : task.Tools;
        if (task.Toolset is { } name)
            tools = [.. tools, .. ToolsetTools(parent, name)];
        return new ChildRequest(task.Goal)
        {
            Tools = [.. tools.Distinct(StringComparer.Ordinal)],
            OfferedTools = offered,
            MixName = task.MixName,
            Mix = task.Mix,
            MaxSteps = task.MaxSteps,
            MaxTokens = task.MaxTokens,
            MaxSeconds = task.MaxSeconds,
            OutputSchema = task.OutputSchema,
            By = by,
            CallId = callId,
        };
    }

    /// <summary>The names of a toolset: <see cref="ReadOnlyToolset"/> over the parent's tools, or a configured one.</summary>
    private IEnumerable<string> ToolsetTools(CorvidSession parent, string name)
    {
        if (string.Equals(name, ReadOnlyToolset, StringComparison.OrdinalIgnoreCase))
            return parent.Tools.List().Where(tool => tool.Risk is { ReadOnly: true, Network: false }).Select(tool => tool.Name)
                .Order(StringComparer.Ordinal);
        if (Options.Agents.Toolsets.TryGetValue(name, out var configured) && configured is not null)
            return configured;
        var known = Options.Agents.Toolsets.Keys.Prepend(ReadOnlyToolset).Order(StringComparer.Ordinal);
        throw new ChildRefusedException($"There is no toolset named \"{TerminalText.Visible(name)}\"; the toolsets are {string.Join(", ", known)}.");
    }

    /// <summary>
    /// The tool's runner: the children of one call, planned within the calling turn's ceilings (its offered tools included),
    /// opened as places allow, run at once, and their results as the call's result. A single child that cannot start is the
    /// call's error. A tainted child's result taints the parent (plan §10.3).
    /// </summary>
    private async Task<object?> DelegateAsync(ImmutableArray<DelegationTask> tasks, ToolCallContext context, CancellationToken cancellationToken)
    {
        var started = new List<(int Index, CorvidSession Child)>();
        var outcomes = new object?[tasks.Length];
        for (var i = 0; i < tasks.Length; i++)
        {
            try
            {
                var request = DelegationRequest(context.SessionId, tasks[i], context.OfferedTools ?? new HashSet<string>(StringComparer.Ordinal),
                    ChildSessions.ByAgent, context.CallId);
                var plan = await PlanChildAsync(context.SessionId, request, cancellationToken).ConfigureAwait(false);
                started.Add((i, OpenChildSession(plan)));
            }
            catch (ChildRefusedException exception)
            {
                if (tasks.Length == 1)
                    throw;
                outcomes[i] = Refused(exception.Message);
            }
        }
        // Each opened child holds a place until it is run, so every one is run, also when another failed to start.
        var results = await Task.WhenAll(started.Select(item => RunChildAsync(item.Child, cancellationToken))).ConfigureAwait(false);
        for (var i = 0; i < results.Length; i++)
        {
            outcomes[started[i].Index] = Outcome(results[i]);
            if (results[i].Tainted)
                context.MarkResultTainted();
        }
        return tasks.Length == 1 ? outcomes[0] : new Dictionary<string, object?> { ["children"] = outcomes };
    }

    private static Dictionary<string, object?> Refused(string why) => new() { ["status"] = "refused", ["detail"] = why };

    /// <summary>A child's result as the parent model reads it: bounded, its JSON output when the schema held.</summary>
    internal static Dictionary<string, object?> Outcome(ChildResult result)
    {
        var outcome = new Dictionary<string, object?>
        {
            ["child_id"] = result.ChildId,
            ["status"] = result.Status,
        };
        if (result.Status == ChildStatuses.Answered && result.Output is { } output)
            outcome["output"] = output;
        else if (result.Text is { Length: > 0 } text)
            outcome["answer"] = text.Length > MaxChildTextChars ? text[..MaxChildTextChars] + "…" : text;
        if (result.Detail is { Length: > 0 } detail)
            outcome["detail"] = detail.Length > 600 ? detail[..600] + "…" : detail;
        outcome["steps"] = result.Steps;
        outcome["tokens"] = result.Tokens;
        return outcome;
    }

    /// <summary>
    /// A5: a session's children, oldest first, from its log (<c>ChildSpawned</c> and <c>ChildFinished</c>), each marked running
    /// when this process runs it now.
    /// </summary>
    public IReadOnlyList<ChildSummary> Children(string parentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        var spawned = new List<ChildSpawned>();
        var finished = new Dictionary<string, ChildFinished>(StringComparer.Ordinal);
        for (long after = 0; ;)
        {
            var page = Log.Read(parentId, after, limit: 4096);
            foreach (var stored in page)
                switch (stored.Payload)
                {
                    case ChildSpawned child:
                        spawned.Add(child);
                        break;
                    case ChildFinished end:
                        finished[end.ChildId] = end;
                        break;
                }
            if (page.Count < 4096)
                break;
            after = page[^1].Sequence;
        }
        return [.. spawned.Select(child => new ChildSummary(child, finished.GetValueOrDefault(child.ChildId), _childPlans.ContainsKey(child.ChildId)))];
    }

    /// <summary>
    /// A5: stops a child running in this process (<c>/agents stop</c>, the Board): its turn is cancelled and it ends
    /// <see cref="ChildStatuses.Cancelled"/>, "stopped by the person". False when no such child runs here.
    /// </summary>
    public bool StopChild(string childId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childId);
        if (!_childPlans.ContainsKey(childId) || !_childUsage.TryGetValue(childId, out var usage))
            return false;
        _stoppedChildren[childId] = 0;
        usage.Stop();
        return true;
    }

    private readonly ConcurrentDictionary<string, Task<ChildResult>> _startedChildren = new(StringComparer.Ordinal);

    // A5: the child ids OpenChildSession is opening now: the only way a child-… session is opened.
    private readonly ConcurrentDictionary<string, byte> _openingChildren = new(StringComparer.Ordinal);

    /// <summary>
    /// A5: refuses to open a child session by id outside its parent's run, as another process's session is refused: it is
    /// read-only on every surface (<see cref="SessionHeldException"/>, which they already show as such).
    /// </summary>
    private void RefuseChildSession(string id)
    {
        if (id.StartsWith(ChildSessions.Surface + "-", StringComparison.Ordinal) && !_openingChildren.ContainsKey(id))
            throw new SessionHeldException($"Session {TerminalText.Visible(id)} is a child session: read-only, its parent's record. " +
                "Its parent's /agents show (or the app's Board) shows it.");
    }

    /// <summary>Opens a child's session for <see cref="OpenChildSession"/>, the one caller <see cref="RefuseChildSession"/> lets through.</summary>
    private CorvidSession OpenChild(string id, SessionSettings settings)
    {
        _openingChildren[id] = 0;
        try
        {
            return OpenSession(id, ChildSessions.Surface, settings);
        }
        finally
        {
            _openingChildren.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// A5: starts a child a person asked for (<c>/delegate</c>, the app) and lets it run in the background: the plan's child
    /// opened now (its <c>ChildSpawned</c> logged), run until it ends, stops with <see cref="StopChild"/> or the runtime's
    /// end, and reported through <see cref="ChildChanged"/>. The person asked for it, so nothing is approved first; its own
    /// calls ask as the parent's would. Returns the child session, whose <see cref="Completion"/> is its result.
    /// </summary>
    /// <exception cref="ChildRefusedException">The parent already runs as many children as it may, or is not open here.</exception>
    public CorvidSession StartChild(ChildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var child = OpenChildSession(plan);
        // Listed before it runs, so its end always finds (and removes) its entry.
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startedChildren[child.Id] = RunStartedAsync(child, listed.Task);
        listed.SetResult();
        return child;
    }

    /// <summary>A started child's result once it ends; null when <paramref name="childId"/> was not started by <see cref="StartChild"/> or has been forgotten.</summary>
    public Task<ChildResult>? Completion(string childId) => _startedChildren.TryGetValue(childId, out var running) ? running : null;

    private async Task<ChildResult> RunStartedAsync(CorvidSession child, Task listed)
    {
        try
        {
            // Off the caller's thread: the command that started it returns at once.
            await listed.ConfigureAwait(false);
            return await RunChildAsync(child, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _startedChildren.TryRemove(child.Id, out _);
        }
    }

    private async Task StartedChildrenIdleAsync()
    {
        foreach (var running in _startedChildren.Values.ToArray())
            await ((Task)running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static string RepairMessage(IReadOnlyList<string> errors) =>
        "Your answer does not meet the output schema: " + string.Join("; ", errors.Take(8)) +
        ". Answer again with only the JSON object the schema describes, and nothing else.";
}
