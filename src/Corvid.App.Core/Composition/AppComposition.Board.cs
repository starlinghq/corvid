using Corvid.App.Core.Board;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IBoardService? _boardService;
    private BoardViewModel? _board;

    /// <summary>
    /// The Board's service: the other areas' attention items and the engine's state. Demo mode (the only mode until
    /// Corvid.App.Engine exists) serves Board.dc.html's moment on the frozen clock.
    /// </summary>
    public IBoardService BoardService => _boardService ??= new DemoBoardService(Time);

    /// <summary>The Board area: every session as a card, and the attention tray.</summary>
    public BoardViewModel Board => _board ??= new BoardViewModel(Shell, SessionHub, BoardService, new SessionServiceWatcher(SessionService), Time, Dispatcher);
}
