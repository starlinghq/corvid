using Corvid.App.Core.Composition;
using Corvid.App.Core.Secrets;

namespace Corvid.App.Pages;

/// <summary>The Secrets area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class SecretsPage : Page
{
    public SecretsPage() => InitializeComponent();

    public SecretsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Secrets;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
