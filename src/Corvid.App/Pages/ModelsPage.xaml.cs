using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;

namespace Corvid.App.Pages;

/// <summary>The Models area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class ModelsPage : Page
{
    public ModelsPage() => InitializeComponent();

    public ModelsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Models;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
