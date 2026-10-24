using Corvid.App.Core.Composition;
using Corvid.App.Core.Audit;

namespace Corvid.App.Pages;

/// <summary>The Audit area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class AuditPage : Page
{
    public AuditPage() => InitializeComponent();

    public AuditViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Audit;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
