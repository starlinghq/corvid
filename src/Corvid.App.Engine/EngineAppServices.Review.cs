using Corvid.App.Core.Composition;
using Corvid.App.Core.Review;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Review: refused until A3 history builds it (src/Corvid.App.Engine/Review/).</summary>
    public override IReviewService CreateReviewService(AppComposition app) =>
        new Unavailable.Review(Unavailable.ComesWith("history", "Review of the write ledger's changes"));
}
