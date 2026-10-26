namespace Corvid.Host.Composition;

/// <summary>The block rewrite job's background loop (plan §9.5): woken after each turn, run only when every session is idle.</summary>
public sealed partial class CorvidRuntime
{
    private Task? _consolidating;

    /// <summary>
    /// Starts the sleep-time loop when there is a consolidator and <c>Corvid:Memory:Consolidation:Background</c> is on. It
    /// stages rewrites for review in <see cref="Abstractions.Events.MemoryJobs.Session"/>; nothing is applied unapproved.
    /// </summary>
    private void StartConsolidation()
    {
        if (Memory?.Consolidator is { } job && Options.Memory.Consolidation.Background)
            _consolidating = Task.Run(() => job.RunAsync(SessionsIdle, _lifetime.Token));
    }

    /// <summary>No session open in this process is running a turn, holding messages or doing exclusive work.</summary>
    internal bool SessionsIdle() => _sessions.Values.All(session => session.Actor.IsIdle);

    /// <summary>A turn ended: the loop looks again once the sessions are idle.</summary>
    private void NudgeConsolidation()
    {
        if (_consolidating is not null)
            Memory?.Consolidator?.Nudge();
    }

    private Task ConsolidationStopped() => _consolidating ?? Task.CompletedTask;
}
