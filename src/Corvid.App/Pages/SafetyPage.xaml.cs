using Corvid.App.Core.Composition;
using Corvid.App.Core.Safety;

namespace Corvid.App.Pages;

/// <summary>The Safety area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class SafetyPage : Page
{
    public SafetyPage() => InitializeComponent();

    public SafetyViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Safety;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
