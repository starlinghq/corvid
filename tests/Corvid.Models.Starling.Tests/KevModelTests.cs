using System.Diagnostics;
using System.Globalization;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Starlings.Models.Kev;

namespace Corvid.Models.Starling.Tests;

/// <summary>
/// Kev-0.8B and Kev-4B on the CPU, behind <see cref="ModelTests.CacheVariable"/> like the other model-backed tests; each
/// skips when its bundle is not in that cache. Kev-4B needs about 18 GB resident, so it also skips when the machine has
/// less than 22 GB available. The tests never download.
/// </summary>
public sealed class KevModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly QuestionOption[] Shortlist =
    [
        new("list_files", "List a directory in the workspace: subdirectories end with '/', files show their size."),
        new("read_file", "Read a text file in the workspace, with line numbers. Long files come in parts: pass start_line to continue."),
        new("web_fetch", "Fetch a web page or document by URL and return its text."),
        new("cron_create", "Schedule a reminder or a recurring job."),
        new("none", "no tool is needed"),
    ];

    private static DecisionRequest Ingress(string message)
    {
        var state = new TurnStateBuilder().Build(new TurnStateInput(message, "cli", "qwen3.5-0.8b local, 8k"));
        var catalogue = QuestionCatalogue.Default;
        return new DecisionRequest(state.Json,
        [
            KeyValuePair.Create("intent", catalogue["intent"].ToQuestion()),
            KeyValuePair.Create("clarify", catalogue["clarify"].ToQuestion()),
            KeyValuePair.Create("difficulty", catalogue["difficulty"].ToQuestion()),
            KeyValuePair.Create("tool", catalogue["tool"].ToQuestion(Shortlist)),
        ]);
    }

    [Fact]
    public async Task Kev_0_8b_answers_an_ingress_checkpoint_calibrated_and_ranks_the_listing_tool_first()
    {
        using var kev = Load(KevModel.Kev08B);
        var started = Stopwatch.GetTimestamp();

        var result = await kev.DecideAsync(Ingress("list the files in my workspace"), Ct);

        var elapsed = Stopwatch.GetElapsedTime(started);
        var answers = Assert.IsType<DeciderAnswers>(result);
        Assert.Equal("kev-0.8b", answers.Decider);
        Assert.Empty(answers.Declined);
        Assert.Equal(4, answers.Answers.Count);
        Assert.All(answers.Answers.Values, answer =>
        {
            Assert.True(answer.Calibrated);
            Assert.Equal(1.0, answer.Probabilities.Sum(), 6);
        });
        Report("kev-0.8b", answers, elapsed);
        // Kev-0.8B is weak off its training distribution (transfer-v4 0.648): on this five-tool shortlist it leans to none
        // at low confidence, which is below the tool floor, so the generator gets the shortlist. Among the tools it still
        // ranks the listing tool first; Kev-4B picks it outright (below).
        var tool = answers.Answers["tool"];
        Assert.Equal("list_files", Shortlist.Where(option => option.Name != "none")
            .MaxBy(option => tool.ProbabilityOf(option.Name))!.Name);
        // A plain request: clarify stays below its floor (0.7), so the turn proceeds.
        Assert.True(answers.Answers["clarify"].ProbabilityOf(DecisionQuestion.Yes) < 0.7);
    }

    [Fact]
    public async Task Kev_0_8b_declines_a_row_over_the_budget_and_refuses_a_state_over_its_limit()
    {
        using var kev = Load(KevModel.Kev08B);
        var state = new TurnStateBuilder().Build(new TurnStateInput("tidy my notes", "cli", "qwen3.5-0.8b local, 8k")).Json;
        var longInstructions = "Old tool result: " + string.Join(' ', Enumerable.Repeat("the quarterly report lists revenue by region", 300));

        var partial = Assert.IsType<DeciderAnswers>(await kev.DecideAsync(new DecisionRequest(state,
        [
            KeyValuePair.Create("clear_results/call-1", DecisionQuestion.Choice(DecisionContent.FromText(longInstructions),
                QuestionCatalogue.Default["clear_results"].Options)),
            KeyValuePair.Create("done", QuestionCatalogue.Default["done"].ToQuestion()),
        ]), Ct));
        Assert.Equal(KevDecider.RowOverBudget, partial.Declined["clear_results/call-1"]);
        Assert.True(partial.Answers.ContainsKey("done"));

        using var huge = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            user_message = string.Join(' ', Enumerable.Repeat("please summarise this long pasted document carefully", 1_500)),
        }));
        var refused = Assert.IsType<DeciderUnavailable>(await kev.DecideAsync(new DecisionRequest(huge.RootElement,
            [KeyValuePair.Create("done", QuestionCatalogue.Default["done"].ToQuestion())]), Ct));
        Assert.Equal(DeciderUnavailableReason.StateOverBudget, refused.Reason);
    }

    [Fact]
    public async Task Kev_4b_answers_the_same_checkpoint()
    {
        Assert.SkipWhen(AvailableGigabytes() < 22, $"Kev-4B needs about 18 GB resident; {AvailableGigabytes():0} GB available.");
        using var kev = Load(KevModel.Kev4B);
        var started = Stopwatch.GetTimestamp();

        var answers = Assert.IsType<DeciderAnswers>(await kev.DecideAsync(Ingress("list the files in my workspace"), Ct));

        Report("kev-4b", answers, Stopwatch.GetElapsedTime(started));
        Assert.Equal("kev-4b", answers.Decider);
        Assert.All(answers.Answers.Values, answer => Assert.True(answer.Calibrated));
        Assert.Equal("list_files", answers.Answers["tool"].Choice);
    }

    private static void Report(string name, DeciderAnswers answers, TimeSpan elapsed)
    {
        var lines = answers.Answers.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            $"{pair.Key}: {pair.Value.Choice} (confidence {pair.Value.Confidence.ToString("0.000", CultureInfo.InvariantCulture)}" +
            (pair.Value.ExpectedScore is { } score ? $", expected {score.ToString("0.00", CultureInfo.InvariantCulture)}" : "") + ")");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{name}, {answers.Answers.Count} questions in {elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s: {string.Join("; ", lines)}");
    }

    private static KevDecider Load(KevModel model)
    {
        var cache = Environment.GetEnvironmentVariable(ModelTests.CacheVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(cache), $"Set {ModelTests.CacheVariable} to a Starling model cache to run model-backed tests.");
        var store = new StarlingModelStore(cache);
        var status = store.Check(model == KevModel.Kev4B ? StarlingModel.Kev4B : StarlingModel.Kev08B, Starlings.Acquisition.CacheVerification.Size);
        Assert.SkipWhen(status.State == StarlingModelState.Missing, status.Describe());
        Assert.True(status.IsReady, status.Describe());
        return (KevDecider)StarlingBindings.KevDecider(store.OpenKev(model)).Service;
    }

    private static double AvailableGigabytes()
    {
        foreach (var line in File.Exists("/proc/meminfo") ? File.ReadLines("/proc/meminfo") : [])
            if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024.0 / 1024.0;
        return 0;
    }
}
