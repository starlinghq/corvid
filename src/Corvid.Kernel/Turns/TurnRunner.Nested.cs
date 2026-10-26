using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Kernel.Events;
using Corvid.Tools;

namespace Corvid.Kernel.Turns;

public sealed partial class TurnRunner
{
    /// <summary>
    /// A7: the nested calls one running call makes (code mode's script calling tools). They run one at a time; after the
    /// parent's tool returns they are cancelled and no new one is accepted, so each has its result before the parent's.
    /// </summary>
    private sealed class NestedCalls(ValidCall parent, DateTimeOffset startedAt, CancellationToken parentToken) : IDisposable
    {
        /// <summary>When the parent's tool was started, by the turn's clock.</summary>
        public DateTimeOffset StartedAt => startedAt;

        /// <summary>A7V1-3: when the parent stops its nested calls (<see cref="ToolCallContext.NestedTimeLimit"/>); null for no limit.</summary>
        public DateTimeOffset? NotAfter => Context?.NestedTimeLimit is { } limit ? startedAt + limit : null;

        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        private volatile bool _closing;

        public ValidCall Parent => parent;

        /// <summary>The parent's context, marked tainted when a nested result is outside content.</summary>
        public ToolCallContext? Context { get; set; }

        /// <summary>Held by the one nested call running; never disposed (a late caller must get the closed error, not a disposed one).</summary>
        public SemaphoreSlim One { get; } = new(1, 1);

        public bool Closing => _closing;

        public CancellationToken Stopping => _stop.Token;

        /// <summary>Ids the log holds, read once at the first nested call and kept up to date by the calls that follow.</summary>
        public HashSet<string>? Issued { get; set; }

        public int Next { get; set; }

        /// <summary>Cancels a nested call still running (or waiting on its approval) and waits until it has its result.</summary>
        public async Task CloseAsync()
        {
            _closing = true;
            await _stop.CancelAsync().ConfigureAwait(false);
            await One.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            One.Release();
        }

        public void Dispose() => _stop.Dispose();
    }

    private sealed partial class Turn
    {
        // A7: the nested calls a running invoker waits on, and their results as logged (scrubbed), by call id. Under _gate.
        private readonly HashSet<string> _nestedPending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolResult> _nestedResults = new(StringComparer.Ordinal);

        /// <summary>Keeps a nested call's result, as the log holds it, for its invoker. The caller holds <c>_gate</c>.</summary>
        private void KeepNestedResult(ToolResult result)
        {
            if (_nestedPending.Contains(result.CallId))
                _nestedResults[result.CallId] = (ToolResult)EventScrubber.Scrub(result);
        }

        /// <summary>
        /// A7: one nested call (<see cref="ToolCallContext.InvokeToolAsync"/>), through the same path as a model's call: the
        /// tool must be one the turn may offer, its arguments are validated, it asks when a model's call would (a side effect;
        /// a network read in a tainted session or one a detection rule flags), and <see cref="ExecuteAsync"/> runs it with its
        /// lease, approval, checkpoints, result and taint. A refusal is logged as a rejected nested call and returned as an
        /// error result. The parent's lease stays held throughout; this call is tracked or parked under its own id.
        /// </summary>
        private async Task<NestedToolResult> InvokeNestedAsync(NestedCalls nested, string name, JsonElement arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (nested.Closing)
                throw Finished(nested);
            CancellationTokenSource linkedSource;
            try
            {
                linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, nested.Stopping);
            }
            catch (ObjectDisposedException)
            {
                // The parent finished between the check above and here.
                throw Finished(nested);
            }
            using var linked = linkedSource;
            var token = linked.Token;
            await nested.One.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (nested.Closing)
                    throw Finished(nested);
                token.ThrowIfCancellationRequested();
                var parentId = nested.Parent.CallId;
                nested.Issued ??= new HashSet<string>(runner.Snapshot().IssuedCallIds, StringComparer.Ordinal);
                string callId;
                do
                    callId = $"{parentId}.{++nested.Next}";
                while (!nested.Issued.Add(callId));
                // Arguments are an object, as a model's are; anything else is logged as an empty object and refused.
                var objectArguments = arguments.ValueKind == JsonValueKind.Object;
                var input = objectArguments ? arguments.Clone() : JsonValues.Object(new Dictionary<string, object?>());
                lock (_gate)
                    _nestedPending.Add(callId);
                try
                {
                    var tool = _catalogue.GetValueOrDefault(name);
                    var refusal = tool is null ? $"'{name}' is not one of the tools this turn may use"
                        : tool.Function is AskUserTool ? $"'{name}' cannot be called from inside another tool"
                        : string.Equals(name, nested.Parent.Tool.Function.Name, StringComparison.Ordinal) ? $"'{name}' cannot call itself"
                        : !objectArguments ? "the arguments must be a JSON object"
                        : null;
                    var outcome = CallOutcome.BindingFailed;
                    if (refusal is not null)
                        IssueRejected(callId, name, input, tool?.Descriptor.Risk.ReadOnly, Error(ToolErrorKinds.InvalidArguments, [refusal], null, null), parentId);
                    else if (ArgumentValidator.Validate(tool!.Function.JsonSchema, input).ToList() is { Count: > 0 } errors)
                        IssueRejected(callId, name, input, tool.Descriptor.Risk.ReadOnly, Error(ToolErrorKinds.InvalidArguments, errors, tool.Function.JsonSchema, null), parentId);
                    else
                    {
                        bool tainted;
                        lock (_gate)
                            tainted = runner.Tainted;
                        // As RunCallsAsync judges a model's call, with taint as of now: an earlier nested fetch taints the rest.
                        var risk = tool.Descriptor.Risk;
                        var ask = !risk.ReadOnly || risk.Network && (tainted
                            || _services.Approvals is { } approvals && approvals.Detects(runner.SessionId, callId,
                                new ToolAction(tool.Descriptor, input), [], token));
                        outcome = await ExecuteAsync(new ValidCall(callId, tool, input, ask, [], parentId) { NotAfter = nested.NotAfter }, token)
                            .ConfigureAwait(false);
                    }
                    ToolResult logged;
                    bool outside;
                    lock (_gate)
                    {
                        logged = _nestedResults[callId];
                        // As ExecuteCoreAsync taints the session: a network tool that ran, or a tool that said so.
                        outside = tool is not null && tool.Descriptor.Risk.Network && outcome is CallOutcome.Succeeded or CallOutcome.Failed
                            || _taintedResults.Contains(callId);
                    }
                    if (outside)
                        nested.Context?.MarkResultTainted();
                    return new NestedToolResult(callId, logged.Result, logged.IsError, outside);
                }
                finally
                {
                    lock (_gate)
                    {
                        _nestedPending.Remove(callId);
                        _nestedResults.Remove(callId);
                    }
                }
            }
            finally
            {
                nested.One.Release();
            }
        }

        private static InvalidOperationException Finished(NestedCalls nested) =>
            new($"The call '{nested.Parent.CallId}' has finished; its tool can make no more nested calls.");
    }
}
