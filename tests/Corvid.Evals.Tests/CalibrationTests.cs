using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Evals.Calibration;
using Corvid.Fabric;
using static Corvid.Evals.Tests.Decisions;

namespace Corvid.Evals.Tests;

public sealed class DecisionSetTests : IDisposable
{
    private readonly SyntheticLog _log = new();

    public void Dispose() => _log.Dispose();

    [Fact]
    public void Decisions_join_their_outcomes_and_keys_split_on_the_separator()
    {
        var done = Done(0.9);
        var clear = new Decision(NewId(), "clear_results/call-7", Map(("keep", 0.8), ("stub", 0.1), ("drop", 0.1)), 0.7, 0.6,
            "apply:keep", FloorsVersion, Kev, Calibrated: true);
        var unknown = new Decision(NewId(), "no_such_question", Map(("yes", 1)), 1, 0.5, "x", FloorsVersion, Kev, Calibrated: true);
        _log.Add("s-1", done, clear, unknown,
            new DecisionOutcomeRecorded(done.DecisionId, "tool_succeeded"),
            new DecisionOutcomeRecorded(done.DecisionId, "user_retried"),
            new DecisionOutcomeRecorded("missing", "approved"));
        _log.Add("s-0", new UserMessage("hi"));

        var set = _log.Load();

        Assert.Equal(["s-0", "s-1"], set.Sessions.Select(session => session.SessionId));
        Assert.Equal(3, set.Records.Length);
        var first = set.Records[0];
        Assert.Equal(["tool_succeeded", "user_retried"], first.Outcomes);
        // The last outcome labels the decision: a retry after a success means the stop was wrong.
        Assert.Equal(OutcomeLabel.Negative, first.Label);
        var instance = set.Records[1];
        Assert.Equal(("clear_results/call-7", "clear_results", "call-7"), (instance.Key, instance.QuestionId, instance.Instance));
        Assert.Equal(OutcomeLabel.None, instance.Label);
        Assert.Null(set.Records[2].Question);
        Assert.False(set.Records[2].Acted);
        Assert.Equal("missing", Assert.Single(set.OrphanOutcomes).DecisionId);
    }

