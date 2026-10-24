using Corvid.App.Core.Memory;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private MemoryViewModel? _memory;
    private IMemoryService? _memoryService;

    /// <summary>
    /// The memory store as the views see it. Demo mode (the only mode until Corvid.App.Engine exists) serves Memory.png's
    /// moment on the frozen clock.
    /// </summary>
    public IMemoryService MemoryService => _memoryService ??= new DemoMemoryService(Time);

    /// <summary>
    /// The Memory area: staged writes, core blocks, facts and lessons. Created on first use; from then on it keeps the
    /// rail's Memory badge to the count of staged writes, and sets the status bar whenever the page shows.
    /// </summary>
    public MemoryViewModel Memory => _memory ??= new MemoryViewModel(MemoryService, Time, Shell, StatusBar, MemoryEngineStatus.Demo);
}
