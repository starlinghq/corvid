using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SkillsViewModel? _skills;

    /// <summary>The Skills area: the skills library and drafts.</summary>
    public SkillsViewModel Skills => _skills ??= new SkillsViewModel();
}
