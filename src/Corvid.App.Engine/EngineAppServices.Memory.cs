using Corvid.App.Core.Composition;
using Corvid.App.Core.Memory;
using Corvid.App.Engine.Memory;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Memory over the runtime's memory store: staged writes, blocks, facts, lessons, the Hermes import (src/Corvid.App.Engine/Memory/).</summary>
    public override IMemoryService CreateMemoryService(AppComposition app) => new EngineMemoryService(Engine, app.Time, () => Hermes(), app.Dispatcher);

    /// <summary>The Memory page's engine line (generator, decider, scheduler) once the runtime is up; null while it starts or when it failed.</summary>
    public override MemoryEngineStatus? CreateMemoryStatus(AppComposition app) => EngineMemoryService.Status(Engine);
}
