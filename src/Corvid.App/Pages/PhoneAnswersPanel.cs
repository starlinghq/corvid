using Corvid.App.Core.Sessions;
using Windows.Foundation;

namespace Corvid.App.Pages;

/// <summary>
/// The phone's approval answers (Phone.png): each answer on its own full-width line, except the denials, which share
/// one line in equal halves ("Deny" and "Deny and say why"). An item is a denial when its data is an
/// <see cref="ApprovalOptionView"/> whose <see cref="ApprovalOptionView.IsDeny"/> is true; consecutive denials share a line.
/// </summary>
public sealed partial class PhoneAnswersPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(PhoneAnswersPanel), new PropertyMetadata(6.0, (d, _) => ((PhoneAnswersPanel)d).InvalidateMeasure()));

    /// <summary>The gap between lines and between the halves of the denials' line.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        var height = 0.0;
        foreach (var line in Lines())
        {
            var cell = Math.Max(0, (width - Spacing * (line.Count - 1)) / line.Count);
            var tallest = 0.0;
            foreach (var child in line)
            {
                child.Measure(new Size(cell, double.PositiveInfinity));
                tallest = Math.Max(tallest, child.DesiredSize.Height);
            }

            height += (height > 0 ? Spacing : 0) + tallest;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (var line in Lines())
        {
            var cell = Math.Max(0, (finalSize.Width - Spacing * (line.Count - 1)) / line.Count);
            var tallest = line.Max(child => child.DesiredSize.Height);
            for (var i = 0; i < line.Count; i++)
                line[i].Arrange(new Rect(i * (cell + Spacing), y, cell, tallest));
            y += tallest + Spacing;
        }

        return finalSize;
    }

    private List<List<UIElement>> Lines()
    {
        var lines = new List<List<UIElement>>();
        var denying = false;
        foreach (var child in Children)
        {
            var deny = child is FrameworkElement { DataContext: ApprovalOptionView { IsDeny: true } };
            if (deny && denying)
                lines[^1].Add(child);
            else
                lines.Add([child]);
            denying = deny;
        }

        return lines;
    }
}
