using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IBoardService? _boardService;
    private BoardViewModel? _board;
    private int _bellGeneration;

    /// <summary>
    /// The Board's service: the other areas' attention items and the engine's state. Demo mode (the only mode until
    /// Corvid.App.Engine exists) serves Board.dc.html's moment on the frozen clock.
    /// <para>
    /// From the moment it exists, the bell counts its items whichever page answers them (MCP accepting a tool, Gateway
    /// pairing), not only once the Board's view model is made.
    /// </para>
    /// </summary>
    public IBoardService BoardService
    {
        get
        {
            if (_boardService is not null)
                return _boardService;
            _boardService = new DemoBoardService(Time);
            _boardService.AttentionChanged += OnBoardAttentionChanged;
            _ = CountBoardAttentionAsync();
            return _boardService;
        }
    }

    /// <summary>The Board area: every session as a card, and the attention tray.</summary>
    public BoardViewModel Board => _board ??= new BoardViewModel(Shell, SessionHub, BoardService, new SessionServiceWatcher(SessionService), Time, Dispatcher);

    private void OnBoardAttentionChanged(object? sender, EventArgs e) => _ = CountBoardAttentionAsync();

    /// <summary>Reports the service's items to the bell; a later count wins over an earlier one still in flight.</summary>
    private async Task CountBoardAttentionAsync()
    {
        if (_boardService is not { } service)
            return;
        var generation = ++_bellGeneration;
        try
        {
            var items = await service.ListAttentionAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation == _bellGeneration && _boardService is not null)
                    Shell.SetAttention(DemoShell.AreasAttention, items.Count);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The bell keeps its last count; the Board's tray says what failed when it is opened.
        }
    }

    private void DisposeBoard()
    {
        _board?.Dispose();
        if (_boardService is not null)
            _boardService.AttentionChanged -= OnBoardAttentionChanged;
    }
}
