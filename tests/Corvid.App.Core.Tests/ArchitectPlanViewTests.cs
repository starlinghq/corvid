using System.Text.Json;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// A7, an architect/editor turn's plan in the session's view: a row in its turn, "planned by X · edited by Y", the plan
/// collapsed under it, model text cleaned; on the attached wire as <c>turn_planned</c>.
/// </summary>
public sealed class ArchitectPlanViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_plan_folds_into_its_turn_before_what_the_editor_does_next()
    {
        var state = SessionFold.Replay("s",
        [
            new UserMessage("fix the failing test") { At = At },
            new TurnStarted(1, "qwen3.5-0.8b") { At = At },
            new TurnPlanned("1. Edit tests/a.cs.", "claude-sonnet-5", "qwen3.5-0.8b", 1) { At = At, Trigger = "difficulty", Position = 7 },
            new AgentMessageChunk("Fixed.") { At = At },
            new TurnEnded(1, TurnOutcome.Answered) { At = At },
        ]);

        var plan = Assert.Single(state.Transcript.OfType<TurnPlanEntry>());
        Assert.Equal(1, plan.Turn);
        Assert.Equal(("claude-sonnet-5", "qwen3.5-0.8b", 7L), (plan.Plan.Planner, plan.Plan.Editor, plan.Plan.Position!.Value));
        var answer = state.Transcript.OfType<AnswerEntry>().Single();
        Assert.True(state.Transcript.IndexOf(plan) < state.Transcript.IndexOf(answer));
        // The plan is not answer text.
        Assert.Equal("Fixed.", answer.Text);
    }

    [Fact]
    public void Live_the_plan_goes_above_the_hand_back_that_was_reported_first_as_on_replay()
    {
        SessionUpdate Escalated() => new ModelSwitched("generator", "small", "big", ModelSwitchKind.Escalated, "escalated") { At = At };
        SessionUpdate HandBack() => new ModelSwitched("generator", "big", "small", ModelSwitchKind.Returned, "to apply the plan") { At = At };
        SessionUpdate Planned() => new TurnPlanned("1. Edit a.cs.", "big", "small", 1) { At = At };
        IReadOnlyList<SessionUpdate> Turn(params SessionUpdate[] middle) =>
            [new UserMessage("fix it") { At = At }, new TurnStarted(1, "small") { At = At }, .. middle, new AgentMessageChunk("Fixed.") { At = At }];

        var replayed = SessionFold.Replay("s", Turn(Escalated(), Planned(), HandBack()));
        var live = SessionFold.Replay("s", Turn(Escalated(), HandBack(), Planned()));

        static IEnumerable<string> Rows(SessionView view) => view.Transcript.Select(entry => entry switch
        {
            SwitchEntry change => "switch " + change.Switch.To,
            TurnPlanEntry => "plan",
            AnswerEntry => "answer",
            _ => entry.GetType().Name,
        }).Where(row => row is "plan" or "answer" || row.StartsWith("switch", StringComparison.Ordinal));
        Assert.Equal(["switch big", "plan", "switch small", "answer"], Rows(replayed));
        Assert.Equal(Rows(replayed), Rows(live));
    }

    [Fact]
    public async Task The_plan_row_says_who_planned_and_who_edits_and_opens_to_the_plan_cleaned()
    {
        var (session, service, time) = await ModelSwitchViewTests.Open();

        service.Push(new TurnPlanned("1. Edit a.cs​.‮", "big‮model", "small", 1) { At = time.GetUtcNow() });

        var row = Assert.IsType<PlanBlock>(session.Blocks.Last());
        Assert.Equal(TranscriptBlockKind.Plan, row.Kind);
        Assert.Equal("planned by bigmodel · edited by small", row.Label);
        Assert.Equal("1. Edit a.cs.", row.Text);
        Assert.False(row.IsExpanded);
        row.ToggleCommand.Execute(null);
        Assert.True(row.IsExpanded);

        service.Push(new TurnPlanned("2. Also b.cs.", "big", "small", 2) { At = time.GetUtcNow() });
        Assert.Equal("re-planned by big · edited by small", Assert.IsType<PlanBlock>(session.Blocks.Last()).Label);
    }

    [Fact]
    public void A_plan_crosses_the_attached_wire_unchanged()
    {
        var plan = new TurnPlanned("1. Edit a.cs.", "big", "small", 2) { At = At, Trigger = "stuck", Position = 12 };

        var json = JsonSerializer.Serialize<SessionUpdate>(plan, AttachedJson.Update);
        var back = Assert.IsType<TurnPlanned>(JsonSerializer.Deserialize(json, AttachedJson.Update));

        Assert.Equal("turn_planned", AttachedJson.KindOf(typeof(TurnPlanned)));
        Assert.False(AttachedJson.IsAcpKind(typeof(TurnPlanned)));
        Assert.Equal(plan, back);
    }
}
