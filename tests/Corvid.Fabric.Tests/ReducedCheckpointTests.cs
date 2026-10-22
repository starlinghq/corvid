using System.Collections.Concurrent;
using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

/// <summary>
/// C-030: the reduced checkpoint profile. A step checkpoint asks at most two questions (done, stuck, tool in that order);
/// the others take their below-floor action without reaching any decider and are still logged, as skipped. Ingress,
/// egress, pre_execute and tick are asked whole. A caller may skip keys it can prove do not change the outcome.
/// </summary>
public sealed class ReducedCheckpointTests
{
    private static readonly CheckpointReduction Cpu = new("kev-test on cpu");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSession() => "s-" + Guid.NewGuid().ToString("N");

    private static DegradedDecider Degraded(ScriptedChatClient chat, KeywordEmbedder? embedder = null) =>
        new(embedder ?? new KeywordEmbedder("reminder", "send"), chat);

    // Confident on the first option of everything: "yes" for a Noul, "keep" for clear_results, level 0 for risk.
    private static FakeCalibratedDecider Kev() => FakeCalibratedDecider.Answering("kev-test", (_, question) =>
        DecisionAnswer.For(question, question.Options.Select((_, i) => i == 0 ? 0.97 : 0.03 / (question.Options.Length - 1))));

    private static KeyValuePair<string, DecisionQuestion>[] StepRequest =>
    [
        Fixtures.Ask("done"), Fixtures.Ask("stuck"), Fixtures.Ask("tool", Fixtures.Tools),
        Fixtures.Ask("clear_results/c1"), Fixtures.Ask("injection/c2"),
    ];

    [Fact]
    public async Task A_reduced_step_asks_done_and_stuck_and_logs_the_rest_as_skipped()
    {
        using var db = new TempLog();
        var kev = Kev();
        var chat = ScriptedChatClient.Always("yes");
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(chat), db.Log, kev, reduction: Cpu);
        var session = NewSession();

        var result = await fabric.DecideAsync(session, Checkpoint.Step, Fixtures.State(step: 1), StepRequest, cancellationToken: Ct);

        var request = Assert.Single(kev.Requests);
        Assert.Equal(["done", "stuck"], request.Questions.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(chat.Prompts);
        Assert.Equal("reduced", result.Profile);
        Assert.Equal("kev-test", result.Decider);

        var decisions = db.Log.Read(session, cancellationToken: Ct).Select(e => Assert.IsType<Decision>(e.Payload)).ToList();
        Assert.Equal(["clear_results/c1", "done", "injection/c2", "stuck", "tool"], decisions.Select(d => d.QuestionId));
        foreach (var key in new[] { "clear_results/c1", "injection/c2", "tool" })
        {
            var decision = decisions.Single(d => d.QuestionId == key);
            Assert.Null(decision.Calibrated);
            Assert.Empty(decision.Probabilities);
            Assert.Equal(Fixtures.Catalogue.Resolve(key).BelowFloor, decision.Action);
            Assert.Equal("degraded skipped: step capped at 2 (kev-test on cpu)", decision.Decider);
            Assert.True(result[key].Skipped);
            Assert.False(result[key].Verdict.Acted);
            Assert.StartsWith("skipped: ", result[key].Verdict.Reason, StringComparison.Ordinal);
            // The question as it would have been asked is still kept for calibration export.
            Assert.NotNull(decision.Question);
        }
        Assert.Equal("stub_older_than_last_2", result["clear_results/c1"].Verdict.Action);
        Assert.Equal("treat_as_tainted", result["injection/c2"].Verdict.Action);
        Assert.Equal("offer_shortlist", result["tool"].Verdict.Action);
        Assert.All(["done", "stuck"], key =>
        {
            Assert.False(result[key].Skipped);
            Assert.Equal("kev-test", result[key].Decider);
            Assert.True(decisions.Single(d => d.QuestionId == key).Calibrated);
        });
        Assert.Equal("stop", result["done"].Verdict.Action);
    }

