using Corvid.App.Core.Composition;
using Corvid.App.Core.Review;
using Corvid.App.Engine.Review;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Review over the write ledger's changes, with comments sent through the window's sessions (A3 history).</summary>
    public override IReviewService CreateReviewService(AppComposition app) =>
        new EngineReviewService(Engine, app.SessionService, app.Time, History(app));
}
