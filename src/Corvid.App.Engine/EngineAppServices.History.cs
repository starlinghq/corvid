using Corvid.App.Core.Composition;
using Corvid.App.Engine.History;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    private HistorySignals? _history;

    /// <summary>
    /// The one signal Rewind and Review share (A3 history): a turn of the window's ended, or one of them put files back.
    /// Made on first use from the composition's session service, on the UI thread, and disposed with the window.
    /// </summary>
    private HistorySignals History(AppComposition app) => _history ??= Track(new HistorySignals(app.SessionService, app.Dispatcher));
}
