using Corvid.App.Core.Shell;

namespace Corvid.App.Shell;

/// <summary>The rail (Main.dc.html <c>nav.rail</c>), drawn from the shell's view model; the shell shows it as a column or over the content.</summary>
public sealed partial class RailView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ShellViewModel), typeof(RailView), new PropertyMetadata(null));

    public RailView() => InitializeComponent();

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
