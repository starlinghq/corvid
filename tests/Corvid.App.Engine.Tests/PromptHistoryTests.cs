using Corvid.Abstractions.Events;
using Corvid.App.Core.Composition;
using Corvid.App.Engine.PromptHistory;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>The palette's prompt history over the event log's user messages across the workspace's sessions (A3 integration).</summary>
public sealed class PromptHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Say(IEventLog log, string session, params SessionEvent[] events) =>
        log.Append(session, log.GetHead(session, Token), [.. events.Select(EventData.Create)], Token);

    [Fact]
    public async Task ThePromptsAPersonTypedComeNewestFirstWithTheirSessionsAndNothingElse()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var log = fixture.Runtime.Log;
        Say(log, "cli-a", new UserMessage("fix the flaky test"), new AssistantMessage("Done."));
        Say(log, "acp-b", new UserMessage("draft the release notes"));
        // A job's prompt, a chat's messages and the stores' sessions are not the person's typing.
        Say(log, "job:nightly:run-1", new UserMessage("Tidy the workspace."));
        Say(log, "gateway:telegram:42", new UserMessage("hello from a chat"));
        // A hidden character is kept as sent: the palette shows it cleaned and refuses to put it back.
        Say(log, "cli-a", new UserMessage("also check the retry​ count"));
        var history = new EnginePromptHistory(fixture.Engine);

        var entries = await history.LoadAsync(Token);

        Assert.Equal(
            [("also check the retry​ count", "cli-a"), ("draft the release notes", "acp-b"), ("fix the flaky test", "cli-a")],
            entries.Select(entry => (entry.Text, entry.SessionId)));
        Assert.True(entries[0].At >= entries[1].At && entries[1].At >= entries[2].At);
        Assert.False(EnginePromptHistory.Typed(Corvid.App.Engine.Memory.EngineMemoryService.ImportSession));
        Assert.False(EnginePromptHistory.Typed(McpSessions.Pins));
    }

    [Fact]
    public async Task WhenTheRuntimeCannotStartTheHistoryRefusesWithItsReason()
    {
        await using var host = EngineHosts.Create(new FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);

        var refusal = await Assert.ThrowsAsync<ServiceUnavailableException>(() => new EnginePromptHistory(engine).LoadAsync(Token));
        Assert.StartsWith("Corvid cannot start:", refusal.Message, StringComparison.Ordinal);
    }
}
