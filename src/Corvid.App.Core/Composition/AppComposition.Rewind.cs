using Corvid.App.Core.Rewind;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private RewindViewModel? _rewind;
    private IRewindService? _rewindService;

    /// <summary>Rewind's seam: the demo's over the demo sessions, or the engine's over the runtime's ledger and rollback (<see cref="Services"/>).</summary>
    public IRewindService RewindService => _rewindService ??= Services.CreateRewindService(this);

    /// <summary>The Rewind area: the checkpoint timeline of the selected session.</summary>
    public RewindViewModel Rewind => _rewind ??= CreateRewind();

    private RewindViewModel CreateRewind()
    {
        var rewind = new RewindViewModel(RewindService, Shell, Time);
        // Rewind.png's moment: the 13:59 write of ci.yml selected, its plan open.
        if (Options.Demo)
            rewind.PreferEntry("c-38");
        return rewind;
    }
}
