using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Kernel.Events;

namespace Corvid.Fabric.Tests;

public sealed class FabricTests
{
    private const string Session = "s-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DegradedDecider Degraded(ScriptedChatClient? chat = null) =>
        new(new KeywordEmbedder("reminder", "send"), chat ?? ScriptedChatClient.Always("no"));

    private static DecisionAnswer KevAnswer(string key, DecisionQuestion question) => key switch
    {
        "risk" => DecisionAnswer.For(question, [0.97, 0.03, 0, 0, 0]),
        "tool" => DecisionAnswer.For(question, [0.9, 0.05, 0.05]),
        _ => DecisionAnswer.For(question, question.Options.Select((_, i) => i == 0 ? 0.9 : 0.1 / (question.Options.Length - 1))),
    };

    [Fact]
    public async Task Every_answer_is_logged_as_a_decision_with_threshold_action_and_version()
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("yes")), db.Log);

        var result = await fabric.DecideAsync(Session, Checkpoint.Ingress, Fixtures.State(),
            [Fixtures.Ask("intent"), Fixtures.Ask("tool", Fixtures.Tools), Fixtures.Ask("difficulty"), Fixtures.Ask("clarify")],
            cancellationToken: Ct);

        Assert.Equal("degraded", result.Decider);
        Assert.Null(result.Fallback);
        Assert.Equal(4, result.Events.Length);
        var decisions = db.Log.Read(Session, cancellationToken: Ct).Select(e => Assert.IsType<Decision>(e.Payload)).ToList();
        Assert.Equal(["clarify", "difficulty", "intent", "tool"], decisions.Select(d => d.QuestionId));
        Assert.All(decisions, d =>
        {
            Assert.Equal("2026-10-18.1", d.CatalogueVersion);
            Assert.Equal("degraded", d.Decider);
            var verdict = result[d.QuestionId];
            Assert.Equal(verdict.DecisionId, d.DecisionId);
            Assert.Equal(verdict.Verdict.Action, d.Action);
            Assert.Equal(verdict.Verdict.Threshold, d.Threshold);
        });
        var tool = decisions.Single(d => d.QuestionId == "tool");
        Assert.Equal("offer_tool:cron.update", tool.Action);
        // The state and the question as asked are kept for calibration and Kev training export (C-021).
        // The log escapes non-ASCII and quotes, so compare values rather than raw text.
        Assert.All(decisions, d => Assert.Equal(Fixtures.Input().UserMessage, d.State!.Value.GetProperty("user_message").GetString()));
        Assert.All(decisions, d => Assert.Equal("qwen3.5-0.8b local, 8k", d.State!.Value.GetProperty("generator").GetString()));
        Assert.Equal("choice", tool.Question!.Kind);
        Assert.Equal("Which tool should run first?", tool.Question.Instructions.GetString());
        Assert.Equal(Fixtures.Tools.Select(option => (option.Name, option.Description)),
            tool.Question.Options.Select(option => (option.Name, option.Description)));
        Assert.Equal(["yes", "no"], decisions.Single(d => d.QuestionId == "clarify").Question!.Options.Select(option => option.Name));
        // The degraded tier's answer is recorded as uncalibrated, so calibration never reads it from the decider's name.
        Assert.False(tool.Calibrated);
        Assert.Equal(0.05, tool.Threshold);
        Assert.Equal(3, tool.Probabilities.Count);
        Assert.Equal("ask_clarifying_question", decisions.Single(d => d.QuestionId == "clarify").Action);
        var difficulty = decisions.Single(d => d.QuestionId == "difficulty");
        Assert.Equal("stay_on_generator", difficulty.Action);
        Assert.Equal(3.5, difficulty.Threshold);
    }

    [Fact]
    public async Task Outcomes_attach_to_logged_decisions_in_the_projection()
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log);

        var result = await fabric.DecideAsync(Session, Checkpoint.Step, Fixtures.State(step: 1),
            [Fixtures.Ask("done"), Fixtures.Ask("clear_results/call-1"), Fixtures.Ask("clear_results/call-2")], cancellationToken: Ct);
        fabric.RecordOutcome(Session, result["done"].DecisionId, "user_retried", Ct);

        var snapshot = SessionProjection.Load(db.Log, Session, cancellationToken: Ct);
        Assert.Equal(3, snapshot.Decisions.Count);
        Assert.Equal("user_retried", snapshot.Decisions[result["done"].DecisionId].Outcome);
        Assert.Empty(snapshot.Issues);
        Assert.Equal("stub_older_than_last_2", result["clear_results/call-2"].Verdict.Action);
        var stored = Assert.IsType<Decision>(snapshot.Decisions[result["clear_results/call-1"].DecisionId].Decision);
        Assert.Equal("clear_results/call-1", stored.QuestionId);
        Assert.Empty(stored.Probabilities);
    }

    [Fact]
    public async Task Degraded_gates_always_ask_or_stage()
    {
        using var db = new TempLog();
        var chat = ScriptedChatClient.Always("yes");
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(chat), db.Log);
        var everything = Fixtures.Conditions(FabricConditions.PermissiveMode, FabricConditions.AgentAuthored);

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(),
            [Fixtures.Ask("risk"), Fixtures.Ask("skill_write_safe"), Fixtures.Ask("memory_op")], everything, Ct);

        Assert.Equal("ask", result["risk"].Verdict.Action);
        Assert.Equal("stage_for_review", result["skill_write_safe"].Verdict.Action);
        Assert.Equal("stage_for_review", result["memory_op"].Verdict.Action);
        Assert.All(result.Verdicts.Values, v => Assert.Null(v.Answer));
        Assert.Empty(chat.Prompts);
        Assert.Equal(3, db.Log.GetHead(Session, Ct));
    }

    [Fact]
    public async Task A_calibrated_decider_answers_when_available()
    {
        using var db = new TempLog();
        var chat = ScriptedChatClient.Always("yes");
        var kev = FakeCalibratedDecider.Answering("kev-test", KevAnswer);
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(chat), db.Log, kev);

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(),
            [Fixtures.Ask("risk"), Fixtures.Ask("memory_op")], Fixtures.Conditions(FabricConditions.PermissiveMode), Ct);

        Assert.Equal("kev-test", result.Decider);
        Assert.Equal("auto_run", result["risk"].Verdict.Action);
        Assert.Equal("apply:add", result["memory_op"].Verdict.Action);
        Assert.Single(kev.Requests);
        Assert.Equal(2, kev.Requests[0].Questions.Count);
        Assert.Empty(chat.Prompts);
        Assert.All(db.Log.Read(Session, cancellationToken: Ct), e => Assert.Equal("kev-test", ((Decision)e.Payload).Decider));
        Assert.All(db.Log.Read(Session, cancellationToken: Ct), e => Assert.True(((Decision)e.Payload).Calibrated));
    }

    [Theory]
    [InlineData(DeciderUnavailableReason.Refused, "refused")]
    [InlineData(DeciderUnavailableReason.StateOverBudget, "state_over_budget")]
    [InlineData(DeciderUnavailableReason.Failed, "failed")]
    public async Task An_unavailable_decider_falls_back_and_logs_why(DeciderUnavailableReason reason, string wire)
    {
        using var db = new TempLog();
        var kev = FakeCalibratedDecider.Declining("kev-test", reason);
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, kev);

        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, Fixtures.State(), [Fixtures.Ask("risk")], cancellationToken: Ct);

        Assert.Equal($"degraded after kev-test: {wire}", result.Decider);
        Assert.Equal(reason, result.Fallback!.Reason);
        Assert.Equal("ask", result["risk"].Verdict.Action);
        Assert.Equal(result.Decider, ((Decision)Assert.Single(db.Log.Read(Session, cancellationToken: Ct)).Payload).Decider);
    }

    [Fact]
    public async Task A_state_over_the_hard_stop_never_reaches_the_calibrated_decider()
    {
        using var db = new TempLog();
        var kev = FakeCalibratedDecider.Answering("kev-test", KevAnswer);
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(ScriptedChatClient.Always("yes")), db.Log, kev);
        var state = new TurnStateBuilder(budget: new TurnStateBudget(Surface: 4_000))
            .Build(Fixtures.Input() with { Surface = new string('s', 8_000) });

        var result = await fabric.DecideAsync(Session, Checkpoint.Step, state, [Fixtures.Ask("done")], cancellationToken: Ct);

        Assert.True(state.ExceedsHardStop);
        Assert.Empty(kev.Requests);
        Assert.Equal(DeciderUnavailableReason.StateOverBudget, result.Fallback!.Reason);
        Assert.Equal("degraded after kev-test: state_over_budget", result.Decider);
        Assert.Equal("stop", result["done"].Verdict.Action);
    }

    [Fact]
    public async Task A_throwing_decider_is_treated_as_failed()
    {
        using var db = new TempLog();
        var kev = new FakeCalibratedDecider("kev-test", _ => throw new InvalidOperationException("native crash"));
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, kev);

        var result = await fabric.DecideAsync(Session, Checkpoint.Step, Fixtures.State(), [Fixtures.Ask("stuck")], cancellationToken: Ct);

        Assert.Equal(DeciderUnavailableReason.Failed, result.Fallback!.Reason);
        Assert.Equal("native crash", result.Fallback.Detail);
    }

    [Fact]
    public async Task Malformed_answers_take_the_below_floor_action()
    {
        using var db = new TempLog();
        var question = Fixtures.Question("tool", Fixtures.Tools);
        // Answers a different shortlist than the one asked.
        var kev = FakeCalibratedDecider.Answering("kev-test", (_, _) =>
            DecisionAnswer.For(DecisionQuestion.Choice("x", "a", "b", "c"), [1, 0, 0]));
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, kev);

        var result = await fabric.DecideAsync(Session, Checkpoint.Ingress, Fixtures.State(), [KeyValuePair.Create("tool", question)],
            cancellationToken: Ct);

        Assert.Null(result["tool"].Answer);
        Assert.Equal("offer_shortlist", result["tool"].Verdict.Action);
    }

    [Fact]
    public async Task Questions_must_belong_to_the_checkpoint_and_the_catalogue()
    {
        using var db = new TempLog();
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log);
        var state = Fixtures.State();

        await Assert.ThrowsAsync<ArgumentException>(() => fabric.DecideAsync(Session, Checkpoint.Ingress, state, [Fixtures.Ask("done")], cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => fabric.DecideAsync(Session, Checkpoint.Step, state,
            [KeyValuePair.Create("done", DecisionQuestion.Choice("x", "a", "b"))], cancellationToken: Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fabric.DecideAsync(Session, Checkpoint.Step, state,
            [KeyValuePair.Create("vibes", DecisionQuestion.Noul("x"))], cancellationToken: Ct));
        Assert.Equal(0, db.Log.GetHead(Session, Ct));
    }

    [Fact]
    public async Task Fixed_catalogue_options_cannot_be_reordered_or_replaced()
    {
        using var db = new TempLog();
        // Certain of the first option: index 0 would read as risk level 0 and auto-run in permissive mode.
        var calibrated = FakeCalibratedDecider.Answering("kev-0.8b",
            (_, question) => DecisionAnswer.For(question, question.Options.Select((_, i) => i == 0 ? 1.0 : 0.0)));
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), db.Log, calibrated);
        var state = Fixtures.State();
        var permissive = Fixtures.Conditions(FabricConditions.PermissiveMode);
        var levels = QuestionCatalogue.Default["risk"].Options.Select(option => option.Name).Reverse().ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => fabric.DecideAsync(Session, Checkpoint.PreExecute, state,
            [KeyValuePair.Create("risk", DecisionQuestion.Score("How risky?", "destructive", "none"))], permissive, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => fabric.DecideAsync(Session, Checkpoint.PreExecute, state,
            [KeyValuePair.Create("risk", DecisionQuestion.Score("How risky?", levels))], permissive, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => fabric.DecideAsync(Session, Checkpoint.PreExecute, state,
            [KeyValuePair.Create("memory_op", DecisionQuestion.Choice("x", "add", "noop"))], cancellationToken: Ct));
        Assert.Equal(0, db.Log.GetHead(Session, Ct));
        Assert.Empty(calibrated.Requests);

        // The catalogue's options with the caller's own instructions are accepted.
        var risk = QuestionCatalogue.Default["risk"].ToQuestion(instructions: DecisionContent.FromText("rm -rf build/"));
        var result = await fabric.DecideAsync(Session, Checkpoint.PreExecute, state, [KeyValuePair.Create("risk", risk)], permissive, Ct);
        Assert.Equal(FabricActions.AutoRun, result["risk"].Verdict.Action);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task A_concurrent_append_is_retried_at_the_new_head()
    {
        using var db = new TempLog();
        var log = new InterferingLog(db.Log) { BeforeNextAppend = () => new UserMessage("meanwhile") };
        var fabric = new DecisionFabric(DecisionPolicy.Default, Degraded(), log);

        var result = await fabric.DecideAsync(Session, Checkpoint.Egress, Fixtures.State(), [Fixtures.Ask("remember")], cancellationToken: Ct);

        Assert.Equal(2, Assert.Single(result.Events).Sequence);
    }

    /// <summary>Appends from "another writer" just before the next append, to force a head conflict.</summary>
    private sealed class InterferingLog(IEventLog inner) : IEventLog
    {
        public Func<SessionEvent>? BeforeNextAppend { get; set; }

        public long GetHead(string sessionId, CancellationToken cancellationToken = default) => inner.GetHead(sessionId, cancellationToken);

        public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
            CancellationToken cancellationToken = default)
        {
            if (BeforeNextAppend is { } interfere)
            {
                BeforeNextAppend = null;
                inner.Append(sessionId, inner.GetHead(sessionId, cancellationToken), [EventData.Create(interfere())], cancellationToken);
            }
            return inner.Append(sessionId, expectedHead, events, cancellationToken);
        }

        public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue,
            int limit = 256, CancellationToken cancellationToken = default) =>
            inner.Read(sessionId, afterSequence, throughSequence, limit, cancellationToken);
    }
}
