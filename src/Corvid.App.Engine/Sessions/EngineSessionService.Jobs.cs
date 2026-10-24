using Corvid.App.Core.Composition;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;

namespace Corvid.App.Engine.Sessions;

public sealed partial class EngineSessionService
{
    private Action? _unwatchJobs;

    /// <summary>
    /// Once the runtime is up, hears the scheduler's runs: a run's session is new to this window (the scheduler opened it,
    /// not the window), so its summary is raised and the rail, the Board and the tray's "Open run" can reach it (F13). Not
    /// tracked as work: a runtime that never starts would keep <see cref="IdleAsync"/> waiting.
    /// </summary>
    private async Task WatchJobsAsync()
    {
        CorvidRuntime runtime;
        try
        {
            runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        }
        catch (ServiceUnavailableException)
        {
            // Nothing runs jobs without a runtime; the Jobs page says why.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log("corvid app: job runs will not reach the session list: " + exception.Message);
            return;
        }

        void OnCompleted(ScheduledJob job, JobRun run)
        {
            if (!_disposed && run.SessionId is { Length: > 0 } id)
                Refresh(runtime, id);
        }

        runtime.JobCompleted += OnCompleted;
        _unwatchJobs = () => runtime.JobCompleted -= OnCompleted;
        // Disposed while the runtime was starting: let go at once.
        if (_disposed)
            UnwatchJobs();
    }

    private void UnwatchJobs() => Interlocked.Exchange(ref _unwatchJobs, null)?.Invoke();
}
