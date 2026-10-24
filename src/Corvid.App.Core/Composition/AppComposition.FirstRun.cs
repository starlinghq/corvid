using Corvid.App.Core.FirstRun;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private FirstRunViewModel? _firstRun;

    /// <summary>The first run area: the doctor's checks and setup.</summary>
    public FirstRunViewModel FirstRun => _firstRun ??= new FirstRunViewModel();
}
