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
        var list = runs.ToList();
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].IsCode && !list[i - 1].IsCode && !list[i - 1].IsLink && Glue(list[i - 1].Text, list[i].Text) is var (before, code)
                && before.Length != list[i - 1].Text.Length)
            {
                list[i - 1] = list[i - 1] with { Text = before };
                list[i] = list[i] with { Text = code };
            }
        }

        foreach (var run in list)
            inlines.Add(Inline(run, owner));
    }

    /// <summary>
    /// Uno may break a line where one run meets the next, so prose ending in an opening bracket or quote right before a
    /// code span ("value (" then <c>[secret:github.token]</c>) could leave the bracket alone at a line's end. The bracket
    /// moves into the code span's run, so the two wrap together.
    /// </summary>
    public static (string Before, string Code) Glue(string before, string code)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(code);
        return before.Length > 0 && code.Length > 0 && before[^1] is '(' or '[' or '{' or '“' or '‘' or '"' or '\''
            ? (before[..^1], string.Concat(before.AsSpan(before.Length - 1), code))
            : (before, code);
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
        if (!run.IsLink || !Uri.TryCreate(run.Url, UriKind.Absolute, out var uri))
            return text;
        var link = new Hyperlink { NavigateUri = uri, Foreground = ThemeBrushes.Get(owner, "CorvidAccent"), UnderlineStyle = UnderlineStyle.None };
        link.Inlines.Add(text);
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
