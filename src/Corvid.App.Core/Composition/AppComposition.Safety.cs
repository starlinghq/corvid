using Corvid.App.Core.Safety;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SafetyViewModel? _safety;

    /// <summary>The Safety area: modes, rules and detections.</summary>
    public SafetyViewModel Safety => _safety ??= new SafetyViewModel();
}
