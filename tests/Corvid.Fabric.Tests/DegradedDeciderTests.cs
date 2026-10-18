using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

public sealed class DegradedDeciderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DecisionRequest Request(TurnState state, params KeyValuePair<string, DecisionQuestion>[] questions) =>
        new(state.Json, questions);

    private static async Task<DeciderAnswers> DecideAsync(DegradedDecider decider, DecisionRequest request) =>
        Assert.IsType<DeciderAnswers>(await decider.DecideAsync(request, Ct));

    [Fact]
    public async Task Choice_picks_the_most_similar_option_with_its_margin()
    {
        var embedder = new KeywordEmbedder("reminder", "send");
        var decider = new DegradedDecider(embedder, ScriptedChatClient.Always("yes"));

        var result = await DecideAsync(decider, Request(Fixtures.State(), Fixtures.Ask("tool", Fixtures.Tools)));

        var answer = result.Answers["tool"];
        Assert.Equal("degraded", result.Decider);
        Assert.False(answer.Calibrated);
        Assert.Equal("cron.update", answer.Choice);
        Assert.True(answer.Margin > 0.5, $"margin {answer.Margin}");
        Assert.Equal(1, answer.Probabilities.Sum(), 6);
        Assert.Equal("offer_tool:cron.update", DecisionPolicy.Default.Evaluate("tool", answer).Action);
        // The state is embedded as Kev would read it, and options as "name: description".
        var batch = Assert.Single(embedder.Batches);
        Assert.StartsWith("user_message: can you move tomorrow's dentist reminder", batch[0], StringComparison.Ordinal);
        Assert.Equal("cron.update: change a scheduled reminder", batch[1]);
    }

    [Fact]
    public async Task Choice_within_the_margin_offers_the_whole_shortlist()
    {
        // The two top options are 0.995 and 0.981 similar to the state: a margin of about 0.015, under 0.05.
        var embedder = new KeywordEmbedder
        {
            Override = text => text switch
            {
                _ when text.StartsWith("cron.update", StringComparison.Ordinal) => [1, 0.1f],
                _ when text.StartsWith("message.send", StringComparison.Ordinal) => [1, 0.2f],
                _ when text.StartsWith("none", StringComparison.Ordinal) => [0, 1],
                _ => [1, 0],
            },
        };
        var decider = new DegradedDecider(embedder, ScriptedChatClient.Always("yes"));

        var answer = (await DecideAsync(decider, Request(Fixtures.State(), Fixtures.Ask("tool", Fixtures.Tools)))).Answers["tool"];
        var verdict = DecisionPolicy.Default.Evaluate("tool", answer);

        Assert.InRange(answer.Margin!.Value, 0.01, 0.02);
        Assert.Equal("cron.update", answer.Choice);
        Assert.False(verdict.Acted);
        Assert.Equal("offer_shortlist", verdict.Action);
        Assert.Equal(0.05, verdict.Threshold);
    }

    [Fact]
    public async Task All_choice_questions_share_one_embedding_batch()
    {
        var embedder = new KeywordEmbedder("reminder", "tools", "thread");
        var decider = new DegradedDecider(embedder, ScriptedChatClient.Always("no"));

        var result = await DecideAsync(decider, Request(Fixtures.State(),
            Fixtures.Ask("tool", Fixtures.Tools), Fixtures.Ask("intent")));

        var batch = Assert.Single(embedder.Batches);
        Assert.Equal(1 + 3 + 5, batch.Count);
        Assert.Equal(["intent", "tool"], result.Answers.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Embedding_failure_leaves_choices_unanswered()
    {
        var embedder = new KeywordEmbedder { Override = _ => throw new HttpRequestException("embedder down") };
        var decider = new DegradedDecider(embedder, ScriptedChatClient.Always("yes"));

        var result = await DecideAsync(decider, Request(Fixtures.State(), Fixtures.Ask("intent")));

        Assert.Empty(result.Answers);
    }

    [Theory]
    [InlineData("Yes.", "stop")]
    [InlineData("**YES**", "stop")]
    [InlineData("no, the user also asked to tell sam", "continue")]
    [InlineData("maybe", "continue")]
    [InlineData("yesterday", "continue")]
    public async Task Noul_is_answered_by_the_generator(string reply, string action)
    {
        var chat = ScriptedChatClient.Always(reply);
        var decider = new DegradedDecider(new KeywordEmbedder(), chat);

        var result = await DecideAsync(decider, Request(Fixtures.State(step: 2), Fixtures.Ask("done")));

        var answer = result.Answers.GetValueOrDefault("done");
        Assert.Equal(action, DecisionPolicy.Default.Evaluate("done", answer).Action);
        Assert.False(answer?.Calibrated ?? false);
        var prompt = Assert.Single(chat.Prompts);
        Assert.Contains("user_message: can you move tomorrow's dentist reminder", prompt, StringComparison.Ordinal);
        Assert.Contains("Is the user's request fully handled?", prompt, StringComparison.Ordinal);
        Assert.Equal(0f, chat.Options[0]!.Temperature);
    }

    [Fact]
    public async Task Generator_failure_leaves_the_question_unanswered()
    {
        var decider = new DegradedDecider(new KeywordEmbedder(), ScriptedChatClient.Throwing());

        var result = await DecideAsync(decider, Request(Fixtures.State(), Fixtures.Ask("clarify"), Fixtures.Ask("needs_recall")));

        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task Gates_and_conservative_questions_call_no_model()
    {
        var chat = ScriptedChatClient.Always("yes");
        var embedder = new KeywordEmbedder("reminder");
        var decider = new DegradedDecider(embedder, chat);
        var neighbours = Fixtures.Ask("memory_op");

        var result = await DecideAsync(decider, Request(Fixtures.State(),
            Fixtures.Ask("risk"), Fixtures.Ask("skill_write_safe"), neighbours, Fixtures.Ask("clear_results/call-1")));
        var injection = await DecideAsync(decider, Request(Fixtures.State(), Fixtures.Ask("injection")));

        Assert.Empty(result.Answers);
        Assert.Empty(injection.Answers);
        Assert.Empty(chat.Prompts);
        Assert.Empty(embedder.Batches);
    }

    [Theory]
    [InlineData("hi", 0, "trivial")]
    [InlineData("hi", 3, "easy")]
    [InlineData("hi", 8, "moderate")]
    public async Task Difficulty_comes_from_length_and_tool_steps(string message, int step, string level)
    {
        var decider = new DegradedDecider(new KeywordEmbedder(), ScriptedChatClient.Always("yes"));

        var answer = (await DecideAsync(decider, Request(Fixtures.State(message, step), Fixtures.Ask("difficulty")))).Answers["difficulty"];

        Assert.Equal(level, answer.Choice);
        Assert.False(answer.Calibrated);
    }

    [Fact]
    public async Task Long_requests_score_as_hard_but_never_escalate()
    {
        var decider = new DegradedDecider(new KeywordEmbedder(), ScriptedChatClient.Always("yes"));
        var message = string.Concat(Enumerable.Repeat("refactor the scheduler and migrate every job ", 15));

        var answer = (await DecideAsync(decider, Request(Fixtures.State(message, step: 9), Fixtures.Ask("difficulty")))).Answers["difficulty"];
        var verdict = DecisionPolicy.Default.Evaluate("difficulty", answer, Fixtures.Conditions(FabricConditions.EscalationAllowed));

        Assert.Equal("needs a frontier model", answer.Choice);
        Assert.Equal("stay_on_generator", verdict.Action);
    }

    [Fact]
    public async Task Questions_outside_the_catalogue_follow_their_shape()
    {
        var chat = ScriptedChatClient.Always("yes");
        var decider = new DegradedDecider(new KeywordEmbedder("reminder"), chat);

        var result = await DecideAsync(decider, Request(Fixtures.State(),
            KeyValuePair.Create("custom_yes_no", DecisionQuestion.Noul("Is this about a reminder?")),
            KeyValuePair.Create("custom_choice", DecisionQuestion.Choice("Which?", "reminder", "other")),
            KeyValuePair.Create("custom_score", DecisionQuestion.Score("How much?", "low", "high"))));

        Assert.True(result.Answers["custom_yes_no"].Yes);
        Assert.Equal("reminder", result.Answers["custom_choice"].Choice);
        Assert.False(result.Answers.ContainsKey("custom_score"));
    }
}
