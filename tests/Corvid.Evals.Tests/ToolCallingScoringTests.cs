using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Evals.ToolCalling;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Evals.Tests;

/// <summary>
/// The S2 harness (C-034) end to end on fakes: a scripted generator, a keyword embedder and a scripted calibrated decider
/// drive <see cref="ToolCallingRunner"/> through the real TurnRunner, and every outcome class is scored from the log.
/// </summary>
public sealed class ToolCallingScoringTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-s2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal const string Catalogue = """
        {"version":"test.1","tools":[
          {"name":"read_file","origin":"builtin","description":"Read a text file in the workspace.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"examples":["open the file"]},
          {"name":"notes__create_note","origin":"mcp:notes","description":"Save a note with a title and body.","parameters":{"type":"object","properties":{"title":{"type":"string"},"body":{"type":"string"}},"required":["title","body"]}},
          {"name":"weather__get_weather","origin":"mcp:weather","description":"Current weather and forecast for a city.","parameters":{"type":"object","properties":{"city":{"type":"string"},"units":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["city"]}},
          {"name":"web__web_search","origin":"mcp:web","description":"Search the web online.","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}
        ]}
        """;

    internal const string Cases = """
        {"id":"correct","prompt":"What's the weather in Porto?","tags":["direct"],"expect":{"tool":"weather__get_weather"},"args":{"city":{"equals":"porto"}},"results":{"weather__get_weather":"{\"city\":\"Porto\",\"now\":\"sunny\"}"}}
        {"id":"wrong","prompt":"Search online for hiking trails.","tags":["direct"],"expect":{"tool":"web__web_search"}}
        {"id":"invalid","prompt":"Open the file README.md.","tags":["direct"],"expect":{"tool":"read_file"},"args":{"path":{"equals":"README.md"}}}
        {"id":"repaired","prompt":"Save a note titled Groceries: eggs.","tags":["near_miss"],"expect":{"tool":"notes__create_note"},"args":{"title":{"equals":"Groceries"}}}
        {"id":"prose","prompt":"Is it raining in Oslo weather?","tags":["required","tempting"],"expect":{"tool":"weather__get_weather"},"args":{"city":{"equals":"Oslo"}}}
        {"id":"clarify","prompt":"Do the thing with the file.","tags":["direct"],"expect":{"tool":"read_file"}}
        {"id":"escalate","prompt":"Open the file config.json.","tags":["direct"],"expect":{"tool":"read_file"}}
        {"id":"notool","prompt":"Hello there!","tags":["no_tool"],"expect":{"none":true}}
        {"id":"chain","prompt":"Read todo.txt and save it as a note titled Todo.","tags":["multi_step"],"steps":[{"expect":{"tool":"read_file"},"args":{"path":{"equals":"todo.txt"}}},{"expect":{"tool":"notes__create_note"},"args":{"title":{"equals":"Todo"}}}],"results":{"read_file":"buy milk"}}
        """;

    /// <summary>What each prompt's generator does, from what the conversation holds so far.</summary>
    private static ChatResponse Script(GeneratorTurn turn) => turn.Prompt switch
    {
        "What's the weather in Porto?" => turn.Results == 0 ? Call("weather__get_weather", """{"city":"Porto"}""") : Text("Sunny in Porto."),
        "Search online for hiking trails." => turn.Results == 0 ? Call("read_file", """{"path":"trails.txt"}""") : Text("Here are some."),
        // A name that was never offered, then prose: nothing ran.
        "Open the file README.md." => turn.Results == 0 ? Call("cat_file", """{"path":"README.md"}""") : Text("I could not open it."),
        // The body is missing (schema), then the repair adds it.
        "Save a note titled Groceries: eggs." => turn.Results switch
        {
            0 => Call("notes__create_note", """{"title":"Groceries"}"""),
            1 => Call("notes__create_note", """{"title":"Groceries","body":"eggs"}"""),
            _ => Text("Saved."),
        },
        // Narrates a result, unless the loop requires the tool.
        "Is it raining in Oslo weather?" => turn.Nudge == "required:weather__get_weather" ? Call("weather__get_weather", """{"city":"Oslo"}""")
            : turn.Results == 0 ? Text("It is raining in Oslo.") : Text("Yes, rain."),
        "Do the thing with the file." => Text("Which file do you mean?"),
        // Never gives the required path: two failed rounds escalate (repair).
        "Open the file config.json." => Call("read_file", """{"file":"config.json"}"""),
        "Read todo.txt and save it as a note titled Todo." => turn.Results switch
        {
            0 => Call("read_file", """{"path":"todo.txt"}"""),
            1 => Call("notes__create_note", """{"title":"Todo","body":"buy milk"}"""),
            _ => Text("Done."),
        },
        _ => Text("Hello! How can I help?"),
    };

    /// <summary>Kev in (c): clarifies one prompt, and picks the weather tool for the narrating one; every other answer is withheld.</summary>
    private static double[]? Kev(string prompt, string key, DecisionQuestion question)
    {
        if (key == "clarify" && prompt.StartsWith("Do the thing", StringComparison.Ordinal))
            return [0.95, 0.05];
        if (key == "tool" && prompt.StartsWith("Is it raining", StringComparison.Ordinal))
            return [.. question.Options.Select(option => option.Name == "weather__get_weather" ? 0.9 : 0.1 / (question.Options.Length - 1))];
        return null;
    }

    private (ToolCallingSet Set, ToolCallingRunner Runner, ImmutableArray<CaseRun> Runs, List<CaseScore> Scores) Run()
    {
        var set = ToolCallingSet.Parse(Catalogue, Cases);
        var generator = new ScriptedGenerator(Script);
        var bindings = new ToolCallingBindings(
            new ModelBinding(Profile("fake-qwen", ToolCallSupport.TemplateRendered, 8_192), generator),
            new ModelBinding(Profile("keywords", ToolCallSupport.None, 512), new KeywordEmbedder("weather", "note", "file", "web", "online")),
            null, new ScriptedDecider(Kev));
        var runner = new ToolCallingRunner(set, bindings, [ToolCallingConfiguration.QwenAll, ToolCallingConfiguration.ShortlistKev], _root);
        var runs = runner.RunAsync(set.Cases, ToolCallingCli.WarmUp, cancellationToken: Ct).GetAwaiter().GetResult();
        return (set, runner, runs, [.. runs.Select(run => ToolCallingScorer.Score(run, set))]);
    }

    private static CaseScore Get(List<CaseScore> scores, string id, string configuration) =>
        scores.Single(score => score.Id == id && score.Configuration == configuration);

    [Fact]
    public void Every_outcome_class_is_scored_from_the_real_turn_loop()
    {
        var (_, _, _, scores) = Run();
        const string a = "qwen-all", c = "shortlist-kev";

        var correct = Get(scores, "correct", a);
        Assert.Equal(CaseOutcome.Correct, correct.Outcome);
        Assert.True(correct.ArgsMatch);
        Assert.True(correct.FirstResponseCorrect);
        Assert.Equal(CaseOutcome.WrongTool, Get(scores, "wrong", a).Outcome);
        var invalid = Get(scores, "invalid", a);
        Assert.Equal(CaseOutcome.InvalidName, invalid.Outcome);
        Assert.True(Assert.Single(invalid.Calls).InvalidName);

        var repaired = Get(scores, "repaired", a);
        Assert.Equal(CaseOutcome.Correct, repaired.Outcome);
        Assert.True(repaired.RepairedToCorrect);
        Assert.False(repaired.FirstResponseCorrect);
        Assert.False(repaired.ArgsValid);
        Assert.Equal([true, false], repaired.Calls.Select(call => call.Rejected));

        // Prose instead of a call: Qwen alone answers in text; with Kev's confident pick the loop requires the tool.
        Assert.Equal(CaseOutcome.NoCall, Get(scores, "prose", a).Outcome);
        var nudged = Get(scores, "prose", c);
        Assert.Equal(CaseOutcome.Correct, nudged.Outcome);
        Assert.True(nudged.Nudged);
        Assert.Equal("weather__get_weather", nudged.KevToolPick);
        Assert.True(nudged.KevToolActed);

        Assert.Equal(CaseOutcome.Clarified, Get(scores, "clarify", c).Outcome);
        Assert.Equal(CaseOutcome.NoCall, Get(scores, "clarify", a).Outcome);

        var escalated = Get(scores, "escalate", a);
        Assert.Equal(CaseOutcome.Escalated, escalated.Outcome);
        Assert.Equal("repair", escalated.EscalationReason);
        Assert.Equal(CaseOutcome.Malformed, escalated.LocalOutcome);
        Assert.Equal(CaseOutcome.Escalated, Get(scores, "escalate", c).Outcome);

        Assert.Equal(CaseOutcome.Correct, Get(scores, "notool", a).Outcome);
        var chain = Get(scores, "chain", a);
        Assert.Equal(CaseOutcome.Correct, chain.Outcome);
        Assert.Equal(2, chain.StepsMatched);
        Assert.True(chain.ArgsMatch);
    }

    [Fact]
    public void Under_the_abstaining_tier_nothing_acts_and_every_tool_is_offered()
    {
        var (set, _, runs, scores) = Run();
        var all = set.Catalogue.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToList();

        foreach (var run in runs.Where(run => run.Configuration == ToolCallingConfiguration.QwenAll))
        {
            var decisions = run.Events.OfType<Decision>().ToList();
            Assert.NotEmpty(decisions);
            Assert.All(decisions, decision => Assert.Equal(AbstainingDecider.DeciderName, decision.Decider));
            Assert.All(decisions, decision => Assert.False(decision.Calibrated));
            // Uniform answers: P(yes) = 0.5 is below every floor TurnRunner asks; choices have no margin; difficulty is a heuristic.
            Assert.Contains(decisions, decision => decision.QuestionId == "clarify" && decision.Action == "proceed");
            Assert.Contains(decisions, decision => decision.QuestionId == "difficulty" && decision.Action == "stay_on_generator");
            Assert.Contains(decisions, decision => decision.QuestionId == "tool" && decision.Action == "offer_shortlist");
            Assert.All(decisions.Where(decision => decision.QuestionId == "done"), decision => Assert.Equal("continue", decision.Action));
            Assert.All(run.GeneratorCalls.Where(call => call.Nudge is null), call => Assert.Equal(all, call.Offered.Order(StringComparer.Ordinal)));
        }
        Assert.All(scores.Where(score => score.Configuration == "qwen-all"), score => Assert.Empty(score.Acted));
        Assert.All(scores.Where(score => score.Configuration == "qwen-all"), score => Assert.Null(score.KevToolPick));
    }

    [Fact]
    public void The_summary_counts_the_headline_and_the_markdown_shows_the_tables()
    {
        var (_, _, _, scores) = Run();
        var header = new ToolCallingHeader("test.1", new string('a', 64), new string('b', 64), 4, "floors", "abc1234",
            ImmutableSortedDictionary<string, string>.Empty.Add("qwen", "fake"), ImmutableSortedDictionary<string, string>.Empty,
            ImmutableSortedDictionary<string, string>.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var report = ToolCallingReport.Build(header, scores);

        var a = report.Summaries.Single(summary => summary.Configuration == "qwen-all");
        var c = report.Summaries.Single(summary => summary.Configuration == "shortlist-kev");
        Assert.Equal(9, a.Cases);
        // (a): correct, repaired, notool, chain. (c) adds the nudged prose case and loses nothing else.
        Assert.Equal(4, a.ToolCorrect);
        Assert.Equal(5, c.ToolCorrect);
        Assert.Equal(1, a.Escalated);
        Assert.Equal(1, a.EscalationReasons["repair"]);
        Assert.Equal(1, c.Clarified);
        Assert.Equal(8, a.NonEscalated);
        Assert.Equal(8, a.ToolCases);
        Assert.Equal(2, a.NoCall);
        Assert.Equal(1, c.Nudged);
        Assert.Equal(9, a.Outcomes.Values.Sum());
        var headline = report.Headline!;
        Assert.Equal(100.0 / 9, headline.ToolPoints, 6);
        Assert.Equal(100.0 / 9, headline.EscalationRate, 6);
        Assert.False(headline.Pass);

        var markdown = report.ToMarkdown();
        Assert.Contains("| qwen-all | 9 | 4 (44.4%) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Tool choice | +11.1 points |", markdown, StringComparison.Ordinal);
        Assert.Contains("## Disagreements", markdown, StringComparison.Ordinal);
        Assert.Contains("| invalid | read_file | invalid_name: ?cat_file |", markdown, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal(18, json.RootElement.GetProperty("cases").GetArrayLength());
        Assert.Equal("test.1", json.RootElement.GetProperty("header").GetProperty("set_version").GetString());
    }

    [Fact]
    public void Kev_tool_decisions_get_an_outcome_the_calibration_tooling_reads()
    {
        var (_, runner, runs, _) = Run();
        var log = runner.Log(ToolCallingConfiguration.ShortlistKev);

        var (accepted, corrected) = ToolCallingReport.AppendKevOutcomes(log,
            runs.Where(run => run.Configuration == ToolCallingConfiguration.ShortlistKev));

        // Only the narrating case has calibrated tool answers, all the weather tool: at ingress and at the no-op check, before
        // the call (accepted), and at the step checkpoint after it ran, when the case wants no more tools (corrected).
        Assert.Equal(2, accepted);
        Assert.Equal(1, corrected);
        var decisions = Calibration.DecisionSet.Load([runner.LogPath(ToolCallingConfiguration.ShortlistKev)], cancellationToken: Ct);
        var labelled = decisions.Records.Where(record => record.QuestionId == "tool" && record.Tier == Calibration.DecisionTier.Calibrated && record.Answer is not null).ToList();
        Assert.Equal(["accepted", "accepted", "corrected"], labelled.Select(record => record.Outcome));
        Assert.All(labelled, record => Assert.Contains(record.Label, new[] { Calibration.OutcomeLabel.Positive, Calibration.OutcomeLabel.Negative }));
    }

    [Fact]
    public async Task A_missing_model_exits_with_the_download_command_and_never_loads_the_others()
    {
        var set = Path.Combine(_root, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "catalogue.json"), Catalogue);
        File.WriteAllText(Path.Combine(set, "cases.jsonl"), Cases);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await ToolCallingCli.RunAsync(["--set", set, "--out", Path.Combine(_root, "out"), "--configs", "shortlist-kev"], output, error,
            () => new MissingKev(), Ct);

        Assert.Equal(1, exit);
        Assert.Contains("kev-0.8b is not present: corvid model download kev-0.8b", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "out", "report.json")));
        Assert.Equal(2, await ToolCallingCli.RunAsync(["--configs", "nope", "--out", _root], output, error, () => new MissingKev(), Ct));
    }

    private sealed class MissingKev : IToolCallingModels
    {
        public ModelBinding Generator() => new(Profile("fake-qwen", ToolCallSupport.TemplateRendered, 8_192), new ScriptedGenerator(Script));

        public ModelBinding Embedder() => new(Profile("keywords", ToolCallSupport.None, 512), new KeywordEmbedder("weather"));

        public ModelBinding? Extractor() => null;

        public IDecider Kev() => throw new ToolCallingModelMissingException("kev-0.8b");

        public IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>();

        public void Dispose()
        {
        }
    }

    internal static ModelProfile Profile(string id, ToolCallSupport toolCalls, int context) =>
        new(id, context, context, toolCalls, StructuredOutputSupport.None, PromptCachingStyle.None, ModelCost.Free, PrivacyClass.Local, "cpu");

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static int s_ids;

    private static ChatResponse Call(string tool, string arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{Interlocked.Increment(ref s_ids)}", tool,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value))]));
}

