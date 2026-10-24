using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SkillsViewModel? _skills;
    private ISkillsService? _skillsService;

    /// <summary>
    /// The skills store as the views see it. Demo mode (the only mode until Corvid.App.Engine exists) serves the scenario's
    /// library, one draft and the store's history on the frozen clock, in memory: it reads no folder and runs no git.
    /// <para>
    /// It is made as the composition starts, and counts the drafts waiting for the rail's Skills badge then, so the badge
    /// shows a waiting draft before the page is first visited; from then on the view model keeps it.
    /// </para>
    /// </summary>
    public ISkillsService SkillsService
    {
        get
        {
            if (_skillsService is not null)
                return _skillsService;
            _skillsService = new DemoSkillsService(Time);
            _ = CountSkillDraftsAsync(_skillsService);
            return _skillsService;
        }
    }

    /// <summary>
    /// The Skills area: drafts to review, the library, history and import. Created on first use; from then on it keeps the
    /// rail's Skills badge to the count of drafts, and holds the status bar while the page shows. It starts no timer;
    /// disposing the composition disposes it.
    /// </summary>
    public SkillsViewModel Skills => _skills ??= new SkillsViewModel(SkillsService, Time, Shell, StatusBar);

    /// <summary>The drafts waiting at start-up, for the badge; the view model's own count wins once it exists.</summary>
    private async Task CountSkillDraftsAsync(ISkillsService service)
    {
        try
        {
            var snapshot = await service.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_skills is null)
                    Shell.SetBadge(Area.Skills, snapshot.Drafts.Count);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No badge yet; the page says what failed when it is opened.
        }
    }
}
