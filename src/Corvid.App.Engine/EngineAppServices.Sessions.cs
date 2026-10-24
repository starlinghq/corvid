using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Sessions over the runtime's event log and turns (src/Corvid.App.Engine/Sessions/). A3 core built the path end to
    /// end (list, open with replay and live updates, send); A3 sessions builds the rest of <see cref="ISessionService"/>.
    /// The composition makes this first, so the status bar shows the runtime's start from here.
    /// </summary>
    public override ISessionService CreateSessionService(AppComposition app)
    {
        ShowStartup(app);
        return new EngineSessionService(Engine, app.Dispatcher, app.Time);
    }
}
