using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Sessions over the runtime's event log and turns (src/Corvid.App.Engine/Sessions/): list, open with replay and live
    /// updates, send and the queue, steering (K-3), interrupt, approval cards, clarifying questions (K-6), slash commands,
    /// goal, mode, compaction and fork; rename (G-6) is refused with what brings it. The composition makes this first, so the
    /// status bar shows the runtime's start from here.
    /// </summary>
    public override ISessionService CreateSessionService(AppComposition app)
    {
        ShowStartup(app);
        return new EngineSessionService(Engine, app.Dispatcher, app.Time);
    }
}
