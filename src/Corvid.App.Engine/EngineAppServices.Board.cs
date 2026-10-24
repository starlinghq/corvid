using Corvid.App.Core.Composition;
using Corvid.App.Core.Board;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Board: refused until A3 integration builds it (src/Corvid.App.Engine/Board/).</summary>
    public override IBoardService CreateBoardService(AppComposition app) =>
        new Unavailable.Board(Unavailable.ComesWith("integration", "the Board's attention items from every area"));
}