/// <summary>One generator request as a script sees it: the turn's prompt, results so far and the loop's nudge.</summary>
internal sealed record GeneratorTurn(string Prompt, int Results, string? Nudge);

/// <summary>A minimal scripted generator: the script decides from the conversation so far, so turns can interleave.</summary>
internal sealed class ScriptedGenerator(Func<GeneratorTurn, ChatResponse> script) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var prompt = list.First(message => message.Role == ChatRole.User).Text;
        var results = list.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count();
        return Task.FromResult(script(new GeneratorTurn(prompt, results, TimedChatClient.NudgeOf(list))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>Embeds by keyword: one axis per keyword the text contains, plus a constant axis so no vector is zero.</summary>
internal sealed class KeywordEmbedder(params string[] keywords) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
        {
            var vector = new float[keywords.Length + 1];
            for (var i = 0; i < keywords.Length; i++)
                vector[i] = text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            vector[^1] = 0.1f;
            return new Embedding<float>(vector);
        })));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A calibrated decider answering from a function of the turn's prompt; null withholds the answer (below the floor).</summary>
internal sealed class ScriptedDecider(Func<string, string, DecisionQuestion, double[]?> answer) : IDecider
{
    public string Name => "kev-test";

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = request.State.TryGetProperty("user_message", out var message) ? message.GetString() ?? "" : "";
        var answers = ImmutableDictionary.CreateBuilder<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (key, question) in request.Questions)
            if (answer(prompt, key, question) is { } probabilities)
                answers[key] = DecisionAnswer.For(question, probabilities);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, answers.ToImmutable()));
    }
}
