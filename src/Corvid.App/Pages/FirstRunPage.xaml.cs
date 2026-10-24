using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;

namespace Corvid.App.Pages;

/// <summary>The FirstRun area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class FirstRunPage : Page
{
    public FirstRunPage() => InitializeComponent();

    public FirstRunViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).FirstRun;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
