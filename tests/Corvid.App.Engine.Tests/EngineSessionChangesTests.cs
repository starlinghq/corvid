using AssistantMessage = Corvid.Abstractions.Events.AssistantMessage;
using UserMessage = Corvid.Abstractions.Events.UserMessage;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Engine.Tests;

/// <summary>The Sessions dock's Changes on the engine: the write ledger's files as the session opens (A3 end-to-end pass).</summary>
public sealed class EngineSessionChangesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnOpenedSessionListsTheFilesItsCallsChangedThisTurnAndThisSession()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        fixture.Write("story.txt", "one\ntwo\n");
        var id = fixture.Session();
        await fixture.WriteTurnAsync(id, "call-1", "story.txt", "ONE\ntwo\n", "capitalise the story");
        fixture.Append(id, new UserMessage("more"));
        await fixture.WriteCallAsync(id, "call-2", "notes.md", "a note\n");
        await fixture.WriteCallAsync(id, "call-3", "story.txt", "ONE\ntwo\nthree\n");
        fixture.Append(id, new AssistantMessage("Done."));

        using var subscription = await fixture.Sessions.OpenAsync(id, _ => { }, Ct);

        var changes = Assert.Single(subscription.History.OfType<ChangesUpdate>());
        // The session's list counts each path from before its first call to after its last.
        Assert.Equal([new FileChange("story.txt", 2, 1), new FileChange("notes.md", 1, 0)], changes.Session);
        // This turn's from before the turn's own first call on it.
        Assert.Equal([new FileChange("notes.md", 1, 0), new FileChange("story.txt", 1, 0)], changes.Turn);
        var state = SessionFold.Replay(id, subscription.History);
        Assert.Equal(2, state.SessionChanges.Length);
    }

    [Fact]
    public async Task ASessionWithoutWritesListsNoFiles()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = fixture.Session();
        fixture.Say(id, "hello");

        using var subscription = await fixture.Sessions.OpenAsync(id, _ => { }, Ct);

        var changes = Assert.Single(subscription.History.OfType<ChangesUpdate>());
        Assert.Empty(changes.Session);
        Assert.Empty(changes.Turn);
    }
}
