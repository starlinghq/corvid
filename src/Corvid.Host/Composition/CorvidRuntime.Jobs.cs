using Corvid.Kernel.Jobs;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Composition;

/// <summary>
/// A6 (G-4): a scheduled job runs in the workspace of the session that created it (<see cref="ScheduledJob.OriginSession"/>):
/// its pre-run script in that workspace's sandbox, assessed by its approvals, and its agent turn with its tools. A job made
/// in a worktree never reads or writes the configured workspace instead; when that workspace is gone (a removed worktree),
/// the run is refused. A job with no origin (made on the Jobs page), or one whose creating session's log predates A6 and
/// records no workspace, runs in the configured workspace. Every run's session records where it ran.
/// </summary>
public sealed partial class CorvidRuntime
{
    /// <summary>
    /// The workspace <paramref name="job"/>'s runs work in. When it is not the configured one, the run's session
    /// <paramref name="runSession"/> records it, so its Review, Rewind and approvals are that workspace's too.
    /// </summary>
    /// <exception cref="WorkspaceUnavailableException">The creating session's workspace cannot be used now.</exception>
    internal WorkspaceServices JobWorkspace(ScheduledJob job, string runSession)
    {
        ArgumentNullException.ThrowIfNull(job);
        // A6 review: the run records its workspace whichever it is, the configured one included, so the run's session
        // resumed by a process started elsewhere still shows and rewinds the files it changed.
        if (job.OriginSession is not { Length: > 0 } origin || string.IsNullOrWhiteSpace(origin) || WorkspaceOf(origin) is { IsDefault: true })
            return BindWorkspace(runSession, DefaultWorkspace.Root);
        var recorded = WorkspaceOf(origin);
        var scope = WorkspaceServicesOf(origin);
        return BindWorkspace(runSession, scope.Root, recorded);
    }

    /// <summary>The scheduler's script runner: each job's script in its own workspace.</summary>
    internal IJobScriptRunner JobScripts() => new WorkspaceJobScripts(this);

    /// <summary>The scheduler's agent (before taint is recorded): each job's turn in its own workspace.</summary>
    internal IJobAgent JobAgent() => new WorkspaceJobAgent(this);

    /// <summary>Runs a job's pre-run script in the job's workspace (<see cref="JobWorkspace"/>).</summary>
    private sealed class WorkspaceJobScripts(CorvidRuntime runtime) : IJobScriptRunner
    {
        public Task<JobScriptResult> RunAsync(JobScriptRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (runtime.Builtin.Jobs.Get(request.JobId) is not { } job)
                return Task.FromResult(JobScriptResult.Refused("the job no longer exists"));
            WorkspaceServices scope;
            try
            {
                scope = runtime.JobWorkspace(job, request.SessionId);
            }
            catch (WorkspaceUnavailableException exception)
            {
                return Task.FromResult(JobScriptResult.Refused(exception.Message));
            }
            return new SandboxedScriptRunner(scope.Shell, scope.Approvals, runtime.Options.Approvals.Mode).RunAsync(request, cancellationToken);
        }
    }

    /// <summary>Runs a job's agent turn with the services of the job's workspace (<see cref="JobWorkspace"/>).</summary>
    private sealed class WorkspaceJobAgent(CorvidRuntime runtime) : IJobAgent
    {
        public Task<JobAgentResult> RunAsync(JobAgentRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            // Unavailable: the exception fails the run with its reason; nothing runs anywhere else.
            var scope = runtime.JobWorkspace(request.Job, request.SessionId);
            return new TurnJobAgent(sessionId => runtime.Services(runtime.NewSlots(sessionId), interactive: false, workspace: scope))
                .RunAsync(request, cancellationToken);
        }
    }
}
