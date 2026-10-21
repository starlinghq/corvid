using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

/// <summary>Plan §5.5's two-tier decider and C-004b's rule that an uncalibrated answer never diverts the turn.</summary>
public sealed class TwoTierTests
{
    private const string Session = "s-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DegradedDecider Degraded(ScriptedChatClient? chat = null, bool askTurnBlocking = false) =>
        new(new KeywordEmbedder("reminder", "send"), chat ?? ScriptedChatClient.Always("no"), askTurnBlocking: askTurnBlocking);

    // Risk level 0 at confidence 0.6 (below the 0.9 floor); memory_op "add" at 0.95 (above its 0.6 floor).
    private static DecisionAnswer Small(string key, DecisionQuestion question) => key switch
    {
        "risk" => DecisionAnswer.For(question, [0.68, 0.08, 0.08, 0.08, 0.08]),
        "memory_op" => DecisionAnswer.For(question, [0.9625, 0.0125, 0.0125, 0.0125]),
        "tool" => DecisionAnswer.For(question, [0.4, 0.3, 0.3]),
        _ => DecisionAnswer.For(question, question.Options.Select((_, i) => i == 0 ? 0.55 : 0.45 / (question.Options.Length - 1))),
    };

    private static KeyValuePair<string, DecisionQuestion>[] PreExecute =>
        [Fixtures.Ask("risk"), Fixtures.Ask("memory_op")];

