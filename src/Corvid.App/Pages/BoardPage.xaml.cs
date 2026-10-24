using Corvid.App.Core.Composition;
using Corvid.App.Core.Board;
using Corvid.App.Shell;

namespace Corvid.App.Pages;

/// <summary>The Board's page, which puts the attention tray in the shell's dock.</summary>
public sealed partial class BoardPage : Page, IDockProvider
{
    private UIElement? _dock;

    public BoardPage() => InitializeComponent();

    public BoardViewModel ViewModel { get; private set; } = null!;

    public UIElement? Dock => _dock ??= new BoardDock();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Board;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
