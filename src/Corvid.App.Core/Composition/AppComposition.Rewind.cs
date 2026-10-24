using Corvid.App.Core.Rewind;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private RewindViewModel? _rewind;

    /// <summary>The Rewind area: the checkpoint timeline of the selected session.</summary>
    public RewindViewModel Rewind => _rewind ??= new RewindViewModel(Shell);
}
