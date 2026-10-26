using System.Text.Json;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// The update contract on the wire (G-1): every update kind the contract has is one the wire knows, an update survives the
/// trip unchanged, ACP's kinds keep ACP's names, and a kind the reader does not know is refused rather than skipped.
/// </summary>
public sealed class AttachedWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryUpdateRecordOfTheContractIsOnTheWire()
    {
        var records = typeof(SessionUpdate).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SessionUpdate)) && !type.IsAbstract)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(records.Select(type => type.Name), AttachedJson.UpdateTypes.Select(type => type.Name).Order(StringComparer.Ordinal));
        // Each has its own name on the wire.
        Assert.Equal(records.Count, records.Select(AttachedJson.KindOf).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AcpsUpdatesKeepAcpsNamesAndCorvidsAreSnakeCase()
    {
        Assert.Equal("agent_message_chunk", AttachedJson.KindOf(typeof(AgentMessageChunk)));
        Assert.Equal("tool_call_update", AttachedJson.KindOf(typeof(ToolCallUpdate)));
        Assert.Equal("user_message_chunk", AttachedJson.KindOf(typeof(UserMessage)));
        Assert.Equal("approval_requested", AttachedJson.KindOf(typeof(ApprovalRequested)));
        Assert.True(AttachedJson.IsAcpKind(typeof(PlanUpdate)));
        Assert.False(AttachedJson.IsAcpKind(typeof(QuestionAsked)));

        var json = JsonSerializer.Serialize<SessionUpdate>(new AgentMessageChunk("hi"), AttachedJson.Update);
        Assert.StartsWith("""{"sessionUpdate":"agent_message_chunk",""", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DemoScene.Main)]
    [InlineData(DemoScene.Approval)]
    public async Task EverySessionOfTheDemoRoundTripsUnchanged(DemoScene scene)
    {
        var service = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo(), scene);
        var kinds = new HashSet<Type>();
        foreach (var summary in await service.ListAsync(Ct))
        {
            using var subscription = await service.OpenAsync(summary.Id, _ => { }, Ct);
            foreach (var update in subscription.History)
            {
                kinds.Add(update.GetType());
                var once = JsonSerializer.Serialize(update, AttachedJson.Update);
                var back = JsonSerializer.Deserialize(once, AttachedJson.Update)!;
                Assert.IsType(update.GetType(), back);
                Assert.Equal(once, JsonSerializer.Serialize(back, AttachedJson.Update));
            }
            var text = JsonSerializer.Serialize(summary, AttachedJson.Summary);
            Assert.Equal(text, JsonSerializer.Serialize(JsonSerializer.Deserialize(text, AttachedJson.Summary)!, AttachedJson.Summary));

            // The fold over what arrived draws what the fold over the original draws.
            var arrived = subscription.History.Select(update =>
                JsonSerializer.Deserialize(JsonSerializer.Serialize(update, AttachedJson.Update), AttachedJson.Update)!).ToList();
            var original = SessionFold.Replay(summary.Id, subscription.History);
            var remote = SessionFold.Replay(summary.Id, arrived);
            Assert.Equal(original.Transcript.Count, remote.Transcript.Count);
            Assert.Equal(original.Approvals.Select(open => open.ApprovalId), remote.Approvals.Select(open => open.ApprovalId));
            Assert.Equal((original.Turn, original.Running, original.Tainted), (remote.Turn, remote.Running, remote.Tainted));
        }
        // The demo's sessions cover the contract's main kinds: turns, tools, approvals, decisions, questions.
        Assert.Contains(typeof(ToolCall), kinds);
        Assert.Contains(typeof(TurnStarted), kinds);
        Assert.Contains(typeof(CheckpointDecided), kinds);
        if (scene == DemoScene.Approval)
            Assert.Contains(typeof(ApprovalRequested), kinds);
    }

    [Fact]
    public void AnApprovalCardKeepsItsHiddenCharactersForThePersonToSee()
    {
        // The card's text is the engine's as it has it: the person approving sees hidden characters revealed, as in-process.
        var request = new ApprovalRequested("a-1", "shell", "rm -rf build/‮", DateTimeOffset.UnixEpoch)
        {
            Commands = [new ParsedCommand("rm -rf build/‮", "rm")],
        };
        var back = (ApprovalRequested)JsonSerializer.Deserialize(JsonSerializer.Serialize<SessionUpdate>(request, AttachedJson.Update), AttachedJson.Update)!;

        Assert.Equal("rm -rf build/‮", back.Text);
        Assert.True(SafeText.HasHidden(back.Commands.Single().Text));
    }

    [Fact]
    public void AKindTheReaderDoesNotKnowIsRefused()
    {
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize("""{"sessionUpdate":"from_the_future","at":"2026-10-25T00:00:00Z"}""", AttachedJson.Update));
    }
}
