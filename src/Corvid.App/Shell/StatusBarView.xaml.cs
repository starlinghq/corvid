using Corvid.App.Core.Shell;

namespace Corvid.App.Shell;

/// <summary>The status bar (Main.dc.html <c>footer.statusbar</c>), with the toggles that bring back a hidden rail or dock.</summary>
public sealed partial class StatusBarView : UserControl
{
    public static readonly DependencyProperty ShellProperty = DependencyProperty.Register(
        nameof(Shell), typeof(ShellViewModel), typeof(StatusBarView), new PropertyMetadata(null));

    public StatusBarView() => InitializeComponent();

    public ShellViewModel Shell
    {
        get => (ShellViewModel)GetValue(ShellProperty);
        set => SetValue(ShellProperty, value);
    }
}
