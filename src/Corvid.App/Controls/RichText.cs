using Corvid.App.Core.Markdown;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Controls;

/// <summary>
/// Inline runs from the core's markdown model in a <c>TextBlock</c>: <c>RichText.Runs="{x:Bind Why}"</c>. Code runs are
/// JetBrains Mono, bold is semibold, links are the accent colour and open in the browser (the model keeps only web and
/// mail addresses). The block's own colour and size apply to the rest.
/// </summary>
public static class RichText
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<MarkdownRun>), typeof(RichText), new PropertyMetadata(null, OnRunsChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(RichText), new PropertyMetadata(false));

    public static IReadOnlyList<MarkdownRun>? GetRuns(TextBlock element) => (IReadOnlyList<MarkdownRun>?)element.GetValue(RunsProperty);

    public static void SetRuns(TextBlock element, IReadOnlyList<MarkdownRun>? value) => element.SetValue(RunsProperty, value);

    /// <summary>Adds the runs to an inline collection, coloured for <paramref name="owner"/>'s theme.</summary>
    public static void Append(InlineCollection inlines, IEnumerable<MarkdownRun> runs, FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        ArgumentNullException.ThrowIfNull(runs);
        foreach (var run in runs)
            inlines.Add(Inline(run, owner));
    }

    private static Inline Inline(MarkdownRun run, FrameworkElement owner)
    {
        var text = new Run { Text = run.Text };
        if (run.IsCode)
        {
            text.FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"];
            text.FontSize = 12.5;
        }

        if (run.IsBold)
            text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        if (run.IsItalic)
            text.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (run.IsStrikethrough)
            text.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        // Only web and mail addresses open, whatever built the run: the parser already filters, this is the last gate.
        if (!run.IsLink || !Uri.TryCreate(run.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            return text;
        var link = new Hyperlink { NavigateUri = uri, Foreground = ThemeBrushes.Get(owner, "CorvidAccent"), UnderlineStyle = UnderlineStyle.None };
        link.Inlines.Add(text);
        // Link text can name one site and lead to another: when it does not show the host, the host follows it.
        var host = uri.Scheme == "mailto" ? uri.OriginalString["mailto:".Length..] : uri.Host;
        if (host.Length > 0 && !run.Text.Contains(host, StringComparison.OrdinalIgnoreCase))
            link.Inlines.Add(new Run { Text = $" ({host})" });
        return link;
    }

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
        if (GetRuns(block) is { } runs)
            Append(block.Inlines, runs, block);
    }
}
