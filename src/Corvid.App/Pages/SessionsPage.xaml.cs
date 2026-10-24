using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;
using Corvid.App.Shell;

namespace Corvid.App.Pages;

/// <summary>The Sessions area's page, which also puts the session's panels in the shell's dock.</summary>
public sealed partial class SessionsPage : Page, IDockProvider
{
    // Made with the page: the frame raises Navigated, where the shell takes the dock, before OnNavigatedTo gives the page
    // its view model, so the dock gets the view model there.
    private readonly SessionsDock _dock = new();

    public SessionsPage() => InitializeComponent();

    public SessionsViewModel ViewModel { get; private set; } = null!;

    public UIElement? Dock => _dock;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Sessions;
        _dock.ViewModel = ViewModel;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
