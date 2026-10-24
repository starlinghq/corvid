using Corvid.App.Core.Review;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    /// <summary>Review.png's two draft comments on SeccompTests.cs, written before the send.</summary>
    internal static readonly (DiffSide Side, int Line, string Text)[] ReviewSceneComments =
    [
        (DiffSide.New, 46, "Keep the control strict on CI: when `CI=true` and the sysctl is off, fail with a clear message instead of passing, so a misconfigured runner stays visible."),
        (DiffSide.New, 54, "Also treat `/proc/sys/user/max_user_namespaces == 0` as off. Arch sets that one, and the Debian sysctl is absent there."),
    ];

    private ReviewViewModel? _review;
    private IReviewService? _reviewService;

    /// <summary>Review's seam: the demo's over the demo sessions until Corvid.App.Engine brings the write ledger's.</summary>
    public IReviewService ReviewService => _reviewService ??= new DemoReviewService(SessionHub, Time);

    /// <summary>The Review area: the changes, with line comments.</summary>
    public ReviewViewModel Review => _review ??= CreateReview();

    private ReviewViewModel CreateReview()
    {
        var review = new ReviewViewModel(ReviewService, Shell, SessionHub, Time);
        if (Options.Demo && Shell.CurrentSession?.Id == DemoReviewService.MainSessionId)
        {
            // Review.png's moment: SeccompTests.cs open side by side with two draft comments.
            review.PreferFile(DemoFiles.SeccompTestsPath);
            foreach (var (side, line, text) in ReviewSceneComments)
                review.AddComment(DemoFiles.SeccompTestsPath, side, line, text);
        }

        return review;
    }
}
