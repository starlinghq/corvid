using Corvid.App.Core.Audit;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Pages;

/// <summary>
/// The Audit page's text in a <c>TextBlock</c>, coloured for the element's theme: an event's summary spans (monospace for
/// commands, the accent for an in-app link), a line of a request as sent with each redacted value marked in amber, and a
/// cell's ink, red for a detection's row. Uno does not repaint a <c>Run</c>'s theme brush, nor a brush set from code, when
/// the theme changes, so each block rebuilds on <c>ActualThemeChanged</c>, as <c>RichText</c> does.
/// </summary>
public static class AuditRuns
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(IReadOnlyList<AuditSpan>), typeof(AuditRuns), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(AuditLine), typeof(AuditRuns), new PropertyMetadata(null, OnChanged));

    /// <summary>The block's colour: a corvid.css token (<c>CorvidText2</c>); <see cref="AlertProperty"/> overrides it.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.RegisterAttached(
        "Ink", typeof(string), typeof(AuditRuns), new PropertyMetadata(null, OnChanged));

    /// <summary>A detection's row: everything in red, links included.</summary>
    public static readonly DependencyProperty AlertProperty = DependencyProperty.RegisterAttached(
        "Alert", typeof(bool), typeof(AuditRuns), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(AuditRuns), new PropertyMetadata(false));

    public static IReadOnlyList<AuditSpan>? GetSpans(TextBlock element) => (IReadOnlyList<AuditSpan>?)element.GetValue(SpansProperty);

    public static void SetSpans(TextBlock element, IReadOnlyList<AuditSpan>? value) => element.SetValue(SpansProperty, value);

    public static AuditLine? GetLine(TextBlock element) => (AuditLine?)element.GetValue(LineProperty);

    public static void SetLine(TextBlock element, AuditLine? value) => element.SetValue(LineProperty, value);

    public static string? GetInk(TextBlock element) => (string?)element.GetValue(InkProperty);

    public static void SetInk(TextBlock element, string? value) => element.SetValue(InkProperty, value);

    public static bool GetAlert(TextBlock element) => (bool)element.GetValue(AlertProperty);

    public static void SetAlert(TextBlock element, bool value) => element.SetValue(AlertProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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
        var alert = GetAlert(block);
        if (alert)
            block.Foreground = ThemeBrushes.Get(block, "CorvidRed");
        else if (GetInk(block) is { Length: > 0 } ink)
            block.Foreground = ThemeBrushes.Get(block, ink);

        var spans = GetSpans(block);
        var line = GetLine(block);
        if (spans is null && line is null)
            return;
        block.Inlines.Clear();
        if (spans is not null)
        {
            foreach (var span in spans)
                block.Inlines.Add(Run(block, span, alert));
        }

        if (line is not null)
        {
            foreach (var segment in line.Segments)
            {
                var run = new Run { Text = segment.Text };
                if (segment.IsRedaction)
                {
                    // Marked, and never by colour alone: the marker's own words say what was taken out.
                    run.Foreground = ThemeBrushes.Get(block, "CorvidAmber");
                    run.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
                }

                block.Inlines.Add(run);
            }
        }
    }

    private static Run Run(TextBlock block, AuditSpan span, bool alert)
    {
        var run = new Run { Text = span.Text };
        switch (span.Style)
        {
            case AuditSpanStyle.Mono:
                run.FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"];
                run.FontSize = 12.5;
                break;
            case AuditSpanStyle.Link when !alert:
                run.Foreground = ThemeBrushes.Get(block, "CorvidAccent");
                break;
        }

        return run;
    }
}
