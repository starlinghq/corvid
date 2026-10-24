using Corvid.App.Core.Composition;
using Corvid.App.Core.Mcp;

namespace Corvid.App.Pages;

/// <summary>The Mcp area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class McpPage : Page
{
    public McpPage() => InitializeComponent();

    public McpViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Mcp;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
