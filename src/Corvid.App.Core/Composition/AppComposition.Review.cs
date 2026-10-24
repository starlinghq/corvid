using Corvid.App.Core.Review;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private ReviewViewModel? _review;

    /// <summary>The Review area: the changes, with line comments.</summary>
    public ReviewViewModel Review => _review ??= new ReviewViewModel();
}
