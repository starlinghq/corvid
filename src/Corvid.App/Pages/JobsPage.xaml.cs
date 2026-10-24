using Corvid.App.Core.Composition;
using Corvid.App.Core.Jobs;

namespace Corvid.App.Pages;

/// <summary>The Jobs area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class JobsPage : Page
{
    public JobsPage() => InitializeComponent();

    public JobsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Jobs;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
