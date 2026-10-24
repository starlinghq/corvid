using Corvid.App.Core;
using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

public sealed class DemoBoardServiceTests
{
    [Fact]
    public async Task ItServesTheMockupsFourAreaItemsOldestFirst()
    {
        var service = new DemoBoardService(ManualTimeProvider.Demo());

        var items = await service.ListAttentionAsync(TestContext.Current.CancellationToken);

        Assert.Equal([AttentionKind.McpTool, AttentionKind.Pairing, AttentionKind.Memory, AttentionKind.Job], items.Select(item => item.Kind));
        Assert.Equal("Pairing · Telegram · @dana · code ABCD-EFGH", items[1].What);
        Assert.Equal(["Pair as guest", "Pair as owner", "Ignore"], items[1].Actions.Select(action => action.Label));
        Assert.Equal(AttentionActionStyle.Danger, items[1].Actions[2].Style);
        var run = Assert.Single(items[3].Actions);
        Assert.Equal(AttentionActionKind.OpenSession, run.Kind);
        Assert.Equal(DemoSessionService.JobSessionId, run.SessionId);
        Assert.Equal(Area.Memory, items[2].Actions[0].Area);
    }

    [Fact]
    public async Task OnlyAResolveActionAnswersAnItem()
    {
        var service = new DemoBoardService(ManualTimeProvider.Demo());
        var changes = 0;
        service.AttentionChanged += (_, _) => changes++;

        await service.ResolveAsync(DemoBoardService.McpItemId, "diff", TestContext.Current.CancellationToken);
        await service.ResolveAsync("nothing", "accept", TestContext.Current.CancellationToken);
        Assert.Equal(0, changes);
        Assert.Equal(4, (await service.ListAttentionAsync(TestContext.Current.CancellationToken)).Count);

        await service.ResolveAsync(DemoBoardService.McpItemId, "accept", TestContext.Current.CancellationToken);
        Assert.Equal(1, changes);
        Assert.DoesNotContain(await service.ListAttentionAsync(TestContext.Current.CancellationToken), item => item.Id == DemoBoardService.McpItemId);
    }

    [Fact]
    public async Task ItReportsTheEngineAsTheBoardMockupsStatusBarShowsIt()
    {
        var status = await new DemoBoardService(ManualTimeProvider.Demo()).GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BoardStatus("in-process", "Kev-0.8B, calibrated", "here · next: nightly-tidy, tomorrow 09:00", "telegram up · discord up · slack off"), status);
    }

    [Fact]
    public async Task TheWatcherReplaysTheLogThenFoldsLiveUpdatesUntilDisposed()
    {
        var time = ManualTimeProvider.Demo();
        var sessions = new DemoSessionService(new InlineDispatcher(), time);
        var views = new List<SessionView>();

        var watch = await new SessionServiceWatcher(sessions).WatchAsync(DemoSessionService.ReleaseNotesSessionId, views.Add, TestContext.Current.CancellationToken);

        var first = Assert.Single(views);
        Assert.Equal("q-1", first.Question?.QuestionId);
        await sessions.AnswerQuestionAsync(DemoSessionService.ReleaseNotesSessionId, "q-1", "0.2.0", TestContext.Current.CancellationToken);
        Assert.Null(views[^1].Question);
        var seen = views.Count;

        watch.Dispose();
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(seen, views.Count);
    }
}
