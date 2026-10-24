namespace Corvid.App.Core.Jobs;

/// <summary>
/// The Jobs area's seam to the engine (docs/plan/ui.md §4.10, F13): the durable scheduler's jobs and runs, the schedule
/// grammar's preview, and the person's own pause, resume and remove (the user acting directly, so no approval step, as
/// <c>/cron</c>). The demo implements it now; <c>Corvid.App.Engine</c> will over the job store and G-13's create API.
/// </summary>
public interface IJobsService
{
    Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>A job's runs, newest first, at most <see cref="JobsText.RunsShown"/>.</summary>
    Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Reads a schedule as the scheduler would and lists its next runs; nothing is written.</summary>
    Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken);

    /// <summary>Creates a job from the form (G-13). Throws <see cref="InvalidOperationException"/> with the reason when refused.</summary>
    Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken);

    Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken);

    Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Removes a job: it will not run again; its runs stay recorded.</summary>
    Task RemoveAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>A job, a run or the scheduler changed. Raised on the UI thread.</summary>
    event EventHandler? Changed;
}
