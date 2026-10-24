using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>
    /// Sessions over the runtime's event log and turns (src/Corvid.App.Engine/Sessions/). A3 core built the path end to
    /// end (list, open with replay and live updates, send); A3 sessions builds the rest of <see cref="ISessionService"/>.
    /// </summary>
    public override ISessionService CreateSessionService(AppComposition app) => new EngineSessionService(Engine, app.Dispatcher, app.Time);
}
