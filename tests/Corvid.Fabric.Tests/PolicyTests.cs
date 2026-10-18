using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

public sealed class PolicyTests
{
    private static readonly DecisionPolicy Policy = DecisionPolicy.Default;

    [Fact]
    public void Calibrated_choice_acts_at_its_floor()
    {
        var question = Fixtures.Question("tool", Fixtures.Tools);

        // Confidence (0.65 − 1/3)/(2/3) = 0.475 ≥ 0.45
        var above = Policy.Evaluate("tool", DecisionAnswer.For(question, [0.65, 0.25, 0.10]));
        // Confidence 0.4 < 0.45
        var below = Policy.Evaluate("tool", DecisionAnswer.For(question, [0.6, 0.3, 0.1]));

        Assert.True(above.Acted);
        Assert.Equal("offer_tool:cron.update", above.Action);
        Assert.Equal("cron.update", above.Option);
        Assert.Equal(0.45, above.Threshold);
        Assert.False(below.Acted);
        Assert.Equal("offer_shortlist", below.Action);
        Assert.Equal(0.45, below.Threshold);
    }

    [Fact]
    public void Noul_floors_apply_to_the_yes_probability()
    {
        var question = Fixtures.Question("done");

        Assert.Equal("stop", Policy.Evaluate("done", DecisionAnswer.For(question, [0.8, 0.2])).Action);
        Assert.Equal("continue", Policy.Evaluate("done", DecisionAnswer.For(question, [0.79, 0.21])).Action);
        // A confident "no" is still below the yes floor.
        Assert.Equal("continue", Policy.Evaluate("done", DecisionAnswer.For(question, [0.01, 0.99])).Action);
    }

    [Fact]
    public void Score_escalates_on_expected_level_only_when_policy_allows()
    {
        var question = Fixtures.Question("difficulty");
        var hard = DecisionAnswer.For(question, [0, 0, 0.1, 0.3, 0.6]);
        var moderate = DecisionAnswer.For(question, [0, 0.2, 0.5, 0.3, 0]);

        var allowed = Policy.Evaluate("difficulty", hard, Fixtures.Conditions(FabricConditions.EscalationAllowed));
        var notAllowed = Policy.Evaluate("difficulty", hard);

        Assert.Equal("escalate_generator", allowed.Action);
        Assert.Equal(3.5, allowed.Threshold);
        Assert.Equal("stay_on_generator", notAllowed.Action);
        Assert.Contains(FabricConditions.EscalationAllowed, notAllowed.Reason, StringComparison.Ordinal);
        Assert.Equal("stay_on_generator",
            Policy.Evaluate("difficulty", moderate, Fixtures.Conditions(FabricConditions.EscalationAllowed)).Action);
    }

    [Theory]
    [InlineData(new[] { 0.0, 0, 0, 0.02, 0.98 }, null, "ask")]
    [InlineData(new[] { 0.0, 0, 0, 0.98, 0.02 }, FabricConditions.PermissiveMode, "ask")]
    [InlineData(new[] { 0.98, 0.02, 0, 0, 0 }, FabricConditions.PermissiveMode, "auto_run")]
    [InlineData(new[] { 0.98, 0.02, 0, 0, 0 }, null, "approval_mode")]
    [InlineData(new[] { 0.0, 0, 0.98, 0.02, 0 }, FabricConditions.PermissiveMode, "approval_mode")]
    // Below the 0.9 floor: ask, even when the most likely level is "none".
    [InlineData(new[] { 0.6, 0.4, 0, 0, 0 }, FabricConditions.PermissiveMode, "ask")]
    public void Calibrated_risk_follows_the_level_rules(double[] probabilities, string? condition, string action)
    {
        var answer = DecisionAnswer.For(Fixtures.Question("risk"), probabilities);

        var verdict = Policy.Evaluate("risk", answer, condition is null ? null : Fixtures.Conditions(condition));

        Assert.Equal(action, verdict.Action);
    }

    [Theory]
    [InlineData("risk", "ask")]
    [InlineData("skill_write_safe", "stage_for_review")]
    [InlineData("memory_op", "stage_for_review")]
    public void Gates_never_act_on_an_uncalibrated_answer(string id, string action)
    {
        var question = Fixtures.Question(id);
        // As certain as an uncalibrated answer can be, and every condition supplied.
        var answer = DecisionAnswer.Certain(question, question.Options[0].Name) with { Margin = 1 };

        var verdict = Policy.Evaluate(id, answer, Fixtures.Conditions(FabricConditions.AgentAuthored, FabricConditions.PermissiveMode));

        Assert.False(verdict.Acted);
        Assert.Equal(action, verdict.Action);
    }

    [Fact]
    public void Missing_answer_and_missing_conditions_take_the_below_floor_action()
    {
        var safe = DecisionAnswer.For(Fixtures.Question("skill_write_safe"), [0.95, 0.05]);

        Assert.Equal("stage_for_review", Policy.Evaluate("skill_write_safe", null).Action);
        Assert.Equal("stage_for_review", Policy.Evaluate("skill_write_safe", safe).Action);
        Assert.Equal("apply_skill_change", Policy.Evaluate("skill_write_safe", safe, Fixtures.Conditions(FabricConditions.AgentAuthored)).Action);
        Assert.Equal("nothing", Policy.Evaluate("skill_opportunity", DecisionAnswer.For(Fixtures.Question("skill_opportunity"), [0.9, 0.1])).Action);
    }

    [Fact]
    public void Uncalibrated_choice_uses_the_margin_rule()
    {
        var question = Fixtures.Question("tool", Fixtures.Tools);
        var probabilities = new[] { 0.5, 0.3, 0.2 };

        var wide = Policy.Evaluate("tool", DecisionAnswer.For(question, probabilities, calibrated: false, margin: 0.06));
        var narrow = Policy.Evaluate("tool", DecisionAnswer.For(question, probabilities, calibrated: false, margin: 0.05));
        var noMargin = Policy.Evaluate("tool", DecisionAnswer.For(question, [0.98, 0.01, 0.01], calibrated: false));

        Assert.Equal("offer_tool:cron.update", wide.Action);
        Assert.Equal(0.05, wide.Threshold);
        // "Above 0.05" is strict.
        Assert.Equal("offer_shortlist", narrow.Action);
        Assert.Equal(0.05, narrow.Threshold);
        Assert.Equal("offer_shortlist", noMargin.Action);
    }

    [Fact]
    public void Heuristic_difficulty_is_never_acted_on()
    {
        var answer = DecisionAnswer.Certain(Fixtures.Question("difficulty"), "needs a frontier model");

        var verdict = Policy.Evaluate("difficulty", answer, Fixtures.Conditions(FabricConditions.EscalationAllowed));

        Assert.Equal("stay_on_generator", verdict.Action);
        Assert.False(verdict.Acted);
    }

    [Fact]
    public void Instance_keys_record_the_key_and_resolve_the_catalogue_row()
    {
        var answer = DecisionAnswer.For(Fixtures.Question("clear_results"), [0.05, 0.9, 0.05]);

        var verdict = Policy.Evaluate("clear_results/call-7", answer);

        Assert.Equal("clear_results/call-7", verdict.Key);
        Assert.Equal("clear_results", verdict.QuestionId);
        Assert.Equal("apply:stub", verdict.Action);
    }
}
