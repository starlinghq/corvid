using Corvid.App.Core.Composition;
using Corvid.App.Core.Skills;

namespace Corvid.App.Pages;

/// <summary>The Skills area's page. Its view model comes from the composition the shell navigates with.</summary>
public sealed partial class SkillsPage : Page
{
    public SkillsPage() => InitializeComponent();

    public SkillsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Skills;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }
}
