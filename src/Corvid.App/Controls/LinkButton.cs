namespace Corvid.App.Controls;

/// <summary>
/// A text link that does something in the app (the rail footer's Settings and Doctor; "what left the machine"): the
/// accent colour, underlined on hover as the mockups' links are. A button underneath, so Enter and Space work and a
/// screen reader announces it with its text.
/// </summary>
public sealed partial class LinkButton : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LinkButton), new PropertyMetadata(""));

    public LinkButton() => DefaultStyleKey = typeof(LinkButton);

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
