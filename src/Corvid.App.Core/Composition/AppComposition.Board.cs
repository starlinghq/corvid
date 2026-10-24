using Corvid.App.Core.Board;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private BoardViewModel? _board;

    /// <summary>The Board area: every session as a card, and the attention tray.</summary>
    public BoardViewModel Board => _board ??= new BoardViewModel();
}
