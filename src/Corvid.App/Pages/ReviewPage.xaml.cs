using Corvid.App.Core.Composition;
using Corvid.App.Core.Review;

namespace Corvid.App.Pages;

/// <summary>The Review area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class ReviewPage : Page
{
    public ReviewPage() => InitializeComponent();

    public ReviewViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Review;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
