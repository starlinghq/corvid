using Corvid.App.Core.Memory;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private MemoryViewModel? _memory;

    /// <summary>The Memory area: staged writes, core blocks, facts and lessons.</summary>
    public MemoryViewModel Memory => _memory ??= new MemoryViewModel();
}
