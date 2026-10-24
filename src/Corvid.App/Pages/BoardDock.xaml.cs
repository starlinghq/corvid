using Corvid.App.Controls;
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

    public static ButtonVariant Variant(bool ghost) => ghost ? ButtonVariant.Ghost : ButtonVariant.Default;

    /// <summary>A link's label is the accent (the mockup's <c>a.btn</c>), a refusal's red (<c>.btn.danger</c>), the rest text.</summary>
    public static string LabelKey(bool link, bool danger) => danger ? "CorvidRed" : link ? "CorvidAccent" : "CorvidText";
}
