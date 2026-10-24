using Windows.Foundation;

namespace Corvid.App.Pages;

/// <summary>
/// The Jobs, MCP and Gateway pages' arrangement for the width, as the Models page arranges itself: the head on one line
/// when the title, its line and the buttons fit (CSS flex-wrap with space-between), the buttons under the title otherwise;
/// two columns (1.4fr and 1fr, 18 apart) when the page is wide enough for both, one column under
/// <see cref="TwoColumnWidth"/>; and tables that fill their panel and scroll sideways only when the panel is narrower
/// than their columns.
/// </summary>
internal static class OpsLayout
{
    public const double PhoneWidth = 720;

    /// <summary>Under this page width the right column goes under the left: their tables would otherwise be squeezed.</summary>
    public const double TwoColumnWidth = 900;

    public static void Arrange(double width, Border headHost, FrameworkElement title, FrameworkElement buttons, Panel body,
        Grid columns, ColumnDefinition right, FrameworkElement rightStack)
    {
        if (width <= 0)
            return;
        var phone = width < PhoneWidth;
        body.Margin = new Thickness(0);
        if (body is StackPanel stack)
            stack.Padding = phone ? new Thickness(14, 14, 14, 18) : new Thickness(24, 18, 24, 18);
        headHost.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);

        var available = width - headHost.Padding.Left - headHost.Padding.Right;
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var oneLine = title.DesiredSize.Width + 12 + buttons.DesiredSize.Width <= available;
        Grid.SetRow(buttons, oneLine ? 0 : 1);
        Grid.SetColumn(buttons, oneLine ? 1 : 0);
        buttons.HorizontalAlignment = oneLine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        buttons.Margin = oneLine ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        Grid.SetColumnSpan(title, oneLine ? 1 : 2);

        var two = width >= TwoColumnWidth;
        right.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        columns.ColumnSpacing = two ? 18 : 0;
        Grid.SetColumn(rightStack, two ? 1 : 0);
        Grid.SetRow(rightStack, two ? 0 : 1);
    }

    /// <summary>
    /// A table in a horizontal scroller fills the scroller and keeps at least its <c>MinWidth</c>: inside a horizontal
    /// scroller its content would otherwise be measured as wide as it likes. Hook it to the scroller's SizeChanged.
    /// </summary>
    public static void FitTable(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer { Content: FrameworkElement table } scroller && e.NewSize.Width > 0)
        {
            table.Width = Math.Max(table.MinWidth, e.NewSize.Width);
            // A table that scrolls sideways keeps a strip under its last row for the scroll bar.
            var room = new Thickness(0, 0, 0, table.MinWidth > e.NewSize.Width + 1 ? 8 : 0);
            if (scroller.Padding != room)
                scroller.Padding = room;
        }
    }

    /// <summary>
    /// Focuses a control that a change has just shown: now if it takes focus, otherwise after the next layout passes (at
    /// most a few). Uno refuses focus to a control that has not been laid out since it became visible.
    /// </summary>
    public static void FocusWhenShown(Control control, FocusState state)
    {
        if (control.Focus(state))
            return;
        var tries = 0;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (control.Focus(state) || ++tries >= 5)
                control.LayoutUpdated -= handler;
        };
        control.LayoutUpdated += handler;
    }
}
