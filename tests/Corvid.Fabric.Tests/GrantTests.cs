using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

/// <summary>
/// C-026: <c>task_succeeded</c> is a precondition with its own floor. It grants the <c>task_succeeded</c> condition that
/// <c>skill_opportunity</c> requires, inside one egress request, and a caller cannot assert it.
/// </summary>
public sealed class GrantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Session = "grant-session";

    private static readonly IReadOnlySet<string> Evidence =
        Fixtures.Conditions(FabricConditions.ProcedureEvidence, FabricConditions.ToolFailuresRecovered);

    private static FakeCalibratedDecider Yes(double opportunity, double success) =>
        FakeCalibratedDecider.Answering("kev-test", (key, question) => key switch
        {
            "skill_opportunity" => DecisionAnswer.For(question, [opportunity, 1 - opportunity]),
            "task_succeeded" => DecisionAnswer.For(question, [success, 1 - success]),
            _ => null,
        });

    private static async Task<(CheckpointResult Result, IReadOnlyList<Decision> Logged)> EgressAsync(IDecider calibrated,
        IReadOnlySet<string> conditions, params string[] keys)
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new KeywordEmbedder("x"), ScriptedChatClient.Always("no")),
            db.Log, calibrated);
        var result = await fabric.DecideAsync(Session, Checkpoint.Egress, Fixtures.State(), keys.Select(key => Fixtures.Ask(key)), conditions, Ct);
        return (result, [.. db.Log.Read(Session, cancellationToken: Ct).Select(item => Assert.IsType<Decision>(item.Payload))]);
    }

    [Fact]
    public void The_default_catalogue_declares_the_grant()
    {
        var success = QuestionCatalogue.Default["task_succeeded"];

        Assert.Equal(FabricConditions.TaskSucceeded, success.Grants);
        Assert.Equal([FabricConditions.ToolFailuresRecovered], success.Requires);
        Assert.Equal(DegradedRule.GeneratorYesNo, success.Degraded);
        Assert.Equal([Checkpoint.Egress], success.Checkpoints);
        Assert.Equal([FabricConditions.ProcedureEvidence, FabricConditions.TaskSucceeded], QuestionCatalogue.Default["skill_opportunity"].Requires);
        Assert.Equal([FabricConditions.TaskSucceeded], QuestionCatalogue.Default.Granted);
        Assert.Equal(0.8, FloorConfiguration.Default.FloorOf("task_succeeded"));
        Assert.DoesNotContain("reusable", success.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_catalogue_rejects_inconsistent_grants()
    {
        var success = QuestionCatalogue.Default["task_succeeded"];
        var opportunity = QuestionCatalogue.Default["skill_opportunity"];

        // A question cannot require what it grants.
        Assert.Contains("cannot require", Assert.Throws<ArgumentException>(() =>
            new QuestionCatalogue([success with { Requires = [FabricConditions.TaskSucceeded] }])).Message, StringComparison.Ordinal);
        // One granter per condition.
        Assert.Contains("more than one", Assert.Throws<ArgumentException>(() =>
            new QuestionCatalogue([success, success with { Id = "also_succeeded" }])).Message, StringComparison.Ordinal);
        // A requirer at another checkpoint could never see the grant.
        Assert.Contains("shares no checkpoint", Assert.Throws<ArgumentException>(() =>
            new QuestionCatalogue([success, opportunity with { Checkpoints = [Checkpoint.Step] }])).Message, StringComparison.Ordinal);
        Assert.Equal(2, new QuestionCatalogue([success, opportunity]).Ids.Count);
    }

    [Fact]
    public async Task A_failed_task_holds_back_a_confident_skill_opportunity()
    {
        // Beacon #649: averaging a high opportunity score with a failed task let the turn through. Here the precondition is
        // its own floor, so P(yes) = 0.99 is logged and held back.
        var (result, logged) = await EgressAsync(Yes(0.99, 0.4), Evidence, "skill_opportunity", "task_succeeded");

        Assert.False(result["task_succeeded"].Verdict.Acted);
        Assert.Equal("task_unconfirmed", result["task_succeeded"].Verdict.Action);
        var opportunity = result["skill_opportunity"].Verdict;
        Assert.False(opportunity.Acted);
        Assert.Equal("nothing", opportunity.Action);
        Assert.Equal("missing condition task_succeeded", opportunity.Reason);
        Assert.Equal(["skill_opportunity", "task_succeeded"], logged.Select(decision => decision.QuestionId));
        Assert.Equal(["nothing", "task_unconfirmed"], logged.Select(decision => decision.Action));
        Assert.Equal(0.99, logged[0].Probabilities[DecisionQuestion.Yes], 6);
    }

    [Fact]
    public async Task A_confirmed_task_lets_skill_opportunity_act()
    {
        var (result, logged) = await EgressAsync(Yes(0.9, 0.85), Evidence, "skill_opportunity", "task_succeeded");

        Assert.Equal("confirm_task_success", result["task_succeeded"].Verdict.Action);
        Assert.Equal("queue_skill_distillation", result["skill_opportunity"].Verdict.Action);
        Assert.Equal(["queue_skill_distillation", "confirm_task_success"], logged.Select(decision => decision.Action));
    }

    [Fact]
    public async Task An_unrecovered_tool_failure_keeps_the_task_unconfirmed_whatever_the_answer()
    {
        var (result, _) = await EgressAsync(Yes(0.99, 0.99), Fixtures.Conditions(FabricConditions.ProcedureEvidence),
            "skill_opportunity", "task_succeeded");

        Assert.Equal("missing condition tool_failures_recovered", result["task_succeeded"].Verdict.Reason);
        Assert.Equal("missing condition task_succeeded", result["skill_opportunity"].Verdict.Reason);
    }

    [Fact]
    public async Task A_caller_cannot_assert_task_success()
    {
        var conditions = Fixtures.Conditions(FabricConditions.ProcedureEvidence, FabricConditions.ToolFailuresRecovered,
            FabricConditions.TaskSucceeded);

        // Not asked at all: the caller's condition is ignored.
        var (alone, _) = await EgressAsync(Yes(0.99, 0.99), conditions, "skill_opportunity");
        // Asked and below its floor: the caller's condition still does not count.
        var (below, _) = await EgressAsync(Yes(0.99, 0.5), conditions, "skill_opportunity", "task_succeeded");

        Assert.Equal("missing condition task_succeeded", alone["skill_opportunity"].Verdict.Reason);
        Assert.Equal("missing condition task_succeeded", below["skill_opportunity"].Verdict.Reason);
    }

    [Fact]
    public async Task Granters_go_first_whatever_the_key_order()
    {
        // A granter whose key sorts after its requirer, and one that sorts before it, both grant before the requirer is evaluated.
        var success = QuestionCatalogue.Default["task_succeeded"];
        var catalogue = new QuestionCatalogue(QuestionCatalogue.Default.Questions
            .Where(question => question.Id != "task_succeeded")
            .Append(success with { Id = "a_task_succeeded" }));
        var floors = FloorConfiguration.Default with
        {
            Version = "grant.1",
            Floors = FloorConfiguration.Default.Floors.Remove("task_succeeded").Add("a_task_succeeded", 0.8),
        };
        using var db = new TempLog();
        var early = new DecisionFabric(new DecisionPolicy(catalogue, floors), new DegradedDecider(new KeywordEmbedder("x"), ScriptedChatClient.Always("no")),
            db.Log, FakeCalibratedDecider.Answering("kev-test", (_, question) => DecisionAnswer.For(question, [0.95, 0.05])));
        var earlyResult = await early.DecideAsync(Session, Checkpoint.Egress, Fixtures.State(),
            [KeyValuePair.Create("skill_opportunity", catalogue["skill_opportunity"].ToQuestion()),
             KeyValuePair.Create("a_task_succeeded", catalogue["a_task_succeeded"].ToQuestion())], Evidence, Ct);
        var (late, _) = await EgressAsync(Yes(0.95, 0.95), Evidence, "task_succeeded", "skill_opportunity");

        Assert.True(earlyResult["skill_opportunity"].Verdict.Acted);
        Assert.True(late["skill_opportunity"].Verdict.Acted);
        // Events stay in key order.
        Assert.Equal(["a_task_succeeded", "skill_opportunity"], earlyResult.Events.Select(item => ((Decision)item.Payload).QuestionId));
    }

    [Fact]
    public async Task Without_kev_the_generator_answers_both_and_the_grant_still_applies()
    {
        using var db = new TempLog();
        var chat = new ScriptedChatClient(prompt => prompt.Contains("accomplish", StringComparison.Ordinal) ? "no" : "yes");
        var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new KeywordEmbedder("x"), chat), db.Log);

        var result = await fabric.DecideAsync(Session, Checkpoint.Egress, Fixtures.State(),
            [Fixtures.Ask("skill_opportunity"), Fixtures.Ask("task_succeeded")], Evidence, Ct);

        Assert.Equal(2, chat.Prompts.Count);
        Assert.False(result["task_succeeded"].Answer!.Calibrated);
        Assert.Equal("missing condition task_succeeded", result["skill_opportunity"].Verdict.Reason);
    }
}
