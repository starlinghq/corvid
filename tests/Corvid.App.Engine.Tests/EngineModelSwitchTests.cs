using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Models;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A5 (switch): a switch mid-turn on the engine: its row says the turn takes it at its next step, each step and tool card
/// names its generator, the header's decider is the session's, and a replay derives all of it from the log's ModelSwitched.
/// </summary>
public sealed class EngineModelSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Tools = "fake-gen-tools";

    [Fact]
    public async Task A_switch_mid_turn_is_taken_at_the_next_step_and_each_step_and_call_names_its_generator_live_and_replayed()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("unused")));
        var tools = new FakeGenerator(Reply.Call("list_notes"), Reply.Say("Done on the other model."));
        models.Provider.Add(Tools, [ModelSlots.Generator], () => tools, context: 32_768, tools: ToolCallSupport.Native);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        models.Generator.Hold = (step, _) =>
        {
            if (step != 0)
                return null;
            held.TrySetResult();
            return release.Task;
        };
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var live = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, live.Add, Ct);
        var header = Assert.IsType<EngineStatus>(subscription.History.Single(update => update is EngineStatus));
        Assert.Equal(("fake-gen", "degraded tier"), (header.Model, header.Decider));

        await service.SendAsync(id, "list my notes", Ct);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var session = fixture.Runtime.OpenSession(id, "app");
        await session.SetModelAsync(ModelSlots.Generator, Tools, SwitchScope.Session, Ct);
        await session.SetModelAsync(ModelSlots.Decider, "fake-kev", SwitchScope.Session, Ct);
        release.SetResult();
        await live.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        Check(live.Updates);
        // The header's decider follows the session's own.
        Assert.Contains(live.Updates, update => update is EngineStatus { Decider: "fake-kev" });

        // Replayed in a fresh window, from the log's ModelSwitched events.
        using var again = fixture.Sessions();
        using var replayed = await again.OpenAsync(id, _ => { }, Ct);
        Check(replayed.History);
        Assert.Contains(replayed.History.OfType<TurnProgress>(), step => step.Model == Tools);
        Assert.Equal("fake-kev", Assert.IsType<EngineStatus>(replayed.History.Last(update => update is EngineStatus)).Decider);

        static void Check(IReadOnlyList<SessionUpdate> updates)
        {
            var row = updates.OfType<ModelSwitched>().Single(change => change.Slot == ModelSlots.Generator);
            Assert.Equal(("fake-gen", Tools, ModelSwitchKind.Switched), (row.From, row.To, row.Kind));
            Assert.Equal("for this session; the running turn takes it at its next step", row.Reason);
            Assert.Equal(ModelSlots.Decider, updates.OfType<ModelSwitched>().Single(change => change.Slot != ModelSlots.Generator).Slot);
            var calls = updates.OfType<ToolCall>().Where(call => call.Tool == "list_notes").GroupBy(call => call.CallId).Select(group => group.Last()).ToList();
            Assert.Equal(["fake-gen", Tools], calls.Select(call => call.Model));
            // Live, a step begins before its calls say which generator asked for them; the replay knows at once.
            Assert.Contains(updates.OfType<ToolCall>(), call => call.Model == Tools);
            // Folded, the second card says which generator asked for it, and the turn lists both.
            var view = SessionFold.Replay("s", updates);
            var cards = view.Transcript.OfType<ToolEntry>().ToList();
            Assert.Equal([("fake-gen", "fake-gen"), (Tools, "fake-gen")], cards.Select(card => (card.Model, card.TurnModel)));
            Assert.Equal(["fake-gen", Tools], view.Transcript.OfType<TurnHeaderEntry>().Last().Models);
        }
    }

    [Fact]
    public async Task A_switch_between_turns_is_said_for_this_session_and_a_replay_shows_its_row_once()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var live = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, live.Add, Ct);
        await fixture.Runtime.OpenSession(id, "app").SetModelAsync(ModelSlots.Generator, "fake-gen-large", SwitchScope.Session, Ct);
        var row = await live.WaitAsync<ModelSwitched>();
        Assert.Equal("for this session", row.Reason);

        await service.SendAsync(id, "hello", Ct);
        await live.WaitAsync<TurnEnded>();
        await service.IdleAsync();
        using var again = fixture.Sessions();
        using var replayed = await again.OpenAsync(id, _ => { }, Ct);
        Assert.Single(replayed.History.OfType<ModelSwitched>());
        Assert.Equal("fake-gen-large", replayed.History.OfType<TurnStarted>().Single().Model);
    }
}
