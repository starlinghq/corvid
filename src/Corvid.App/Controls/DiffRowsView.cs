using Corvid.App.Core.Diffs;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Controls;

/// <summary>
/// A file's change as unified rows (corvid.css <c>.dl</c>): the line number in a 38 px gutter, added lines on add-bg and
/// removed ones on del-bg, the changed words highlighted, folds as grey rows. Lines do not wrap; the rows scroll sideways.
/// </summary>
public sealed partial class DiffRowsView : UserControl
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<DiffRow>), typeof(DiffRowsView), new PropertyMetadata(null, (d, _) => ((DiffRowsView)d).Rebuild()));

    private readonly StackPanel _lines = new();

    public DiffRowsView()
    {
        IsTabStop = false;
        Content = new ScrollViewer
        {
            Content = _lines,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        };
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public IReadOnlyList<DiffRow>? Rows
    {
        get => (IReadOnlyList<DiffRow>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>Rows drawn at most: each is a few controls, built on the UI thread; the rest are counted in one row.</summary>
    public const int MaxRows = 400;

    private void Rebuild()
    {
        _lines.Children.Clear();
        var rows = Rows ?? [];
        foreach (var row in rows.Take(MaxRows))
            _lines.Children.Add(Line(row));
        if (rows.Count > MaxRows)
        {
            var more = rows.Skip(MaxRows).Sum(row => row.Kind == DiffRowKind.Skip ? 0 : 1);
            _lines.Children.Add(Line(new DiffRow(DiffRowKind.Skip, null, null, []),
                $"… {more.ToString(System.Globalization.CultureInfo.InvariantCulture)} more lines, not drawn here"));
        }
    }

    private Grid Line(DiffRow row, string? label = null)
    {
        var mono = (FontFamily)Application.Current.Resources["CorvidMono"];
        var line = new Grid { MinHeight = 22 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Background = row.Kind switch
        {
            DiffRowKind.Added => ThemeBrushes.Get(this, "CorvidAddBg"),
            DiffRowKind.Removed => ThemeBrushes.Get(this, "CorvidDelBg"),
            DiffRowKind.Skip => ThemeBrushes.Get(this, "CorvidBg3"),
            _ => null,
        };
        var number = row.Kind == DiffRowKind.Removed ? row.OldNumber : row.NewNumber ?? row.OldNumber;
        var gutter = new Border
        {
            BorderBrush = ThemeBrushes.Get(this, "CorvidLine"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                FontFamily = mono,
                FontSize = 12,
                Foreground = ThemeBrushes.Get(this, "CorvidText2"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        line.Children.Add(gutter);
        var text = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (row.Kind == DiffRowKind.Skip)
        {
            text.Children.Add(Segment(label ?? row.SkipText, mono, null, ThemeBrushes.Get(this, "CorvidText2")));
        }
        else
        {
            var word = row.Kind == DiffRowKind.Removed ? ThemeBrushes.Get(this, "CorvidDelWord") : ThemeBrushes.Get(this, "CorvidAddWord");
            foreach (var segment in row.Segments)
                text.Children.Add(Segment(segment.Text, mono, segment.Changed && row.Kind != DiffRowKind.Context ? word : null, ThemeBrushes.Get(this, "CorvidText")));
        }

        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        var sign = row.Kind switch { DiffRowKind.Added => "added", DiffRowKind.Removed => "removed", _ => "" };
        AutomationProperties.SetName(line, row.Kind == DiffRowKind.Skip ? label ?? row.SkipText : $"{sign} {number} {row.Text}".Trim());
        return line;
    }

    private static Border Segment(string text, FontFamily mono, Brush? highlight, Brush foreground) => new()
    {
        Background = highlight,
        CornerRadius = new CornerRadius(2),
        Child = new TextBlock
        {
            // Leading and trailing spaces keep their width as no-break spaces.
            Text = text.Replace(' ', ' ').Replace("\t", "    ", StringComparison.Ordinal),
            FontFamily = mono,
            FontSize = 12,
            Foreground = foreground,
            TextWrapping = TextWrapping.NoWrap,
        },
    };
}
