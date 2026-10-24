using Corvid.App.Core.Board;

namespace Corvid.App.Pages;

/// <summary>The attention tray in the Board's dock (Board.dc.html <c>aside.tray</c>).</summary>
public sealed partial class BoardDock : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(BoardViewModel), typeof(BoardDock), new PropertyMetadata(null, (d, _) => ((BoardDock)d).Bindings.Update()));

    public BoardDock() => InitializeComponent();

    public BoardViewModel ViewModel
    {
        get => (BoardViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
