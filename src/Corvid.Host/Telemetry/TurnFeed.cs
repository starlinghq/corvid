using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Corvid.Fabric;
using Corvid.Kernel;

namespace Corvid.Host.Telemetry;

/// <summary>What a surface hears about a session's running turn. Calls can arrive from several threads at once.</summary>
public interface ITurnObserver
{
    /// <summary>A generator call started; text that follows belongs to it.</summary>
    void GenerationStarted() { }

    /// <summary>Text the generator has produced so far in this call (a delta).</summary>
    void Text(string delta) { }

    /// <summary>Reasoning the generator streamed in this call (a delta of its <c>TextReasoningContent</c>, K-1).</summary>
    void Thought(string delta) { }

    /// <summary>
    /// The generator call finished, with the tokens its provider reported (K-5, as far as the call itself tells): the
    /// prompt's and the completion's; null when the provider did not say.
    /// </summary>
    void Usage(long? inputTokens, long? outputTokens) { }

    /// <summary>
    /// A5 review (L3): the generator call finished and its provider reported no usage, so these are the token counter's
    /// estimate of the request and the response. Surfaces that show only reported usage ignore it; a child's token budget
    /// counts it.
    /// </summary>
    void UsageEstimated(long inputTokens, long outputTokens) { }

    void ToolStarted(string callId, string tool, bool approval) { }

    /// <summary>
    /// A7: a call started, with the call it was made from when it is nested (code mode's script calling a tool). The feed
    /// calls this one; by default it forwards to the overload without the parent.
    /// </summary>
    void ToolStarted(string callId, string tool, bool approval, string? parentCallId) => ToolStarted(callId, tool, approval);

    /// <param name="outcome"><c>succeeded</c>, <c>failed</c>, <c>invalid_arguments</c>, <c>denied</c> or <c>cancelled</c>.</param>
    void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed) { }

    void Decided(CheckpointDecisions decisions) { }

    /// <summary>The turn began step <paramref name="step"/> of at most <paramref name="maxSteps"/> (a generator response with tool calls).</summary>
    void Progress(int step, int maxSteps) { }

    /// <summary>
    /// K-2: a running call's output so far as its last lines (bounded and credential-scrubbed by the kernel), replacing the
    /// previous tail. Untrusted text: clean it before showing it.
    /// </summary>
    void ToolOutput(string callId, string tool, string tail) { }
}

public sealed record QuestionDecision(string Question, double Confidence, string Action, bool Acted);

public sealed record CheckpointDecisions(string Checkpoint, string Decider, ImmutableArray<QuestionDecision> Questions);

/// <summary>
/// Turns the spans the kernel and fabric already emit into per-session events for surfaces: the CLI's live tool panel and
/// streamed text read the same <c>execute_tool</c> and <c>corvid.decide</c> spans that are exported, rather than a
/// second reporting path through the kernel. Its listener also makes sure those spans exist when nothing exports them.
/// </summary>
public sealed class TurnFeed : IDisposable
{
    private readonly ConcurrentDictionary<string, ImmutableArray<ITurnObserver>> _observers = new(StringComparer.Ordinal);
    private readonly ActivityListener _listener;

    public TurnFeed()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is KernelTelemetry.SourceName or FabricTelemetry.SourceName or HostTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = Started,
            ActivityStopped = Stopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IDisposable Subscribe(string sessionId, ITurnObserver observer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(observer);
        _observers.AddOrUpdate(sessionId, [observer], (_, list) => list.Add(observer));
        return new Subscription(this, sessionId, observer);
    }

    public bool HasObservers(string sessionId) => _observers.TryGetValue(sessionId, out var list) && !list.IsEmpty;

    internal void Publish(string sessionId, Action<ITurnObserver> notify)
    {
        if (!_observers.TryGetValue(sessionId, out var list))
            return;
        foreach (var observer in list)
        {
            try
            {
                notify(observer);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A surface that fails to render must not fail the turn; the answer is still delivered and logged.
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>The kernel's turn listener over this feed: steps and running calls' tails reach the session's observers.</summary>
    internal Corvid.Kernel.Turns.ITurnListener Listener => _turnListener ??= new FeedListener(this);

    private Corvid.Kernel.Turns.ITurnListener? _turnListener;

    private sealed class FeedListener(TurnFeed feed) : Corvid.Kernel.Turns.ITurnListener
    {
        public void StepStarted(string sessionId, int step, int maxSteps) => feed.Publish(sessionId, observer => observer.Progress(step, maxSteps));

        public void ToolOutput(string sessionId, string callId, string tool, string tail) =>
            feed.Publish(sessionId, observer => observer.ToolOutput(callId, tool, tail));
    }

    private void Started(Activity activity)
    {
        if (activity.Source.Name == KernelTelemetry.SourceName && IsTool(activity, out var session, out var callId, out var tool))
            Publish(session, observer => observer.ToolStarted(callId, tool, activity.GetTagItem(KernelTelemetry.ToolApproval) is true,
                activity.GetTagItem(KernelTelemetry.ToolParentCallId) as string));
    }

    private void Stopped(Activity activity)
    {
        if (activity.Source.Name == KernelTelemetry.SourceName && IsTool(activity, out var session, out var callId, out var tool))
        {
            var outcome = activity.GetTagItem(KernelTelemetry.ToolOutcome) as string ?? "failed";
            Publish(session, observer => observer.ToolFinished(callId, tool, outcome, activity.Duration));
        }
        else if (activity.Source.Name == FabricTelemetry.SourceName && HostTelemetry.SessionOf(activity) is { } decided
                 && activity.GetTagItem("corvid.decide.questions") is string[] questions)
        {
            var confidences = activity.GetTagItem("corvid.decide.confidences") as double[] ?? [];
            var actions = activity.GetTagItem("corvid.decide.actions") as string[] ?? [];
            var acted = activity.GetTagItem("corvid.decide.acted") as bool[] ?? [];
            var decisions = new CheckpointDecisions(activity.GetTagItem("corvid.checkpoint") as string ?? "",
                activity.GetTagItem("corvid.decider") as string ?? "",
                [.. questions.Select((question, i) => new QuestionDecision(question, i < confidences.Length ? confidences[i] : 0,
                    i < actions.Length ? actions[i] : "", i < acted.Length && acted[i]))]);
            Publish(decided, observer => observer.Decided(decisions));
        }
    }

    private static bool IsTool(Activity activity, out string session, out string callId, out string tool)
    {
        session = activity.GetTagItem(KernelTelemetry.ConversationId) as string ?? "";
        callId = activity.GetTagItem(KernelTelemetry.ToolCallId) as string ?? "";
        tool = activity.GetTagItem(KernelTelemetry.ToolName) as string ?? "";
        return activity.GetTagItem(KernelTelemetry.OperationName) is "execute_tool" && session.Length > 0;
    }

    private sealed class Subscription(TurnFeed feed, string sessionId, ITurnObserver observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                feed._observers.AddOrUpdate(sessionId, [], (_, list) => list.Remove(observer));
        }
    }
}
