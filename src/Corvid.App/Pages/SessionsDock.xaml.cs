using Corvid.App.Core.Sessions;

namespace Corvid.App.Pages;

/// <summary>The session's dock panels (Main.dc.html <c>aside.dock</c>): the tabs, and the selected one's body.</summary>
public sealed partial class SessionsDock : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SessionsViewModel), typeof(SessionsDock), new PropertyMetadata(null));

    public SessionsDock() => InitializeComponent();

    public SessionsViewModel ViewModel
    {
        get => (SessionsViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
