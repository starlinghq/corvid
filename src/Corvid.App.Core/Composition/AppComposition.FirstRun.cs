using Corvid.App.Core.FirstRun;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IFirstRunService? _firstRunService;
    private FirstRunViewModel? _firstRun;

    /// <summary>
    /// The doctor and the setup's choices. Demo mode answers for FirstRun.png's fresh machine when the window opens on the
    /// first run (<c>--page first-run</c>), and for the configured machine of the other mockups when Doctor opens it.
    /// </summary>
    public IFirstRunService FirstRunService => _firstRunService ??= new DemoFirstRunService(freshMachine: Options.Page == Area.FirstRun);

    /// <summary>
    /// The first run area: the doctor's checks and the setup. Opening the window on it is the setup, with its own column of
    /// steps; opened from the rail's Doctor, it is the checks alone. It shares <see cref="ModelService"/> with Models.
    /// </summary>
    public FirstRunViewModel FirstRun => _firstRun ??= new FirstRunViewModel(FirstRunService, ModelService, Shell, StatusBar,
        isSetup: Options.Page == Area.FirstRun);
}