    [Fact]
    public void A_missing_log_is_an_error_and_is_not_created()
    {
        var path = Path.Combine(_log.Root, "typo.db");

        Assert.Throws<FileNotFoundException>(() => DecisionSet.Load([path], cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("degraded", true, DecisionTier.Degraded)]
    [InlineData("degraded", null, DecisionTier.Degraded)]
    [InlineData("degraded after kev-0.8b: state_over_budget", true, DecisionTier.Degraded)]
    [InlineData("kev-0.8b", true, DecisionTier.Calibrated)]
    [InlineData("kev-0.8b", false, DecisionTier.Degraded)]
    [InlineData("degraded-but-not-really", false, DecisionTier.Degraded)]
    // Fails closed: a decider name alone never makes an answer calibrated.
    [InlineData("kev-0.8b", null, DecisionTier.Unknown)]
    [InlineData("mystery-decider", null, DecisionTier.Unknown)]
    [InlineData(null, true, DecisionTier.Unknown)]
    public void Tiers_come_from_the_recorded_calibration_flag_and_decider(string? decider, bool? calibrated, DecisionTier tier) =>
        Assert.Equal(tier, DecisionSet.TierOf(Done(0.9, decider: decider!) with { Calibrated = calibrated }));

    [Fact]
    public void An_unanswered_decision_keeps_its_deciders_tier()
    {
        var unanswered = new Decision(NewId(), "done", ImmutableDictionary<string, double>.Empty, 0, 0.8, "continue", FloorsVersion, Kev);

        Assert.Equal(DecisionTier.Calibrated, DecisionSet.TierOf(unanswered));
        Assert.Equal(DecisionTier.Degraded, DecisionSet.TierOf(unanswered with { Decider = Degraded }));
    }

    [Fact]
    public void Measures_follow_each_questions_floor_measure_and_option_order()
    {
        // The stored map is unordered; difficulty's levels must come from the catalogue, not the map.
        var difficulty = new Decision(NewId(), "difficulty",
            Map(("needs a frontier model", 0.6), ("hard", 0.3), ("moderate", 0.1), ("easy", 0), ("trivial", 0)), 0.5, 3.5,
            "escalate_generator", FloorsVersion, Kev, Calibrated: true);
        var tool = new Decision(NewId(), "tool", Map(("cron.update", 0.7), ("none", 0.2), ("message.send", 0.1)), 0.55, 0.45,
            "offer_tool:cron.update", FloorsVersion, Kev, Calibrated: true);
        _log.Add("s", difficulty, tool, Done(0.85));

        var records = _log.Load().Records;

        Assert.Equal(3.5, records[0].Measure!.Value, 10);
        Assert.True(records[0].Acted);
        // (0.7 − 1/3)/(2/3)
        Assert.Equal(0.55, records[1].Measure!.Value, 10);
        Assert.Equal("cron.update", records[1].Answer!.Choice);
        Assert.Equal(0.85, records[2].Measure!.Value, 10);
    }

    [Fact]
    public void Risk_acts_by_threshold_because_ask_is_both_its_act_and_its_fallback()
    {
        string[] levels = ["none", "low", "moderate", "high", "destructive"];
        Decision Risk(int level, double p, string action) => new(NewId(), "risk",
            Map([.. levels.Select((name, i) => (name, i == level ? p : (1 - p) / 4))]), (p - 0.2) / 0.8, 0.9, action, FloorsVersion, Kev,
            Calibrated: true);
        _log.Add("s", Risk(4, 0.98, "ask"), Risk(4, 0.5, "ask"), Risk(1, 0.95, "approval_mode"));
        _log.Add("t", new Decision(NewId(), "risk", ImmutableDictionary<string, double>.Empty, 0, 0.9, "ask", FloorsVersion, Degraded));

        var records = _log.Load().Records;

        Assert.Equal([true, false, true], records.Where(record => record.SessionId == "s").Select(record => record.Acted));
        var degraded = records.Single(record => record.SessionId == "t");
        Assert.False(degraded.Answered);
        Assert.Null(degraded.Measure);
        Assert.False(degraded.Acted);
    }

    [Fact]
    public void Unknown_outcomes_are_unlabelled_unless_configured()
    {
        var decision = Done(0.9);
        _log.Add("s", WithOutcome(decision, "thumbs_up"));

        Assert.Equal(OutcomeLabel.Unlabelled, _log.Load().Records[0].Label);
        var custom = new OutcomeClassifier(OutcomeClassifier.Default.Positive.Add("thumbs_up"), OutcomeClassifier.Default.Negative);
        Assert.Equal(OutcomeLabel.Positive, _log.Load(custom).Records[0].Label);
        Assert.Throws<ArgumentException>(() => new OutcomeClassifier(["x"], ["x"]));
    }

    [Fact]
    public async Task Decisions_from_the_fabric_carry_state_and_question_through_to_the_set()
    {
        var fabric = new DecisionFabric(DecisionPolicy.Default, new FixedDecider(Degraded, 0.5), _log.Log, new FixedDecider(Kev, 0.9));
        var state = new TurnStateBuilder().Build(new TurnStateInput("is it done?", "cli", "qwen3.5-0.8b local, 8k", Step: 2));

        var result = await fabric.DecideAsync("s", Checkpoint.Step, state,
            [KeyValuePair.Create("done", QuestionCatalogue.Default["done"].ToQuestion())], cancellationToken: TestContext.Current.CancellationToken);
        fabric.RecordOutcome("s", result["done"].DecisionId, "accepted", TestContext.Current.CancellationToken);

        var record = Assert.Single(_log.Load().Records);
        Assert.Equal(DecisionTier.Calibrated, record.Tier);
        Assert.True(record.Event.Calibrated);
        Assert.True(record.Acted);
        Assert.Equal(OutcomeLabel.Positive, record.Label);
        Assert.Equal("is it done?", record.Event.State!.Value.GetProperty("user_message").GetString());
        Assert.Equal(2, record.Event.State!.Value.GetProperty("step").GetInt32());
        Assert.Equal("Is the user's request fully handled?", record.Event.Question!.Instructions.GetString());
    }
}

public sealed class FloorFitterTests : IDisposable
{
    private readonly SyntheticLog _log = new();

    public void Dispose() => _log.Dispose();

    private void Many(int count, Func<Decision> decision, string? outcome)
    {
        for (var i = 0; i < count; i++)
        {
            var made = decision();
            _log.Add("s", outcome is null ? [made] : WithOutcome(made, outcome));
        }
    }

    private FitResult Fit(string version = "2026-11-01.1", int minSupport = 20) =>
        FloorFitter.Fit(_log.Load(), FloorConfiguration.Default, new FitOptions(version, MinSupport: minSupport));

    [Fact]
    public void A_routing_floor_drops_to_the_lowest_level_that_holds_its_target()
    {
        Many(10, () => Done(0.95), "accepted");
        Many(8, () => Done(0.7), "accepted");
        Many(2, () => Done(0.7), "user_retried");
        Many(3, () => Done(0.5), "accepted");
        Many(7, () => Done(0.5), "user_retried");

        var fit = Fit().Questions.Single(question => question.QuestionId == "done");

        Assert.Equal(FitStatus.Fitted, fit.Status);
        Assert.Equal(0.8, fit.Target);
        Assert.Equal(0.8, fit.CurrentFloor);
        Assert.Equal(0.7, fit.Floor, 10);
        Assert.Equal(30, fit.Samples);
        Assert.Equal((20, 0.9), (fit.AtFloor.Support, fit.AtFloor.Precision!.Value));
        Assert.Equal(20 / 30.0, fit.AtFloor.Coverage, 10);
        Assert.Equal((10, 1.0), (fit.AtCurrent.Support, fit.AtCurrent.Precision!.Value));
    }

    [Fact]
    public void A_gate_floor_holds_the_gate_target_and_ignores_uncalibrated_answers()
    {
        Many(20, () => MemoryOp(0.97), "approved");
        Many(5, () => MemoryOp(0.7), "approved");
        Many(5, () => MemoryOp(0.7), "rejected");
        // Uncalibrated answers that look perfect at a low level must not pull a gate floor down.
        Many(40, () => MemoryOp(0.4, decider: Degraded), "approved");

        var fit = Fit().Questions.Single(question => question.QuestionId == "memory_op");

        Assert.Equal(QuestionRole.Gate, fit.Role);
        Assert.Equal(0.9, fit.Target);
        Assert.Equal((0.97 - 0.25) / 0.75, fit.Floor, 10);
        Assert.Equal(30, fit.Samples);
        Assert.Equal(40, fit.Excluded);
    }

    [Fact]
    public void Degraded_decisions_alone_fit_nothing()
    {
        Many(50, () => Done(1.0, decider: Degraded), "accepted");
        Many(50, () => MemoryOp(0.9, decider: "degraded after kev-0.8b: failed"), "approved");

        var result = Fit();

        Assert.All(result.Questions, fit => Assert.Equal(FitStatus.InsufficientData, fit.Status));
        Assert.All(result.Questions, fit => Assert.False(fit.Changed));
        Assert.Equal(FloorConfiguration.Default.Floors, result.Floors.Floors);
        Assert.Equal(50, result.Questions.Single(fit => fit.QuestionId == "memory_op").Excluded);
    }

    [Fact]
    public void Answers_without_calibration_evidence_fit_nothing_whatever_the_decider_is_called()
    {
        // An unknown decider with no recorded flag, and a calibrated decider's answer marked uncalibrated.
        Many(30, () => MemoryOp(0.4, decider: "mystery-decider") with { Calibrated = null }, "approved");
        Many(30, () => MemoryOp(0.4) with { Calibrated = false }, "approved");

        var fit = Fit().Questions.Single(question => question.QuestionId == "memory_op");

        Assert.Equal((FitStatus.InsufficientData, 0, 60), (fit.Status, fit.Samples, fit.Excluded));
        Assert.False(fit.Changed);
        var report = CalibrationReport.Build(_log.Load());
        Assert.Empty(report.Calibrated);
        Assert.Equal(60, Assert.Single(report.Uncalibrated).Decisions);
    }

    [Fact]
    public void Too_little_data_or_an_unreachable_target_keeps_the_current_floor()
    {
        Many(5, () => Noul("clarify", 0.9, 0.7, "ask_clarifying_question", "proceed"), "accepted");
        Many(25, () => Noul("stuck", 0.9, 0.7, "change_strategy", "continue"), "corrected");
        Many(10, () => Done(0.9), null);

        var fits = Fit().Questions.ToDictionary(fit => fit.QuestionId);

        Assert.Equal((FitStatus.InsufficientData, 0.7), (fits["clarify"].Status, fits["clarify"].Floor));
        Assert.Equal((FitStatus.TargetUnreachable, 0.7), (fits["stuck"].Status, fits["stuck"].Floor));
        Assert.Equal(0.0, fits["stuck"].AtFloor.Precision);
        // Decisions without an outcome are not evidence.
        Assert.Equal(0, fits["done"].Samples);
    }

    [Fact]
    public void Expected_score_floors_stay_within_the_levels()
    {
        string[] levels = ["trivial", "easy", "moderate", "hard", "needs a frontier model"];
        Decision Difficulty(int level) => new(NewId(), "difficulty", Map([.. levels.Select((name, i) => (name, i == level ? 1.0 : 0))]),
            1, 3.5, level >= 3.5 ? "escalate_generator" : "stay_on_generator", FloorsVersion, Kev, Calibrated: true);
        Many(20, () => Difficulty(4), "accepted");
        Many(20, () => Difficulty(3), "accepted");
        Many(20, () => Difficulty(1), "user_corrected");

        var fit = Fit().Questions.Single(question => question.QuestionId == "difficulty");

        Assert.Equal(3, fit.Floor);
        var bins = CalibrationReport.Build(_log.Load()).Calibrated.Single(stats => stats.QuestionId == "difficulty").Reliability;
        // Expected scores are normalized by the top level for binning: 3/4 falls in [0.7, 0.8).
        Assert.Equal(20, bins[7].Count);
        Assert.Equal(20, bins[9].Count);
    }

    [Fact]
    public void The_new_floors_file_is_complete_versioned_and_loadable()
    {
        Many(20, () => Done(0.9), "accepted");

        var result = Fit("2026-10-19.cal");
        var reloaded = FloorConfiguration.Parse(result.Floors.ToJson());

        Assert.Equal("2026-10-19.cal", reloaded.Version);
        Assert.Equal(FloorConfiguration.Default.DegradedChoiceMargin, reloaded.DegradedChoiceMargin);
        Assert.Equal(QuestionCatalogue.Default.Ids.Order(StringComparer.Ordinal), reloaded.Floors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(0.9, reloaded.FloorOf("done"), 10);
        Assert.Throws<ArgumentException>(() => Fit(FloorConfiguration.Default.Version));
    }
}

public sealed class ReportAndReplayTests : IDisposable
{
    private readonly SyntheticLog _log = new();

    public void Dispose() => _log.Dispose();

    [Fact]
    public void The_report_separates_uncalibrated_decisions_and_writes_text_and_json()
    {
        _log.Add("s", WithOutcome(Done(0.95), "accepted"));
        _log.Add("s", WithOutcome(Done(0.85), "user_retried"));
        _log.Add("s", WithOutcome(Done(0.5), "accepted"));
        _log.Add("s", Done(0.5));
        _log.Add("s", WithOutcome(Done(1.0, decider: Degraded), "mystery"));
        _log.Add("s", new Decision(NewId(), "risk", ImmutableDictionary<string, double>.Empty, 0, 0.9, "ask", FloorsVersion, Degraded));

        var report = CalibrationReport.Build(_log.Load());

        var done = Assert.Single(report.Calibrated);
        Assert.Equal((4, 2, 0.5, 0.5, 0.75), (done.Decisions, done.Acted, done.ActedRate, done.EscalationRate, done.OutcomeCoverage));
        Assert.Equal(0.5, done.PrecisionWhenActed);
        Assert.Equal(3, done.Reliability.Sum(bin => bin.Count));
        Assert.Equal(1, done.Reliability[9].Count);
        Assert.Equal(["done", "risk"], report.Uncalibrated.Select(stats => stats.QuestionId));
        Assert.Empty(report.Uncalibrated[0].Reliability);
        Assert.Equal(1, report.UnlabelledOutcomes["mystery"]);
        Assert.Contains("never fitted", report.ToText(), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Assert.Equal(CalibrationReport.Format, root.GetProperty("format").GetString());
        Assert.Equal(6, root.GetProperty("decisions").GetInt32());
        Assert.Equal(4, root.GetProperty("tiers").GetProperty("calibrated").GetInt32());
        Assert.Equal(10, root.GetProperty("calibrated")[0].GetProperty("reliability").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fit").ValueKind);
    }

    [Fact]
    public void Replay_lists_the_decisions_a_candidate_floor_would_change()
    {
        var below = Done(0.7);
        var above = Done(0.9);
        var lowYes = Done(0.75, decider: Degraded);
        var held = Noul("skill_opportunity", 0.9, 0.75, "queue_skill_distillation", "nothing") with { Action = "nothing" };
        var embedding = new Decision(NewId(), "deliver", Map(("reply", 0.4), ("reply_in_thread", 0.2), ("home_channel", 0.2), ("silent", 0.2)),
            0.2, 0.05, "deliver:reply", FloorsVersion, Degraded);
        _log.Add("s", WithOutcome(below, "accepted"));
        _log.Add("s", above, lowYes, held, embedding);
        var floors = FloorConfiguration.Default.Floors.SetItem("done", 0.6).SetItem("skill_opportunity", 0.95);
        var candidate = new FloorConfiguration("candidate", 0.05, floors);

        var result = FloorReplay.Run(_log.Load(), candidate);

        Assert.Equal(2, result.Changes.Length);
        var change = result.Changes.Single(c => c.Decision.Event.DecisionId == below.DecisionId);
        Assert.Equal(("continue", "stop", true), (change.OldAction, change.NewAction, change.NowActs));
        Assert.Equal((0.8, 0.6), (change.OldThreshold, change.NewThreshold));
        // The degraded generator's yes/no is held to the floor too, so it replays.
        Assert.Contains(result.Changes, c => c.Decision.Event.DecisionId == lowYes.DecisionId);
        // A decision held back by a missing condition stays held back.
        Assert.DoesNotContain(result.Changes, c => c.Decision.Event.DecisionId == held.DecisionId);
        var done = result.Questions.Single(question => question.QuestionId == "done");
        Assert.Equal((3, 2, 2, 0, 1), (done.Replayed, done.Changed, done.NowAct, done.NowBelow, done.ChangedPositive));

        var margin = FloorReplay.Run(_log.Load(), candidate with { DegradedChoiceMargin = 0.1 });
        Assert.Equal(1, margin.Questions.Single(question => question.QuestionId == "deliver").NotReplayable);

        using var json = JsonDocument.Parse(result.ToJson());
        Assert.Equal(2, json.RootElement.GetProperty("changes").GetArrayLength());
        Assert.Contains("continue -> stop", result.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Raising_a_floor_stops_decisions_that_acted_and_conditions_are_flagged()
    {
        var acted = Done(0.85);
        var conditional = Noul("delegate", 0.6, 0.7, "delegate", "inline");
        _log.Add("s", WithOutcome(acted, "user_corrected"));
        _log.Add("s", conditional);
        var floors = FloorConfiguration.Default.Floors.SetItem("done", 0.9).SetItem("delegate", 0.5);

        var result = FloorReplay.Run(_log.Load(), new FloorConfiguration("candidate", 0.05, floors));

        var stop = result.Changes.Single(c => c.Decision.QuestionId == "done");
        Assert.Equal(("stop", "continue", false), (stop.OldAction, stop.NewAction, stop.NowActs));
        var delegated = result.Changes.Single(c => c.Decision.QuestionId == "delegate");
        Assert.True(delegated.ConditionsUnverified);
    }
}

public sealed class ExportTests : IDisposable
{
    private readonly SyntheticLog _log = new();

    public void Dispose() => _log.Dispose();

    [Fact]
    public void Kev_records_carry_state_question_options_acted_answer_and_outcome()
    {
        var tool = new Decision(NewId(), "tool", Map(("cron.update", 0.7), ("message.send", 0.1), ("none", 0.2)), 0.55, 0.45,
            "offer_tool:cron.update", FloorsVersion, Kev, Json("""{"user_message":"move it","step":0}"""),
            new AskedQuestion("choice", Json("\"Which tool should run first?\""),
                [new AskedOption("message.send", "send a message"), new AskedOption("cron.update", "change a reminder"), new AskedOption("none")]),
            Calibrated: true);
        var unanswered = new Decision(NewId(), "risk", ImmutableDictionary<string, double>.Empty, 0, 0.9, "ask", FloorsVersion, Degraded);
        var legacy = Done(0.6);
        _log.Add("s", WithOutcome(tool, "tool_succeeded"));
        _log.Add("s", unanswered, legacy);
        var output = new StringWriter();

        var summary = KevTrainingExport.Write(_log.Load(), output);

        Assert.Equal(new ExportSummary(2, 1, 1), summary);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var record = JsonDocument.Parse(lines[0]);
        var root = record.RootElement;
        Assert.Equal(KevTrainingExport.Format, root.GetProperty("format").GetString());
        Assert.Equal("move it", root.GetProperty("state").GetProperty("user_message").GetString());
        Assert.Equal("Which tool should run first?", root.GetProperty("instructions").GetString());
        // Options keep the order they were asked in, with descriptions, so the probabilities line up.
        Assert.Equal(["message.send", "cron.update", "none"],
            root.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("name").GetString()));
        Assert.Equal(0.7, root.GetProperty("options")[1].GetProperty("probability").GetDouble());
        Assert.Equal("change a reminder", root.GetProperty("options")[1].GetProperty("description").GetString());
        Assert.Equal("cron.update", root.GetProperty("acted_answer").GetString());
        Assert.Equal("positive", root.GetProperty("outcome_label").GetString());
        Assert.True(root.GetProperty("calibrated").GetBoolean());

        using var old = JsonDocument.Parse(lines[1]);
        Assert.Equal(JsonValueKind.Null, old.RootElement.GetProperty("state").ValueKind);
        Assert.False(old.RootElement.GetProperty("question_recorded").GetBoolean());
        Assert.Equal("Is the user's request fully handled?", old.RootElement.GetProperty("instructions").GetString());
        Assert.Equal(JsonValueKind.Null, old.RootElement.GetProperty("acted_answer").ValueKind);

        var labelled = new StringWriter();
        Assert.Equal(1, KevTrainingExport.Write(_log.Load(), labelled, new KevExportOptions(LabelledOnly: true)).Written);
    }

    [Fact]
    public void Trajectories_are_written_in_sharegpt_form_from_the_raw_history()
    {
        _log.Add("s",
            new UserMessage("move my dentist reminder"),
            new AssistantMessage("Looking."),
            new ToolCallIssued("call-1", "cron.update", Json("""{"id":14}""")),
            new ToolResult("call-1", Json("""{"ok":true}""")),
            Done(0.9),
            new AssistantMessage("Moved."));
        _log.Add("s", new Compacted(1, 4, new CompactionSummary("move reminder", [], [], [], [], null)));
        _log.Add("empty", Done(0.9));
        var output = new StringWriter();

        var summary = ShareGptExport.Write(_log.Load(), output);

        Assert.Equal((1, 1), (summary.Written, summary.Skipped));
        using var line = JsonDocument.Parse(output.ToString().Trim());
        var turns = line.RootElement.GetProperty("conversations").EnumerateArray()
            .Select(turn => (turn.GetProperty("from").GetString(), turn.GetProperty("value").GetString())).ToArray();
        Assert.Equal("s", line.RootElement.GetProperty("id").GetString());
        Assert.Equal(["human", "gpt", "tool", "gpt"], turns.Select(turn => turn.Item1));
        Assert.Equal("Looking.\n<tool_call>\n{\"name\":\"cron.update\",\"arguments\":{\"id\":14}}\n</tool_call>", turns[1].Item2);
        Assert.Equal("<tool_response>\n{\"tool_call_id\":\"call-1\",\"name\":\"cron.update\",\"content\":{\"ok\":true}}\n</tool_response>", turns[2].Item2);
    }
}

public sealed class CalibrationCliTests : IDisposable
{
    private readonly SyntheticLog _log = new();

    public void Dispose() => _log.Dispose();

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CalibrationCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Calibrate_writes_a_floors_file_and_a_json_report()
    {
        for (var i = 0; i < 20; i++)
            _log.Add("s", WithOutcome(Done(0.9), "accepted"));
        var floors = Path.Combine(_log.Root, "out", "floors.json");
        var report = Path.Combine(_log.Root, "out", "report.json");

        var (code, output, error) = Run("calibrate", "--log", _log.DatabasePath, "--version", "2026-11-01.1", "--out", floors,
            "--report-json", report);

        Assert.True(code == 0, error);
        Assert.Contains("Wrote floors 2026-11-01.1", output, StringComparison.Ordinal);
        Assert.Equal(0.9, FloorConfiguration.Parse(File.ReadAllText(floors)).FloorOf("done"), 10);
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("2026-11-01.1", json.RootElement.GetProperty("fit").GetProperty("version").GetString());

        var (replayCode, replay, _) = Run("replay", "--log", _log.DatabasePath, "--candidate", floors);
        Assert.Equal(0, replayCode);
        Assert.Contains("0 decision(s) would change", replay, StringComparison.Ordinal);
    }

    [Fact]
    public void Exports_and_report_run_from_the_command_line()
    {
        _log.Add("s", new UserMessage("hi"), Done(0.9), new AssistantMessage("hello"));
        var decisions = Path.Combine(_log.Root, "decisions.jsonl");
        var trajectories = Path.Combine(_log.Root, "trajectories.jsonl");

        Assert.Equal(0, Run("export-decisions", "--log", _log.DatabasePath, "--out", decisions).Code);
        Assert.Equal(0, Run("export-trajectories", "--log", _log.DatabasePath, "--out", trajectories).Code);
        Assert.Equal(0, Run("report", "--log", _log.DatabasePath).Code);
        Assert.Single(File.ReadAllLines(decisions));
        Assert.Single(File.ReadAllLines(trajectories));
    }

    [Theory]
    [InlineData]
    [InlineData("calibrate", "--log", "x.db")]
    [InlineData("report")]
    [InlineData("report", "--log")]
    [InlineData("report", "--log", "x.db", "--bogus", "1")]
    [InlineData("frobnicate", "--log", "x.db")]
    public void Usage_errors_exit_two(params string[] args) => Assert.Equal(2, Run(args).Code);

    [Fact]
    public void A_missing_log_exits_one()
    {
        var (code, _, error) = Run("report", "--log", Path.Combine(_log.Root, "nope.db"));

        Assert.Equal(1, code);
        Assert.Contains("No event log", error, StringComparison.Ordinal);
    }
}
