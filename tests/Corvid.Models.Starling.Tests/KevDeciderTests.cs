using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Scheduling;
using Starlings.Models.Kev;

namespace Corvid.Models.Starling.Tests;

/// <summary>
/// Kev's encoder and graph replaced by a fake: each question's row takes the tokens <see cref="RowTokens"/> gives, and its
/// probabilities (in Kev's option order) come from <see cref="Probabilities"/>. The row's first id is the question's index
/// in the record, so a run of a reduced encoding still knows which question each row is.
/// </summary>
internal sealed class FakeKevRunner : IKevRunner
{
    private KevRecord? _record;

    public Func<KevRecordQuestion, int> RowTokens { get; set; } = _ => 40;

    public Func<KevRecordQuestion, float[]> Probabilities { get; set; } =
        question => [.. question.Options.Select(_ => 1f / question.Options.Count)];

    /// <summary>Throws Kev's own refusal for the state or a row, as the real encoder does.</summary>
    public Func<KevRecord, KevInputTooLongException?> Refuse { get; set; } = _ => null;

    public List<KevRecord> Encoded { get; } = [];

    public List<KevEncoding> Ran { get; } = [];

    public Action? OnRun { get; set; }

    public KevEncoding Encode(KevRecord record)
    {
        Encoded.Add(record);
        if (Refuse(record) is { } refusal)
            throw refusal;
        _record = record;
        var state = ImmutableArray.Create(KevModelProfile.StateTokenId, 1, 2, 3);
        var rows = record.Questions.Select((question, index) =>
        {
            var length = Math.Max(RowTokens(question), question.Options.Count + 2);
            var ids = Enumerable.Range(0, length).Select(i => i == 0 ? index : 7).ToImmutableArray();
            var positions = Enumerable.Range(state.Length, length).ToImmutableArray();
            return new KevRow(ids, positions, length - 1, [.. Enumerable.Range(1, question.Options.Count)]);
        }).ToList();
        return new KevEncoding(state, rows);
    }

    public IReadOnlyList<float[]> Run(KevEncoding encoding, CancellationToken cancellationToken)
    {
        OnRun?.Invoke();
        Ran.Add(encoding);
        return [.. encoding.Rows.Select(row => Probabilities(_record!.Questions[row.Ids[0]]))];
    }
}

