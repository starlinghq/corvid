using Corvid.App.Core.Board;

namespace Corvid.App.Pages;

/// <summary>The Board's grid holds session cards and, last, the dashed card that starts another session.</summary>
public sealed partial class BoardTileTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Card { get; set; }

    public DataTemplate? NewSession { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is NewSessionTile ? NewSession : Card;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
