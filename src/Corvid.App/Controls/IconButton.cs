using Corvid.App.Core;

namespace Corvid.App.Controls;

/// <summary>
/// A 28 px icon-only button (Main.dc.html <c>.iconbtn</c>): muted until hovered, with an optional amber count in its
/// corner (the attention bell). It has no text, so every use sets AutomationProperties.Name.
/// </summary>
public sealed partial class IconButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconKind), typeof(IconButton), new PropertyMetadata(IconKind.None));

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(string), typeof(IconButton), new PropertyMetadata("", (d, _) => ((IconButton)d).UpdateStates(true)));

    public IconButton() => DefaultStyleKey = typeof(IconButton);

    public IconKind Icon
    {
        get => (IconKind)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The count drawn in the corner; empty hides it.</summary>
    public string Count
    {
        get => (string)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates(false);
    }

    private void UpdateStates(bool useTransitions) =>
        VisualStateManager.GoToState(this, string.IsNullOrEmpty(Count) ? "NoCount" : "HasCount", useTransitions);
}
