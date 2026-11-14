using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// C-042 in the window: a flock's message to a session the window drives starts a turn the window shows as it shows its own
/// (the peer named on the prompt), and the session's summary carries its flock for the rail.
/// </summary>
public sealed class EngineFlockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_s_message_starts_a_turn_the_window_shows_and_the_summary_names_the_flock()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("On it."))));
        var runtime = fixture.Runtime;
        var api = runtime.OpenSession(surface: "cli").Id;
        var web = runtime.OpenSession(surface: "app").Id;
        var flock = runtime.Flocks.Create("team");
        runtime.JoinFlock(flock.Id, api, "api");
        runtime.JoinFlock(flock.Id, web, "web");
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(web, waiter.Add, Ct);

        runtime.Flocks.Say(flock.Id, api, "Please update the client for /v2/token.", [], tainted: false);

        var prompt = await waiter.WaitAsync<UserMessage>().WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(("Please update the client for /v2/token.", "api"), (prompt.Text, prompt.From));
        await waiter.WaitAsync<TurnEnded>().WaitAsync(TimeSpan.FromSeconds(20), Ct);
        // The answer streams in chunks, as a turn the window started does.
        Assert.Equal("On it.", string.Concat(waiter.Updates.OfType<AgentMessageChunk>().Select(chunk => chunk.Text)));
        var listed = (await service.ListAsync(Ct)).Single(summary => summary.Id == web);
        Assert.Equal("team", listed.Flock);
    }
}
