using Corvid.App.Core.Models;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Pages;

/// <summary>
/// Runs from the core's <see cref="TextRun"/>s in a <c>TextBlock</c> (<c>local:SpanText.Runs="{x:Bind Detail}"</c>), for
/// the Models page's and the first run's lines that mix paths and commands into prose: code in JetBrains Mono at 12.5 px
/// (corvid.css <c>.mono</c>), bold in semibold, muted in the second text colour. Uno does not repaint a run's theme brush,
/// so the runs are rebuilt when the theme changes; unmarked runs take the block's own colour.
/// </summary>
public static class SpanText
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<TextRun>), typeof(SpanText), new PropertyMetadata(null, OnRunsChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(SpanText), new PropertyMetadata(false));

    public static IReadOnlyList<TextRun>? GetRuns(TextBlock element) => (IReadOnlyList<TextRun>?)element.GetValue(RunsProperty);

    public static void SetRuns(TextBlock element, IReadOnlyList<TextRun>? value) => element.SetValue(RunsProperty, value);

    private static void OnRunsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (TextBlock)d;
        Fill(block);
        if ((bool)block.GetValue(HookedProperty))
            return;
        block.SetValue(HookedProperty, true);
        block.ActualThemeChanged += (sender, _) => Fill((TextBlock)sender);
    }

    private static void Fill(TextBlock block)
    {
        block.Inlines.Clear();
        if (GetRuns(block) is not { } runs)
            return;
        foreach (var piece in runs)
        {
            var run = new Run { Text = piece.Text };
            if (piece.Code)
            {
                run.FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"];
                run.FontSize = 12.5;
            }

            if (piece.Bold)
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (piece.Muted)
                run.Foreground = ThemeBrushes.Get(block, "CorvidText2");
            block.Inlines.Add(run);
        }
    }
}
