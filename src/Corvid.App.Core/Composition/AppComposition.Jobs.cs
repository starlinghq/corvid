using Corvid.App.Core.Jobs;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IJobsService? _jobsService;
    private JobsViewModel? _jobs;

    /// <summary>
    /// The Jobs area's service: the scheduler, its jobs and their runs. Demo mode (the engine's comes from <see cref="Services"/>
    /// instead) serves two jobs on the frozen clock, nightly-tidy's run this morning being the demo's job session.
    /// </summary>
    public IJobsService JobsService => _jobsService ??= Services.CreateJobsService(this);

    /// <summary>The Jobs area: scheduled jobs, their runs, and the scheduler.</summary>
    public JobsViewModel Jobs => _jobs ??= new JobsViewModel(JobsService, Shell, StatusBar, Time, HostingService);
}
