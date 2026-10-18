using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Choice_confidence_follows_kev()
    {
        var question = DecisionQuestion.Choice("Which?", "a", "b", "c");

        var answer = DecisionAnswer.For(question, [0.7, 0.2, 0.1]);

        // (p_max − 1/K)/(1 − 1/K) = (0.7 − 1/3)/(2/3)
        Assert.Equal(0.55, answer.Confidence, 10);
        Assert.Equal("a", answer.Choice);
        Assert.Equal(0, DecisionAnswer.For(question, [1 / 3.0, 1 / 3.0, 1 / 3.0]).Confidence, 10);
        Assert.Equal(1, DecisionAnswer.For(question, [0, 0, 1]).Confidence, 10);
        Assert.Null(answer.ExpectedScore);
        Assert.Equal(0.7, answer.ToProbabilityMap()["a"]);
    }

    [Fact]
    public void Score_expected_value_uses_level_indices()
    {
        var question = DecisionQuestion.Score("How hard?", "trivial", "easy", "moderate", "hard", "frontier");

        var answer = DecisionAnswer.For(question, [0, 0, 0.1, 0.3, 0.6]);

        // Σ i·pᵢ = 0.2 + 0.9 + 2.4: between the fourth and fifth levels, as in plan §5.3.
        Assert.Equal(3.5, answer.ExpectedScore!.Value, 10);
        Assert.Equal("frontier", answer.Choice);
        Assert.Equal(4, answer.ChoiceIndex);
    }

    [Fact]
    public void Noul_options_are_yes_and_no()
    {
        var question = DecisionQuestion.Noul("Done?");

        var answer = DecisionAnswer.For(question, [0.8, 0.2]);

        Assert.Equal(["yes", "no"], question.Options.Select(option => option.Name));
        Assert.True(answer.Yes);
        Assert.Equal(0.8, answer.ProbabilityOf(DecisionQuestion.Yes));
        Assert.False(DecisionAnswer.Certain(question, DecisionQuestion.No).Yes);
        Assert.False(DecisionAnswer.Certain(question, DecisionQuestion.No).Calibrated);
    }

    [Fact]
    public void Options_carry_descriptions_as_kev_reads_them()
    {
        var question = DecisionQuestion.Choice(DecisionContent.FromText("Which tool?"),
            [new QuestionOption("cron.update", "change a reminder"), new QuestionOption("none")]);

        Assert.Equal(["cron.update: change a reminder", "none"], question.Options.Select(option => option.ToString()));
    }

    [Fact]
    public void Malformed_questions_and_answers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => DecisionQuestion.Choice("Which?", "only"));
        Assert.Throws<ArgumentException>(() => DecisionQuestion.Choice("Which?", "a", "a"));
        Assert.Throws<ArgumentException>(() => DecisionQuestion.Score("How?", "low", " "));
        Assert.ThrowsAny<ArgumentException>(() => DecisionQuestion.Noul(" "));

        var question = DecisionQuestion.Choice("Which?", "a", "b");
        Assert.Throws<ArgumentException>(() => DecisionAnswer.For(question, [0.7, 0.7]));
        Assert.Throws<ArgumentException>(() => DecisionAnswer.For(question, [1.2, -0.2]));
        Assert.Throws<ArgumentException>(() => DecisionAnswer.For(question, [1.0]));
        Assert.Throws<ArgumentException>(() => DecisionAnswer.For(question, [double.NaN, 1]));
        Assert.Throws<ArgumentException>(() => DecisionAnswer.For(question, [0.5, 0.5]).Validate(DecisionQuestion.Choice("Which?", "a", "c")));
        Assert.Throws<ArgumentException>(() => DecisionAnswer.Certain(question, "c"));
        Assert.Throws<ArgumentException>(() => new DecisionRequest(Fixtures.Json("{}"), []));
    }
}
