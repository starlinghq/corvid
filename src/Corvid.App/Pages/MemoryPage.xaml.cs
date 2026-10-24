using Corvid.App.Core.Composition;
using Corvid.App.Core.Memory;

namespace Corvid.App.Pages;

/// <summary>The Memory area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class MemoryPage : Page
{
    public MemoryPage() => InitializeComponent();

    public MemoryViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Memory;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
