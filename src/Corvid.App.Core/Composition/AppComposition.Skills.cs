using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SkillsViewModel? _skills;
    private ISkillsService? _skillsService;

    /// <summary>
    /// The skills store as the views see it. Demo mode (the only mode until Corvid.App.Engine exists) serves the scenario's
    /// library, one draft and the store's history on the frozen clock, in memory: it reads no folder and runs no git.
    /// </summary>
    public ISkillsService SkillsService => _skillsService ??= new DemoSkillsService(Time);

    /// <summary>
    /// The Skills area: drafts to review, the library, history and import. Created on first use; from then on it keeps the
    /// rail's Skills badge to the count of drafts, and holds the status bar while the page shows. It starts no timer.
    /// </summary>
    public SkillsViewModel Skills => _skills ??= new SkillsViewModel(SkillsService, Time, Shell, StatusBar);
}