    [Fact]
    public async Task A_gate_below_its_floor_goes_to_the_second_tier_on_the_same_state()
    {
        using var db = new TempLog();
        var small = FakeCalibratedDecider.Answering("kev-0.8b", Small);
        var large = FakeCalibratedDecider.Answering("kev-4b", (key, question) => key == "risk"
            ? DecisionAnswer.For(question, [0.976, 0.006, 0.006, 0.006, 0.006])
            : throw new InvalidOperationException("only risk was below its floor"));
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, small, large);
        var state = Fixtures.State();

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, state, PreExecute, cancellationToken: Ct);

        var request = Assert.Single(large.Requests);
        Assert.Equal(["risk"], request.Questions.Keys);
        Assert.Equal(state.Json.GetRawText(), request.State.GetRawText());
        Assert.Equal("kev-0.8b", result.Decider);
        Assert.Equal("kev-4b after kev-0.8b: below_floor", result["risk"].Decider);
        Assert.Equal("kev-0.8b", result["memory_op"].Decider);
        // Kev-4B is confident at level 0, so the gate passes to the approval mode instead of asking.
        Assert.True(result["risk"].Verdict.Acted);
        Assert.Equal(FabricActions.ApprovalMode, result["risk"].Verdict.Action);
        var decisions = db.Log.Read(Session, cancellationToken: Ct).Select(e => Assert.IsType<Decision>(e.Payload)).ToDictionary(d => d.QuestionId);
        Assert.Equal("kev-4b after kev-0.8b: below_floor", decisions["risk"].Decider);
        Assert.Equal(0.976, decisions["risk"].Probabilities["none"], 9);
        Assert.Equal("kev-0.8b", decisions["memory_op"].Decider);
    }

    [Fact]
    public async Task Below_the_floor_on_both_tiers_still_asks_the_user()
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log,
            FakeCalibratedDecider.Answering("kev-0.8b", Small), FakeCalibratedDecider.Answering("kev-4b", Small));

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(), [Fixtures.Ask("risk")], cancellationToken: Ct);

        Assert.Equal(FabricActions.Ask, result["risk"].Verdict.Action);
        Assert.False(result["risk"].Verdict.Acted);
        Assert.Equal("kev-4b after kev-0.8b: below_floor", result["risk"].Decider);
    }

    [Fact]
    public async Task Routing_questions_and_gates_above_their_floor_never_reach_the_second_tier()
    {
        using var db = new TempLog();
        var large = FakeCalibratedDecider.Answering("kev-4b", Small);
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, FakeCalibratedDecider.Answering("kev-0.8b", Small), large);

        var ingress = await fabric.DecideAsync(Session, Checkpoint.Ingress, Fixtures.State(),
            [Fixtures.Ask("intent"), Fixtures.Ask("tool", Fixtures.Tools), Fixtures.Ask("clarify")], cancellationToken: Ct);
        var memory = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(), [Fixtures.Ask("memory_op")], cancellationToken: Ct);

        Assert.Empty(large.Requests);
        Assert.All(ingress.Verdicts.Values, verdict => Assert.Equal("kev-0.8b", verdict.Decider));
        Assert.Equal("apply:add", memory["memory_op"].Verdict.Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_tier_that_fails_or_declines_leaves_the_first_answer_and_says_so(bool throws)
    {
        using var db = new TempLog();
        IDecider large = throws
            ? new FakeCalibratedDecider("kev-4b", _ => throw new InvalidOperationException("out of memory"))
            : FakeCalibratedDecider.Declining("kev-4b", DeciderUnavailableReason.Refused);
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, FakeCalibratedDecider.Answering("kev-0.8b", Small), large);

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(), [Fixtures.Ask("risk")], cancellationToken: Ct);

        Assert.Equal(FabricActions.Ask, result["risk"].Verdict.Action);
        Assert.Equal(0.68, result["risk"].Answer!.Probabilities[0], 9);
        Assert.Equal(throws ? "kev-0.8b after kev-4b: failed" : "kev-0.8b after kev-4b: refused", result["risk"].Decider);
    }

    [Fact]
    public async Task A_fallback_to_the_degraded_tier_never_asks_the_second_tier()
    {
        using var db = new TempLog();
        var large = FakeCalibratedDecider.Answering("kev-4b", Small);
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log,
            FakeCalibratedDecider.Declining("kev-0.8b", DeciderUnavailableReason.StateOverBudget), large);

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(), PreExecute, cancellationToken: Ct);

        Assert.Empty(large.Requests);
        Assert.Equal("degraded after kev-0.8b: state_over_budget", result.Decider);
        Assert.Equal(FabricActions.Ask, result["risk"].Verdict.Action);
    }

    [Fact]
    public void A_second_tier_needs_a_first()
    {
        using var db = new TempLog();
        Assert.Throws<ArgumentException>(() => new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, null,
            FakeCalibratedDecider.Answering("kev-4b", Small)));
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, FakeCalibratedDecider.Answering("kev-0.8b", Small),
            FakeCalibratedDecider.Answering("kev-4b", Small));
        Assert.Equal(("kev-0.8b", "kev-4b"), (fabric.CalibratedDecider, fabric.EscalationDecider));
    }

    [Fact]
    public async Task A_question_the_decider_declined_records_why()
    {
        using var db = new TempLog();
        var kev = new FakeCalibratedDecider("kev-0.8b", request => new DeciderAnswers("kev-0.8b", request.Questions
            .Where(pair => pair.Key != "memory_op")
            .ToImmutableDictionary(pair => pair.Key, pair => Small(pair.Key, pair.Value), StringComparer.Ordinal))
        {
            Declined = ImmutableDictionary<string, string>.Empty.Add("memory_op", "row_over_budget"),
        });
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, kev);

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(), PreExecute, cancellationToken: Ct);

        Assert.Equal("kev-0.8b declined: row_over_budget", result["memory_op"].Decider);
        Assert.Equal("stage_for_review", result["memory_op"].Verdict.Action);
        Assert.Null(result["memory_op"].Answer);
    }

    [Fact]
    public void An_uncalibrated_yes_never_diverts_the_turn_unless_the_floors_allow_it()
    {
        var clarify = Fixtures.Question("clarify");
        var stuck = Fixtures.Question("stuck");
        var yes = DecisionAnswer.Certain(clarify, DecisionQuestion.Yes);

        Assert.True(QuestionCatalogue.Default["clarify"].BlocksTurn);
        Assert.True(QuestionCatalogue.Default["stuck"].BlocksTurn);
        Assert.False(QuestionCatalogue.Default["done"].BlocksTurn);
        var verdict = DecisionPolicy.Default.Evaluate("clarify", yes);
        Assert.Equal("proceed", verdict.Action);
        Assert.False(verdict.Acted);
        Assert.Equal("continue", DecisionPolicy.Default.Evaluate("stuck", DecisionAnswer.Certain(stuck, DecisionQuestion.Yes)).Action);
        // Other degraded yes/no answers still act, as C-004a decided (done stops the loop, needs_recall retrieves).
        Assert.True(DecisionPolicy.Default.Evaluate("done", DecisionAnswer.Certain(Fixtures.Question("done"), DecisionQuestion.Yes)).Acted);

        var permissive = new DecisionPolicy(QuestionCatalogue.Default, FloorConfiguration.Default with { DegradedTurnBlocking = true });
        Assert.Equal("ask_clarifying_question", permissive.Evaluate("clarify", yes).Action);
        // A calibrated answer is judged on its floor either way.
        Assert.Equal("ask_clarifying_question", DecisionPolicy.Default.Evaluate("clarify", DecisionAnswer.For(clarify, [0.8, 0.2])).Action);
        Assert.Equal("proceed", DecisionPolicy.Default.Evaluate("clarify", DecisionAnswer.For(clarify, [0.6, 0.4])).Action);
    }

    [Fact]
    public async Task The_degraded_tier_asks_the_generator_about_turn_blocking_questions_only_when_allowed()
    {
        var chat = ScriptedChatClient.Always("yes");
        var request = new DecisionRequest(Fixtures.State().Json, [Fixtures.Ask("clarify"), Fixtures.Ask("needs_recall")]);

        var answers = Assert.IsType<DeciderAnswers>(await Degraded(chat).DecideAsync(request, Ct)).Answers;
        Assert.False(answers.ContainsKey("clarify"));
        Assert.True(answers.ContainsKey("needs_recall"));
        Assert.Single(chat.Prompts);

        var allowed = Assert.IsType<DeciderAnswers>(await Degraded(chat, askTurnBlocking: true).DecideAsync(request, Ct)).Answers;
        Assert.True(allowed.ContainsKey("clarify"));
    }

    [Fact]
    public void Floors_files_without_the_turn_blocking_setting_still_load_and_it_round_trips()
    {
        var old = """
            {"version":"old.1","degraded_choice_margin":0.05,"floors":{"intent":0.5,"difficulty":3.5,"needs_recall":0.6,"clarify":0.7,
             "tool":0.45,"skill":0.5,"done":0.8,"stuck":0.7,"clear_results":0.6,"injection":0.5,"risk":0.9,"remember":0.7,
             "memory_op":0.6,"skill_opportunity":0.75,"lesson_opportunity":0.75,"skill_write_safe":0.85,"wake":0.5,"deliver":0.6,"delegate":0.7}}
            """;
        Assert.False(FloorConfiguration.Parse(old).DegradedTurnBlocking);
        var on = FloorConfiguration.Default with { Version = "on.1", DegradedTurnBlocking = true };
        Assert.True(FloorConfiguration.Parse(on.ToJson()).DegradedTurnBlocking);
        Assert.Contains("\"degraded_turn_blocking\": true", on.ToJson(), StringComparison.Ordinal);
    }
}
