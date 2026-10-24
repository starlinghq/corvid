using Corvid.App.Core.Jobs;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private JobsViewModel? _jobs;

    /// <summary>The Jobs area: scheduled jobs and the scheduler.</summary>
    public JobsViewModel Jobs => _jobs ??= new JobsViewModel();
}