public sealed class KevDeciderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonElement State = JsonDocument.Parse(
        """{"user_message":"list the files in my workspace","surface":"cli","step":0}""").RootElement.Clone();

    private static KevDecider Kev(FakeKevRunner runner, int rowBudget = KevDecider.DefaultRowBudget) =>
        new("kev-0.8b", runner, rowBudget: rowBudget);

    private static DecisionRequest Request(params (string Key, DecisionQuestion Question)[] questions) =>
        new(State, questions.Select(item => KeyValuePair.Create(item.Key, item.Question)));

    private static DecisionQuestion Noul(string text = "Does completing this require running a tool?") => DecisionQuestion.Noul(text);

    private static DecisionQuestion Tools() => DecisionQuestion.Choice(DecisionContent.FromText("Which tool should run first?"),
        [new QuestionOption("list_files", "List the files in a workspace directory."), new QuestionOption("read_file", "Read a file."),
         new QuestionOption("none", "no tool is needed")]);

    private static DecisionQuestion Risk() => DecisionQuestion.Score("How risky is this action?", "none", "low", "moderate", "high", "destructive");

    [Fact]
    public async Task One_request_per_checkpoint_with_the_turn_state_as_json_and_questions_in_key_order()
    {
        var runner = new FakeKevRunner();
        var instructions = JsonDocument.Parse("""{"question":"Should this old result stay?","result":"3 files"}""").RootElement;

        var result = await Kev(runner).DecideAsync(Request(
            ("tool", Tools()), ("clarify", Noul("Is the request too ambiguous?")),
            ("clear_results/call-1", DecisionQuestion.Choice(DecisionContent.FromJson(instructions),
                [new QuestionOption("keep", "still needed"), new QuestionOption("stub"), new QuestionOption("drop")])),
            ("risk", Risk())), Ct);

        Assert.IsType<DeciderAnswers>(result);
        var record = Assert.Single(runner.Encoded);
        Assert.Single(runner.Ran);
        // Kev renders a JSON state as key: value lines, as the degraded tier does.
        Assert.Contains("user_message: list the files in my workspace", record.State, StringComparison.Ordinal);
        Assert.Equal(["clarify", "clear_results/call-1", "risk", "tool"], record.Questions.Select(q => q.Id));
        var tool = record.Questions.Single(q => q.Id == "tool");
        Assert.Equal("Which tool should run first?", tool.Instruction);
        // Option descriptions are read as name: description at each option's closing token (§5.1).
        Assert.Equal(["list_files: List the files in a workspace directory.", "read_file: Read a file.", "none: no tool is needed"], tool.Options);
        var clear = record.Questions.Single(q => q.Id == "clear_results/call-1");
        Assert.Contains("result: 3 files", clear.Instruction, StringComparison.Ordinal);
        Assert.Equal(["keep: still needed", "stub", "drop"], clear.Options);
        Assert.Equal(["none", "low", "moderate", "high", "destructive"], record.Questions.Single(q => q.Id == "risk").Options);
        // Kev's yes/no options are no, then yes.
        Assert.Equal(2, record.Questions.Single(q => q.Id == "clarify").Options.Count);
    }

    [Fact]
    public async Task Answers_keep_the_question_order_and_are_calibrated()
    {
        var runner = new FakeKevRunner
        {
            Probabilities = question => question.Id switch
            {
                "needs_tools" => [0.2f, 0.8f], // Kev order: no, yes
                "tool" => [0.7f, 0.2f, 0.1f],
                "risk" => [0.1f, 0.2f, 0.4f, 0.2f, 0.1f],
                _ => throw new InvalidOperationException(),
            },
        };

        var result = Assert.IsType<DeciderAnswers>(await Kev(runner).DecideAsync(
            Request(("needs_tools", Noul()), ("tool", Tools()), ("risk", Risk())), Ct));

        Assert.Equal("kev-0.8b", result.Decider);
        Assert.Empty(result.Declined);
        var yes = result.Answers["needs_tools"];
        Assert.True(yes.Calibrated);
        Assert.Equal(["yes", "no"], yes.Options);
        Assert.Equal(0.8, yes.ProbabilityOf("yes"), 6);
        Assert.True(yes.Yes);
        var tool = result.Answers["tool"];
        Assert.Equal("list_files", tool.Choice);
        Assert.Equal((0.7 - 1.0 / 3) / (1 - 1.0 / 3), tool.Confidence, 5);
        var risk = result.Answers["risk"];
        Assert.Equal(2.0, risk.ExpectedScore!.Value, 5);
        Assert.Equal("moderate", risk.Choice);
        Assert.All(result.Answers.Values, answer => Assert.Equal(1.0, answer.Probabilities.Sum(), 9));
    }

    [Fact]
    public async Task A_row_over_the_budget_is_declined_and_the_rest_run_without_encoding_again()
    {
        var runner = new FakeKevRunner
        {
            RowTokens = question => question.Id == "memory_op" ? 1_200 : 60,
            Probabilities = question => question.Id == "tool" ? [0.1f, 0.8f, 0.1f] : [0.3f, 0.7f],
        };

        var result = Assert.IsType<DeciderAnswers>(await Kev(runner).DecideAsync(Request(
            ("memory_op", DecisionQuestion.Choice("How should this fact change memory?", "add", "update", "invalidate", "noop")),
            ("needs_tools", Noul()), ("tool", Tools())), Ct));

        Assert.Equal(KevDecider.RowOverBudget, result.Declined["memory_op"]);
        Assert.False(result.Answers.ContainsKey("memory_op"));
        Assert.Equal("read_file", result.Answers["tool"].Choice);
        Assert.Equal(0.7, result.Answers["needs_tools"].ProbabilityOf("yes"), 6);
        Assert.Single(runner.Encoded);
        Assert.Equal(2, Assert.Single(runner.Ran).Rows.Count);
    }

    [Fact]
    public async Task Every_row_over_the_budget_refuses_the_request()
    {
        var runner = new FakeKevRunner { RowTokens = _ => 2_000 };

        var result = Assert.IsType<DeciderUnavailable>(await Kev(runner).DecideAsync(Request(("needs_tools", Noul()), ("tool", Tools())), Ct));

        Assert.Equal(DeciderUnavailableReason.Refused, result.Reason);
        Assert.Contains("needs_tools (row_over_budget)", result.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_state_kev_refuses_makes_the_decider_unavailable_so_the_fabric_falls_back()
    {
        var runner = new FakeKevRunner
        {
            Refuse = _ => new KevInputTooLongException("too long") { TokenCount = 9_000, MaxTokens = 8_192, QuestionIndex = null },
        };

        var result = Assert.IsType<DeciderUnavailable>(await Kev(runner).DecideAsync(Request(("needs_tools", Noul())), Ct));

        Assert.Equal(DeciderUnavailableReason.StateOverBudget, result.Reason);
        Assert.Contains("9000 tokens", result.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_row_over_kevs_own_limit_is_declined_and_the_rest_encoded_again()
    {
        var runner = new FakeKevRunner
        {
            Refuse = record => record.Questions.FirstOrDefault(q => q.Id == "memory_op") is { } row
                ? new KevInputTooLongException("row") { TokenCount = 9_000, MaxTokens = 8_192, QuestionIndex = record.Questions.ToList().IndexOf(row) }
                : null,
        };

        var result = Assert.IsType<DeciderAnswers>(await Kev(runner).DecideAsync(Request(
            ("memory_op", DecisionQuestion.Choice("How should this fact change memory?", "add", "update", "invalidate", "noop")),
            ("needs_tools", Noul())), Ct));

        Assert.Equal(KevDecider.RowOverBudget, result.Declined["memory_op"]);
        Assert.True(result.Answers.ContainsKey("needs_tools"));
        Assert.Equal(2, runner.Encoded.Count);
        Assert.Equal(["needs_tools"], runner.Encoded[1].Questions.Select(q => q.Id));
    }

    [Fact]
    public async Task A_refusal_naming_no_pending_question_is_a_failure_not_an_exception()
    {
        var runner = new FakeKevRunner
        {
            Refuse = _ => new KevInputTooLongException("row") { TokenCount = 9_000, MaxTokens = 8_192, QuestionIndex = 5 },
        };

        var result = Assert.IsType<DeciderUnavailable>(await Kev(runner).DecideAsync(Request(("needs_tools", Noul()), ("tool", Tools())), Ct));

        Assert.Equal(DeciderUnavailableReason.Failed, result.Reason);
        Assert.Contains("question 5 of 2", result.Detail, StringComparison.Ordinal);
        Assert.Single(runner.Encoded);
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_question_kev_cannot_take_is_declined()
    {
        var runner = new FakeKevRunner();
        var many = DecisionQuestion.Choice("Which?", Enumerable.Range(0, 300).Select(i => $"option{i}"));

        var result = Assert.IsType<DeciderAnswers>(await Kev(runner).DecideAsync(Request(("tool", many), ("needs_tools", Noul())), Ct));

        Assert.Equal(KevDecider.Unsupported, result.Declined["tool"]);
        Assert.Equal(["needs_tools"], Assert.Single(runner.Encoded).Questions.Select(q => q.Id));
    }

    [Fact]
    public async Task A_malformed_run_is_a_failure_not_an_answer()
    {
        var runner = new FakeKevRunner { Probabilities = _ => [1f, 0f, 0f] };

        var result = Assert.IsType<DeciderUnavailable>(await Kev(runner).DecideAsync(Request(("needs_tools", Noul())), Ct));

        Assert.Equal(DeciderUnavailableReason.Failed, result.Reason);
    }

    [Fact]
    public void Answers_map_kevs_option_order_and_normalise()
    {
        var yes = KevDecider.ToAnswer(Noul(), [0.25f, 0.75f])!;
        Assert.Equal([0.75, 0.25], yes.Probabilities);
        var score = KevDecider.ToAnswer(Risk(), [0.2f, 0.2f, 0.2f, 0.2f, 0.2001f])!;
        Assert.Equal(1.0, score.Probabilities.Sum(), 12);
        Assert.Null(KevDecider.ToAnswer(Noul(), [float.NaN, 1f]));
        Assert.Null(KevDecider.ToAnswer(Noul(), [-0.1f, 1.1f]));
        Assert.Null(KevDecider.ToAnswer(Noul(), [0f, 0f]));
    }

    [Fact]
    public async Task Through_the_fabric_the_decisions_are_calibrated_and_a_declined_row_says_why()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-kev-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SqliteEventLog(Path.Combine(root, "events.db"));
            var runner = new FakeKevRunner
            {
                RowTokens = question => question.Id == "memory_op" ? 1_500 : 50,
                Probabilities = question => question.Id == "tool" ? [0.9f, 0.05f, 0.05f] : [0.9f, 0.1f],
            };
            var fabric = new DecisionFabric(DecisionPolicy.Default, new UnusedDecider(), log, Kev(runner));
            var state = new TurnStateBuilder().Build(new TurnStateInput("list the files in my workspace", "cli", "qwen3.5-0.8b local, 8k"));

            var result = await fabric.DecideAsync("s-1", Checkpoint.Ingress, state,
                [KeyValuePair.Create("clarify", DecisionQuestion.Noul("Is the request too ambiguous?")), KeyValuePair.Create("tool", Tools())],
                cancellationToken: Ct);

            Assert.Equal("kev-0.8b", result.Decider);
            Assert.Null(result.Fallback);
            Assert.Equal("offer_tool:list_files", result["tool"].Verdict.Action);
            // Kev's no is 0.9: clarify proceeds on a calibrated answer.
            Assert.Equal("proceed", result["clarify"].Verdict.Action);
            var decisions = log.Read("s-1", cancellationToken: Ct).Select(e => Assert.IsType<Decision>(e.Payload)).ToList();
            Assert.All(decisions, d => Assert.True(d.Calibrated));

            var memory = await fabric.DecideAsync("s-1", Checkpoint.PreExecute, state,
                [KeyValuePair.Create("memory_op", DecisionQuestion.Choice(DecisionContent.FromText("How should this fact change memory?"),
                    QuestionCatalogue.Default["memory_op"].Options))],
                cancellationToken: Ct);
            // Every row declined: the decider is unavailable, and the degraded tier's gate rule stages the write.
            Assert.Equal("stage_for_review", memory["memory_op"].Verdict.Action);
            Assert.Equal(DeciderUnavailableReason.Refused, memory.Fallback!.Reason);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task On_the_device_scheduler_kev_runs_on_a_device_thread_not_the_callers()
    {
        using var device = new DeviceScheduler("cpu");
        bool? onDevice = null;
        var runner = new FakeKevRunner { OnRun = () => onDevice = device.IsCurrentThread };
        var decider = Kev(runner).Schedule(device, DevicePriority.Fabric);

        await decider.DecideAsync(Request(("needs_tools", Noul())), Ct);

        Assert.True(onDevice);
        Assert.False(device.IsCurrentThread);
    }

    [Fact]
    public void Create_refuses_any_temperature_but_the_calibrated_one_and_cuda_only_without_the_cuda_natives()
    {
        var files = new KevModelFiles(KevModelProfile.Kev08B, "/nonexistent/model.onnx", "/nonexistent/head.safetensors",
            "/nonexistent/head.json", "/nonexistent/tokenizer.json", null!);
        // C-029: CUDA is accepted. A CPU build refuses it before the model is read; a CUDA build gets as far as the files.
        var cuda = new KevOptions
        {
            Session = new Starlings.Runtime.Onnx.OnnxSessionOptions { Provider = Starlings.Runtime.Onnx.OnnxExecutionProvider.Cuda, CudaDeviceId = 1 },
        };
        if (KevDecider.HasCudaProvider(out _) == false)
            Assert.Equal("cuda:1", Assert.Throws<ModelPlacementUnavailableException>(() => KevDecider.Create(files, cuda)).Placement);
        Assert.Throws<ArgumentException>(() => KevDecider.Create(files, new KevOptions { Temperature = 1.0 }));
        Assert.Throws<ArgumentException>(() => KevDecider.Create(files, new KevOptions { Temperature = 1.0, Session = cuda.Session }));
        Assert.Equal("cpu", KevDecider.PlacementOf(null));
        Assert.Equal("cuda:1", KevDecider.PlacementOf(cuda.Session));
        Assert.Null(KevDecider.OptionsFor("cpu"));
        Assert.Equal(2, KevDecider.OptionsFor("cuda:2")!.Session!.CudaDeviceId);
        Assert.Throws<ArgumentException>(() => KevDecider.OptionsFor("gpu"));
        Assert.Equal("kev-0.8b", KevDecider.NameOf(KevModel.Kev08B));
        Assert.Equal("kev-4b", KevDecider.NameOf(KevModel.Kev4B));
    }

    /// <summary>The degraded tier, for fallbacks: no answers, so every question takes its below-floor action.</summary>
    private sealed class UnusedDecider : IDecider
    {
        public string Name => "degraded";

        public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, ImmutableDictionary<string, DecisionAnswer>.Empty));
    }
}
