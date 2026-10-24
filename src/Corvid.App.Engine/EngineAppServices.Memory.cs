using Corvid.App.Core.Composition;
using Corvid.App.Core.Memory;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Memory: refused until A3 knowledge builds it (src/Corvid.App.Engine/Memory/).</summary>
    public override IMemoryService CreateMemoryService(AppComposition app) =>
        new Unavailable.Memory(Unavailable.ComesWith("knowledge", "the memory store"));

    /// <summary>No engine line on the Memory page's status bar until the knowledge group reads it; the session's shows.</summary>
    public override MemoryEngineStatus? CreateMemoryStatus(AppComposition app) => null;
}
