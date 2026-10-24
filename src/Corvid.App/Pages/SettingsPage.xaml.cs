using Corvid.App.Core.Composition;
using Corvid.App.Core.Settings;

namespace Corvid.App.Pages;

/// <summary>The Settings area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    public SettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Settings;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
