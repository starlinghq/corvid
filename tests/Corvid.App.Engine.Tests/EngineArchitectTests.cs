using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Tests;
using HostTurnPlanned = Corvid.Host.Sessions.TurnPlanned;
using TurnPlan = Corvid.Abstractions.Events.TurnPlan;
using TurnPlanned = Corvid.App.Core.Sessions.TurnPlanned;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A7: an architect/editor turn on the engine: <c>/architect</c> from the app, the plan as its own row ("planned by X ·
/// edited by Y") before the editor's call, never in the answer, live and replayed alike.
/// </summary>
public sealed class EngineArchitectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_translator_turns_a_plan_into_the_apps_plan_with_bare_model_names()
    {
        var at = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var translator = new SessionUpdateTranslator(Path.GetTempPath());
        translator.Translate(new Corvid.Host.Sessions.PromptSent("fix it", 1) { At = at });

        var plan = Assert.IsType<TurnPlanned>(Assert.Single(translator.Translate(
            new HostTurnPlanned("1. Edit a.cs.", new TurnPlan("big@cuda0", "small@cpu", "stuck", 2), 5) { At = at })));

        Assert.Equal(new TurnPlanned("1. Edit a.cs.", "big", "small", 2) { At = at, Trigger = "stuck", Position = 5 }, plan);
    }

    [Fact]
    public async Task Architect_from_the_app_plans_the_turn_and_the_plan_is_its_own_row_live_and_replayed()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("Two notes.")));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var live = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, live.Add, Ct);

        await service.RunCommandAsync(id, "/architect", Ct);
        await service.SendAsync(id, "list my notes", Ct);
        await live.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        Check(live.Updates);
        using var again = fixture.Sessions();
        using var replayed = await again.OpenAsync(id, _ => { }, Ct);
        Check(replayed.History);

        static void Check(IReadOnlyList<SessionUpdate> updates)
        {
            var plan = Assert.Single(updates.OfType<TurnPlanned>());
            Assert.Equal(("Hello from the larger model.", "fake-gen-large", "fake-gen", "architect"), (plan.Text, plan.Planner, plan.Editor, plan.Trigger));
            var view = SessionFold.Replay("s", updates);
            var row = Assert.Single(view.Transcript.OfType<TurnPlanEntry>());
            var card = view.Transcript.OfType<ToolEntry>().Single(entry => entry.Tool == "list_notes");
            Assert.True(view.Transcript.IndexOf(row) < view.Transcript.IndexOf(card));
            Assert.Equal("fake-gen", card.Model);
            Assert.DoesNotContain(view.Transcript.OfType<AnswerEntry>(), answer => answer.Text.Contains("larger model", StringComparison.Ordinal));
            Assert.Contains(view.Transcript.OfType<AnswerEntry>(), answer => answer.Text.Contains("Two notes.", StringComparison.Ordinal));
        }
    }
}
