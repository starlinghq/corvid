using System.Collections;
using Corvid.App.Controls;

namespace Corvid.App.Pages;

/// <summary>The attention tray's items, in the Board's dock and, on a phone, on the Board's page.</summary>
public sealed partial class AttentionList : UserControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(AttentionList), new PropertyMetadata(null, (d, _) => ((AttentionList)d).Bindings.Update()));

    public AttentionList() => InitializeComponent();

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static ButtonVariant Variant(bool ghost) => ghost ? ButtonVariant.Ghost : ButtonVariant.Default;

    /// <summary>A link's label is the accent (the mockup's <c>a.btn</c>), a refusal's red (<c>.btn.danger</c>), the rest text.</summary>
    public static string LabelKey(bool link, bool danger) => danger ? "CorvidRed" : link ? "CorvidAccent" : "CorvidText";
}
