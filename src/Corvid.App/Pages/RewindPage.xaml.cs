using Corvid.App.Core.Composition;
using Corvid.App.Core.Rewind;

namespace Corvid.App.Pages;

/// <summary>The Rewind area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class RewindPage : Page
{
    public RewindPage() => InitializeComponent();

    public RewindViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Rewind;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