    [Fact]
    public async Task The_no_op_check_shape_asks_done_and_tool()
    {
        using var db = new TempLog();
        var kev = Kev();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("no")), db.Log, kev, reduction: Cpu);

        var result = await fabric.DecideAsync(NewSession(), Checkpoint.Step, Fixtures.State(step: 1),
            [Fixtures.Ask("done"), Fixtures.Ask("tool", Fixtures.Tools), Fixtures.Ask("clear_results/c1")], cancellationToken: Ct);

        Assert.Equal(["done", "tool"], Assert.Single(kev.Requests).Questions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("offer_tool:cron.update", result["tool"].Verdict.Action);
        Assert.True(result["clear_results/c1"].Skipped);
    }

    public static TheoryData<Checkpoint, string[]> WholeCheckpoints => new()
    {
        { Checkpoint.Ingress, ["intent", "tool", "clarify", "needs_recall", "skill", "injection/mcp:docs", "injection/skill:x"] },
        { Checkpoint.Egress, ["remember", "deliver", "task_succeeded", "skill_opportunity", "lesson_opportunity"] },
        { Checkpoint.PreExecute, ["risk", "memory_op", "skill_write_safe"] },
        { Checkpoint.Tick, ["wake"] },
    };

    [Theory]
    [MemberData(nameof(WholeCheckpoints))]
    public async Task Ingress_egress_pre_execute_and_tick_are_asked_whole(Checkpoint checkpoint, string[] keys)
    {
        using var db = new TempLog();
        var kev = Kev();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("no")), db.Log, kev, reduction: Cpu);
        var questions = keys.Select(key => Fixtures.Catalogue.Resolve(key).HasFixedOptions || Fixtures.Catalogue.Resolve(key).Kind == QuestionKind.Noul
            ? Fixtures.Ask(key)
            : Fixtures.Ask(key, Fixtures.Tools)).ToArray();

        var result = await fabric.DecideAsync(NewSession(), checkpoint, Fixtures.State(), questions, cancellationToken: Ct);

        Assert.Equal(keys.Order(StringComparer.Ordinal), Assert.Single(kev.Requests).Questions.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Verdicts.Values, verdict => Assert.False(verdict.Skipped));
    }

    [Fact]
    public async Task The_full_profile_asks_every_step_question()
    {
        using var db = new TempLog();
        var kev = Kev();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("no")), db.Log, kev);

        var result = await fabric.DecideAsync(NewSession(), Checkpoint.Step, Fixtures.State(step: 1), StepRequest, cancellationToken: Ct);

        Assert.Null(fabric.Reduction);
        Assert.Equal("full", result.Profile);
        Assert.Equal(5, Assert.Single(kev.Requests).Questions.Count);
        Assert.All(result.Verdicts.Values, verdict => Assert.False(verdict.Skipped));
        Assert.All(result.Verdicts.Values, verdict => Assert.Equal("kev-test", verdict.Decider));
    }

    [Fact]
    public async Task On_the_degraded_tier_the_cap_applies_too_and_skipped_questions_cost_no_generator_call()
    {
        using var db = new TempLog();
        var chat = ScriptedChatClient.Always("no");
        var embedder = new KeywordEmbedder("reminder", "send");
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(chat, embedder), db.Log, reduction: new("configured"));

        var result = await fabric.DecideAsync(NewSession(), Checkpoint.Step, Fixtures.State(step: 1), StepRequest, cancellationToken: Ct);

        // done is asked of the generator; stuck is not (an uncalibrated yes never diverts the turn); tool is skipped, so
        // nothing is embedded.
        Assert.Single(chat.Prompts);
        Assert.Contains("fully handled", chat.Prompts[0], StringComparison.Ordinal);
        Assert.Empty(embedder.Batches);
        Assert.Equal("degraded", result["done"].Decider);
        Assert.Equal("degraded skipped: step capped at 2 (configured)", result["tool"].Decider);
    }

    [Fact]
    public async Task When_every_question_is_skipped_no_decider_is_called()
    {
        using var db = new TempLog();
        var kev = Kev();
        var second = Kev();
        var chat = ScriptedChatClient.Always("yes");
        var embedder = new KeywordEmbedder("reminder");
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(chat, embedder), db.Log, kev, second, Cpu);
        var session = NewSession();

        var ingress = await fabric.DecideAsync(session, Checkpoint.Ingress, Fixtures.State(), [Fixtures.Ask("injection/skill:x")], null,
            new Dictionary<string, string> { ["injection/skill:x"] = "verdict unused" }, Ct);
        var step = await fabric.DecideAsync(session, Checkpoint.Step, Fixtures.State(step: 1),
            [Fixtures.Ask("clear_results/c1"), Fixtures.Ask("injection/c2")], cancellationToken: Ct);
        var risk = await fabric.DecideAsync(session, Checkpoint.PreExecute, Fixtures.State(), [Fixtures.Ask("risk")], null,
            new Dictionary<string, string> { ["risk"] = "the outcome is ask" }, Ct);

        Assert.Empty(kev.Requests);
        Assert.Empty(second.Requests);
        Assert.Empty(chat.Prompts);
        Assert.Empty(embedder.Batches);
        Assert.Equal("degraded skipped: verdict unused", ingress.Decider);
        Assert.Equal("treat_as_tainted", ingress["injection/skill:x"].Verdict.Action);
        Assert.All(step.Verdicts.Values, verdict => Assert.True(verdict.Skipped));
        Assert.Equal("ask", risk["risk"].Verdict.Action);
        Assert.Equal("degraded skipped: the outcome is ask", risk["risk"].Decider);
        Assert.Equal(4, db.Log.GetHead(session, Ct));
    }

    [Fact]
    public async Task A_caller_skip_must_name_a_key_of_the_request()
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("no")), db.Log);

        await Assert.ThrowsAsync<ArgumentException>(() => fabric.DecideAsync(NewSession(), Checkpoint.PreExecute, Fixtures.State(),
            [Fixtures.Ask("risk")], null, new Dictionary<string, string> { ["memory_op"] = "not asked" }, Ct));
    }

    [Fact]
    public async Task Spans_record_the_profile_and_the_skipped_keys_with_reasons()
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("no")), db.Log, Kev(), reduction: Cpu);
        var full = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("no")), db.Log, Kev());
        var session = NewSession();
        // Listeners are process-wide and tests run in parallel: keep only this session's spans.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FabricTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("gen_ai.conversation.id") as string == session)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        await fabric.DecideAsync(session, Checkpoint.Step, Fixtures.State(step: 1), StepRequest, cancellationToken: Ct);
        await fabric.DecideAsync(session, Checkpoint.Ingress, Fixtures.State(), [Fixtures.Ask("injection/skill:x")], null,
            new Dictionary<string, string> { ["injection/skill:x"] = "verdict unused" }, Ct);
        await full.DecideAsync(session, Checkpoint.Step, Fixtures.State(step: 1), StepRequest, cancellationToken: Ct);

        var all = spans.ToArray();
        Assert.Equal(3, all.Length);
        var (step, skill, whole) = (all[0], all[1], all[2]);
        Assert.Equal("reduced", step.GetTagItem("corvid.checkpoint.profile"));
        Assert.Equal(["clear_results/c1", "injection/c2", "tool"], Assert.IsType<string[]>(step.GetTagItem("corvid.decide.skipped")));
        Assert.Equal(Enumerable.Repeat("step capped at 2 (kev-test on cpu)", 3), Assert.IsType<string[]>(step.GetTagItem("corvid.decide.skip_reasons")));
        Assert.Equal("kev-test", step.GetTagItem("corvid.decider"));
        // Skipped keys are not reported as escalated.
        Assert.Null(step.GetTagItem("corvid.decider.escalated"));
        var events = step.Events.Where(e => e.Name == "corvid.decision")
            .ToDictionary(e => (string)e.Tags.Single(tag => tag.Key == "corvid.question.id").Value!,
                e => e.Tags.ToDictionary(tag => tag.Key, tag => tag.Value));
        Assert.Equal(5, events.Count);
        Assert.Equal(true, events["tool"]["corvid.skipped"]);
        Assert.Equal("degraded skipped: step capped at 2 (kev-test on cpu)", events["tool"]["corvid.decider"]);
        Assert.Equal(false, events["done"]["corvid.skipped"]);
        Assert.Equal("kev-test", events["done"]["corvid.decider"]);

        // A fully skipped checkpoint still has its span, with only skipped keys.
        Assert.Equal(["injection/skill:x"], Assert.IsType<string[]>(skill.GetTagItem("corvid.decide.questions")));
        Assert.Equal(["injection/skill:x"], Assert.IsType<string[]>(skill.GetTagItem("corvid.decide.skipped")));
        Assert.Equal(["verdict unused"], Assert.IsType<string[]>(skill.GetTagItem("corvid.decide.skip_reasons")));

        Assert.Equal("full", whole.GetTagItem("corvid.checkpoint.profile"));
        Assert.Empty(Assert.IsType<string[]>(whole.GetTagItem("corvid.decide.skipped")));
    }
}
