using Corvid.App.Core.Memory;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Pages;

/// <summary>
/// The Memory page's mixed lines as runs in one wrapping <c>TextBlock</c>: <c>local:MemorySpans.Spans="{x:Bind Statement}"</c>.
/// Bold is semibold, muted is the second text colour, small is 12 px, code is JetBrains Mono at 12.5 px. Uno does not
/// repaint a run's theme brush, so the runs are rebuilt when the theme changes.
/// </summary>
public static class MemorySpans
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(IReadOnlyList<MemorySpan>), typeof(MemorySpans), new PropertyMetadata(null, OnSpansChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(MemorySpans), new PropertyMetadata(false));

    public static IReadOnlyList<MemorySpan>? GetSpans(TextBlock element) => (IReadOnlyList<MemorySpan>?)element.GetValue(SpansProperty);

    public static void SetSpans(TextBlock element, IReadOnlyList<MemorySpan>? value) => element.SetValue(SpansProperty, value);

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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
        if (GetSpans(block) is not { } spans)
            return;
        foreach (var span in spans)
        {
            var run = new Run { Text = span.Text };
            if (span.IsBold)
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (span.IsCode)
            {
                run.FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"];
                run.FontSize = 12.5;
                // JetBrains Mono is embedded at 400 and 500 only; a heading's code takes the heavier of the two.
                if (span.IsBold || block.FontWeight.Weight >= 600)
                    run.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
            }

            if (span.IsSmall)
                run.FontSize = 12;
            if (span.IsMuted)
                run.Foreground = ThemeBrushes.Get(block, "CorvidText2");
            block.Inlines.Add(run);
        }
    }
}
