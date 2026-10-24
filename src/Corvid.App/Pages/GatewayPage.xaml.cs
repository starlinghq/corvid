using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;

namespace Corvid.App.Pages;

/// <summary>The Gateway area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class GatewayPage : Page
{
    public GatewayPage() => InitializeComponent();

    public GatewayViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Gateway;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
