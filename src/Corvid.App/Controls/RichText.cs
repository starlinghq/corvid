using Corvid.App.Core;
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
        var list = runs as IReadOnlyList<MarkdownRun> ?? [.. runs];
        for (var i = 0; i < list.Count; i++)
        {
            // A link whose text has several styles ([**bank**.example](...)) is several runs with one URL: one hyperlink.
            var end = i + 1;
            while (list[i].IsLink && end < list.Count && list[end].Url == list[i].Url)
                end++;
            if (Link(list, i, end, owner) is { } link)
            {
                inlines.Add(link);
                i = end - 1;
            }
            else
            {
                inlines.Add(Styled(list[i]));
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="start"/> to <paramref name="end"/> (one URL) as one hyperlink. Only web and mail addresses
    /// open, whatever built the runs: the parser already filters, this is the last gate. Link text can name one site and
    /// lead to another, so unless the text shows the host as a whole name, the host follows it: in punycode when it is an
    /// international name (a lookalike), as user@host for mail, and revealed (no hidden characters).
    /// </summary>
    private static Hyperlink? Link(IReadOnlyList<MarkdownRun> runs, int start, int end, FrameworkElement owner)
    {
        var first = runs[start];
        if (!first.IsLink || !Uri.TryCreate(first.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            return null;
        var link = new Hyperlink { NavigateUri = uri, Foreground = ThemeBrushes.Get(owner, "CorvidAccent"), UnderlineStyle = UnderlineStyle.None };
        for (var i = start; i < end; i++)
            link.Inlines.Add(Styled(runs[i]));
        var text = string.Concat(runs.Skip(start).Take(end - start).Select(run => run.Text));
        var host = uri.IdnHost;
        var shown = uri.Scheme == "mailto" ? $"{uri.UserInfo}@{host}" : host;
        var international = !string.Equals(uri.IdnHost, uri.Host, StringComparison.OrdinalIgnoreCase);
        if (shown.Length > 0 && (international || !NamesHost(text, uri.Host)))
            link.Inlines.Add(new Run { Text = $" ({SafeText.Reveal(shown)})" });
        return link;
    }

    /// <summary>The text names the host as a whole name: "docs.example.org/x" names docs.example.org, "mybank.com" does not name bank.com.</summary>
    internal static bool NamesHost(string text, string host)
    {
        if (host.Length == 0)
            return false;
        for (var at = text.IndexOf(host, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(host, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = at == 0 || text[at - 1] is ' ' or '/' or '@' or '.' or ':' or '(' or '<' or '\t' or '\n';
            var after = at + host.Length == text.Length || text[at + host.Length] is ' ' or '/' or ':' or '?' or '#' or ')' or '>' or ',' or '\t' or '\n';
            if (before && after)
                return true;
        }

        return false;
    }

    private static Run Styled(MarkdownRun run)
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
        return text;
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
