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
        // C-037's restated condition: strict tool and args each at least (a)'s, and turn p50 no worse; escalation does not gate.
        Assert.Equal(c.TurnMs.P50 - a.TurnMs.P50, headline.TurnP50DeltaMs, 6);
        Assert.Equal(headline.ToolPoints >= 0 && headline.ArgsPoints >= 0 && headline.TurnP50DeltaMs <= 0, headline.Pass);

        var markdown = report.ToMarkdown();
        Assert.Contains("| qwen-all | 9 | 4 (44.4%) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Tool choice | +11.1 points |", markdown, StringComparison.Ordinal);
        Assert.Contains("## Disagreements", markdown, StringComparison.Ordinal);
        Assert.Contains("| invalid | read_file | invalid_name: ?cat_file |", markdown, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal(18, json.RootElement.GetProperty("cases").GetArrayLength());
        Assert.Equal("test.1", json.RootElement.GetProperty("header").GetProperty("set_version").GetString());
        Assert.Equal(9, headline.Cases);
        Assert.Equal(9, json.RootElement.GetProperty("headline").GetProperty("cases").GetInt32());
    }

    [Fact]
    public void S2s_headline_leaves_the_history_cases_out()
    {
        var (_, _, _, scores) = Run();
        var header = new ToolCallingHeader("test.1", new string('a', 64), new string('b', 64), 4, "floors", "abc1234",
            ImmutableSortedDictionary<string, string>.Empty, ImmutableSortedDictionary<string, string>.Empty,
            ImmutableSortedDictionary<string, string>.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var singleTurn = ToolCallingReport.Build(header, scores).Headline!;
        // The same run plus history cases where (c) is right and (a) wrong on everything: they would move the gate if counted.
        var history = scores.Select(score => score with
        {
            Id = "hist-" + score.Id,
            Tags = [.. score.Tags, S4Comparison.GatingTag],
            Outcome = score.Configuration == "qwen-all" ? CaseOutcome.WrongTool : CaseOutcome.Correct,
        });

        var report = ToolCallingReport.Build(header, [.. scores, .. history]);

        Assert.Equal(18, report.Summaries.Single(summary => summary.Configuration == "qwen-all").Cases);
        Assert.Equal(singleTurn, report.Headline);
        Assert.Equal(9, report.Headline!.Cases);
        Assert.Contains("Over the 9 single-turn case(s) of the run", report.ToMarkdown(), StringComparison.Ordinal);
        // A history-only run has no S2 headline.
        Assert.Null(ToolCallingReport.Build(header, [.. history]).Headline);
    }

    [Fact]
    public void Kev_tool_decisions_get_an_outcome_the_calibration_tooling_reads()
    {
        var (_, runner, runs, _) = Run();
        var log = runner.Log(ToolCallingConfiguration.ShortlistKev);

        var outcomes = ToolCallingReport.AppendKevOutcomes(log,
            runs.Where(run => run.Configuration == ToolCallingConfiguration.ShortlistKev));

        // Only the narrating case has calibrated tool answers, all the weather tool: at ingress and at the no-op check, before
        // the call (accepted), and at the step checkpoint after it ran, when the case wants no more tools (corrected).
        Assert.Equal(new KevOutcomes(1, 0, 1, 1), outcomes);
        Assert.Equal(2, outcomes.Accepted);
        var decisions = Calibration.DecisionSet.Load([runner.LogPath(ToolCallingConfiguration.ShortlistKev)], cancellationToken: Ct);
        var labelled = decisions.Records.Where(record => record.QuestionId == "tool" && record.Tier == Calibration.DecisionTier.Calibrated && record.Answer is not null).ToList();
        Assert.Equal(["accepted", "accepted", "corrected"], labelled.Select(record => record.Outcome));
        Assert.All(labelled, record => Assert.Contains(record.Label, new[] { Calibration.OutcomeLabel.Positive, Calibration.OutcomeLabel.Negative }));
    }

    [Fact]
    public void The_restated_condition_gates_on_strict_tool_and_args_and_turn_p50_only()
    {
        var a = Summary(tool: 70, args: 60, p50: 8_000, escalated: 0, lenient: 90);
        Assert.True(Headline.For(a, Summary(tool: 70, args: 60, p50: 8_000, escalated: 50, lenient: 10)).Pass);
        Assert.True(Headline.For(a, Summary(tool: 80, args: 75, p50: 2_300, escalated: 0, lenient: 80)).Pass);
        Assert.False(Headline.For(a, Summary(tool: 69, args: 75, p50: 2_300, escalated: 0, lenient: 100)).Pass);
        Assert.False(Headline.For(a, Summary(tool: 80, args: 59, p50: 2_300, escalated: 0, lenient: 100)).Pass);
        var slower = Headline.For(a, Summary(tool: 80, args: 75, p50: 8_001, escalated: 0, lenient: 100));
        Assert.False(slower.Pass);
        Assert.Equal(1, slower.TurnP50DeltaMs, 6);
        Assert.False(Headline.For(a, Summary(tool: 80, args: 75, p50: double.NaN, escalated: 0, lenient: 100)).Pass);
    }

    private static ConfigurationSummary Summary(int tool, int args, double p50, int escalated, int lenient)
    {
        var empty = Distribution.Of([]);
        var empties = ImmutableSortedDictionary<string, int>.Empty;
        return new ConfigurationSummary("x", 100, empties, tool, args, lenient, lenient, 100 - escalated, tool, args, escalated, empties,
            0, 100, 0, 0, 0, 0, 0, 0, 0, new Distribution(100, p50, p50, p50, p50), empty, empty, empty, empty, empty);
    }

    [Fact]
    public async Task A_confident_description_verdict_withholds_nothing_and_c_asks_no_trusted_description_question()
    {
        var set = ToolCallingSet.Parse(Catalogue, Cases);
        // Kev says the weather server's description instructs the agent, as Kev-0.8B did for most benign extras in the S2 run;
        // C-037: the set's extras are trusted, so (c) never asks about them and the verdict could not withhold anything.
        var bindings = new ToolCallingBindings(
            new ModelBinding(Profile("fake-qwen", ToolCallSupport.TemplateRendered, 8_192), new ScriptedGenerator(Script)),
            new ModelBinding(Profile("keywords", ToolCallSupport.None, 512), new KeywordEmbedder("weather", "note", "file", "web", "online")),
            null, new ScriptedDecider((_, key, _) => key == "injection/mcp:weather" ? [0.9, 0.1] : null));
        var runner = new ToolCallingRunner(set, bindings,
            [ToolCallingConfiguration.ShortlistKev, ToolCallingConfiguration.ShortlistKevNoDescriptions], _root);
        var runs = await runner.RunAsync([.. set.Cases.Where(item => item.Id == "correct")], cancellationToken: Ct);
        var scores = runs.Select(run => ToolCallingScorer.Score(run, set)).ToList();

        var production = Get(scores, "correct", "shortlist-kev");
        Assert.DoesNotContain(runs.Single(run => run.Configuration == ToolCallingConfiguration.ShortlistKev).Events.OfType<Decision>(),
            decision => decision.QuestionId.StartsWith("injection/", StringComparison.Ordinal));
        Assert.False(production.DescriptionFlagged);
        Assert.Contains("weather__get_weather", production.Offered);
        Assert.Equal(CaseOutcome.Correct, production.Outcome);

        var ablation = Get(scores, "correct", "shortlist-kev-no-desc");
        Assert.DoesNotContain(runs.Single(run => run.Configuration == ToolCallingConfiguration.ShortlistKevNoDescriptions).Events.OfType<Decision>(),
            decision => decision.QuestionId.StartsWith("injection/", StringComparison.Ordinal));
        Assert.False(ablation.DescriptionFlagged);
        Assert.Equal(CaseOutcome.Correct, ablation.Outcome);

        var report = ToolCallingReport.Build(new ToolCallingHeader("test.1", new string('a', 64), new string('b', 64), 4, "floors", "abc1234",
            ImmutableSortedDictionary<string, string>.Empty, ImmutableSortedDictionary<string, string>.Empty,
            ImmutableSortedDictionary<string, string>.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), scores);
        Assert.Equal(0, report.Summaries.Single(summary => summary.Configuration == "shortlist-kev").DescriptionsFlagged);
        Assert.Equal(0, report.Summaries.Single(summary => summary.Configuration == "shortlist-kev-no-desc").DescriptionsFlagged);

        // Review of #63, finding 3: an acted outside-description verdict is counted as flagged, not withheld; the tool is
        // still offered in that turn, and the report names the count so.
        var flagged = production with { Acted = ["injection/mcp:weather=mark_tainted"] };
        Assert.True(flagged.DescriptionFlagged);
        Assert.Contains("weather__get_weather", flagged.Offered);
        var flaggedReport = ToolCallingReport.Build(report.Header, [flagged]);
        Assert.Equal(1, flaggedReport.Summaries.Single().DescriptionsFlagged);
        Assert.Contains("| MCP descriptions flagged |", flaggedReport.ToMarkdown(), StringComparison.Ordinal);
        Assert.DoesNotContain("withheld", flaggedReport.ToMarkdown(), StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(flaggedReport.ToJson());
        var summaryJson = json.RootElement.GetProperty("summaries")[0];
        Assert.Equal(1, summaryJson.GetProperty("descriptions_flagged").GetInt32());
        Assert.False(summaryJson.TryGetProperty("descriptions_withheld", out _));
    }

    private const string CueLine = "user: What's the weather in Lisbon?";

    /// <summary>
    /// S4's worst case for the wide arm (C-038): 40 entries, every message at the 80-character cut, the cue at entry 0, and
    /// two seeded calls (the cue's weather lookup and a read later on).
    /// </summary>
    private static string HistoryCase()
    {
        var entries = new List<string>
        {
            """{"user":"What's the weather in Lisbon?"}""",
            """{"tool":"weather__get_weather","args":{"city":"Lisbon"},"result":"{\"city\":\"Lisbon\",\"now\":\"clear\"}"}""",
            """{"assistant":"Clear in Lisbon."}""",
        };
        for (var i = 3; entries.Count < ToolCallingSet.MaxHistory; i++)
            entries.Add(i == 10
                ? """{"tool":"read_file","args":{"path":"owls.txt"},"result":"1\towls"}"""
                : JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [i % 2 == 1 ? "user" : "assistant"] = $"Message {i:00} about owls, barn owls and tawny owls, kept to the cut".PadRight(80, '.'),
                }));
        return $$$"""{"id":"same-for-porto","prompt":"And the same for Porto.","tags":["history","history_far","direct"],"expect":{"tool":"weather__get_weather"},"args":{"city":{"equals":"Porto"}},"cue":0,"history":[{{{string.Join(",", entries)}}}]}""";
    }

    /// <summary>
    /// Kev as S4 would find it if the state mattered: it knows "the same" means the weather only when the cue's one-liner is in
    /// <c>recent</c>; without it the top pick is a web search below the floor, and <c>done</c> after the call says no.
    /// </summary>
    private static double[]? HistoryKev(JsonElement state, string key, DecisionQuestion question)
    {
        var sees = state.GetProperty("recent").EnumerateArray().Any(item => item.GetString() == CueLine);
        var step = state.GetProperty("step").GetInt32();
        double[] Pick(string tool, double p) =>
            [.. question.Options.Select(option => option.Name == tool ? p : (1 - p) / (question.Options.Length - 1))];
        return key switch
        {
            "tool" when step == 0 => sees ? Pick("weather__get_weather", 0.9) : Pick("web__web_search", 0.4),
            "tool" => Pick("none", 0.8),
            "done" => sees ? [0.9, 0.1] : [0.3, 0.7],
            _ => null,
        };
    }

    [Fact]
    public async Task S4_seeds_the_history_and_scores_only_the_turns_own_decisions_in_both_state_arms()
    {
        var set = ToolCallingSet.Parse(Catalogue, HistoryCase());
        var item = Assert.Single(set.Cases);
        Assert.Equal(ToolCallingSet.MaxHistory, item.History.Length);
        var seededLines = item.History.Sum(entry => entry.RenderedLines);
        var generator = new ScriptedGenerator(turn => turn.Prompt == "And the same for Porto." && turn.Results == 0
            ? Call("weather__get_weather", """{"city":"Porto"}""")
            : Text("Rain in Porto."));
        var bindings = new ToolCallingBindings(
            new ModelBinding(Profile("fake-qwen", ToolCallSupport.TemplateRendered, 8_192), generator),
            new ModelBinding(Profile("keywords", ToolCallSupport.None, 512), new KeywordEmbedder("weather", "note", "file", "web", "online")),
            null, new StateScriptedDecider(HistoryKev));
        var runner = new ToolCallingRunner(set, bindings,
            [ToolCallingConfiguration.ShortlistKev, ToolCallingConfiguration.ShortlistKevTranscriptState], _root);
        var runs = await runner.RunAsync(set.Cases, cancellationToken: Ct);
        var scores = runs.Select(run => ToolCallingScorer.Score(run, set)).ToList();

        foreach (var run in runs)
        {
            // The seed is in the log, before the turn; the run's events are the turn's own.
            Assert.Equal(item.History.Length + 2, run.SeedHead);
            var log = runner.Log(run.Configuration).Read(run.SessionId, 0, limit: 4096, cancellationToken: Ct);
            Assert.Equal(["h-1", "h-2"], log.Take((int)run.SeedHead).Select(stored => stored.Payload).OfType<ToolCallIssued>().Select(call => call.CallId));
            Assert.IsType<UserMessage>(run.Events[0]);
            Assert.DoesNotContain(run.Events.OfType<ToolCallIssued>(), call => call.CallId.StartsWith(ToolCallingRunner.SeededCallPrefix, StringComparison.Ordinal));
            Assert.DoesNotContain(run.Events, payload => payload is Compacted);
        }
        var narrow = Get(scores, "same-for-porto", "shortlist-kev");
        var wide = Get(scores, "same-for-porto", "shortlist-kev-transcript");
        foreach (var score in new[] { narrow, wide })
        {
            // Seeded calls are never the turn's: the only call scored is the turn's weather lookup.
            Assert.Equal("weather__get_weather", Assert.Single(score.Calls).Tool);
            Assert.Equal(CaseOutcome.Correct, score.Outcome);
            Assert.Equal(0, score.Cue);
            Assert.Equal(0, score.Kev!.Fallbacks);
            Assert.True(score.Kev.ShortlistHit);
        }

        // 600: the eight latest one-liners at most, the cue long gone. Transcript: every seeded one-liner, the cue first.
        static JsonElement IngressState(CaseRun run) =>
            run.Events.OfType<Decision>().First(decision => decision.QuestionId == "tool").State!.Value;
        var narrowRecent = IngressState(runs.Single(run => run.Configuration == ToolCallingConfiguration.ShortlistKev)).GetProperty("recent");
        var wideRecent = IngressState(runs.Single(run => run.Configuration == ToolCallingConfiguration.ShortlistKevTranscriptState)).GetProperty("recent");
        Assert.InRange(narrowRecent.GetArrayLength(), 1, 8);
        Assert.DoesNotContain(narrowRecent.EnumerateArray(), line => line.GetString() == CueLine);
        Assert.Equal(seededLines, wideRecent.GetArrayLength());
        Assert.Equal(CueLine, wideRecent[0].GetString());
        Assert.Contains(wideRecent.EnumerateArray(), line => line.GetString() == "called read_file");
        Assert.All(wideRecent.EnumerateArray(), line => Assert.DoesNotContain("…", line.GetString(), StringComparison.Ordinal));

        // As scripted: the wide arm right on both tool decisions and on done; 600 wrong at ingress and on done.
        Assert.Equal(["tool:weather__get_weather:True:True", "done:yes:True:True", "tool:none:True:True"],
            wide.Kev!.Decisions.Select(decision => $"{decision.Question}:{decision.Answer}:{decision.Right}:{decision.Acted}"));
        Assert.Equal(["tool:web__web_search:False:False", "done:no:False:False", "tool:none:True:True"],
            narrow.Kev!.Decisions.Select(decision => $"{decision.Question}:{decision.Answer}:{decision.Right}:{decision.Acted}"));
        Assert.True(wide.Kev.Decisions[0].Ingress);
        Assert.Equal("yes", wide.Kev.Decisions[1].Expected);
        Assert.True(narrow.Kev.Decisions.Single(decision => decision.Ingress).StateTokens < wide.Kev.Decisions.Single(decision => decision.Ingress).StateTokens);

        var report = ToolCallingReport.Build(new ToolCallingHeader("test.1", new string('a', 64), new string('b', 64), 4, "floors", "abc1234",
            ImmutableSortedDictionary<string, string>.Empty, ImmutableSortedDictionary<string, string>.Empty,
            ImmutableSortedDictionary<string, string>.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), scores);
        var s4 = report.S4!;
        Assert.Equal((1, 0, 2, 1, 1, 0), (s4.Short.IngressTool, s4.Short.IngressToolRight, s4.Short.Tool, s4.Short.ToolRight, s4.Short.Done, s4.Short.DoneRight));
        Assert.Equal((1, 1, 2, 2, 1, 1), (s4.Wide.IngressTool, s4.Wide.IngressToolRight, s4.Wide.Tool, s4.Wide.ToolRight, s4.Wide.Done, s4.Wide.DoneRight));
        Assert.Equal(-50, s4.ToolPoints, 6);
        Assert.Equal(-100, s4.DonePoints, 6);
        Assert.Equal((1, 0, 0, 1), (s4.PairedCases, s4.BothRight, s4.ShortOnlyRight, s4.WideOnlyRight));
        Assert.False(s4.Pass);
        Assert.Contains("## S4: 600-token state against a transcript-sized state", report.ToMarkdown(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("corvid-tool-calling-report/4", json.RootElement.GetProperty("format").GetString());
        Assert.False(json.RootElement.GetProperty("s4").GetProperty("pass").GetBoolean());
        Assert.False(s4.Inconclusive);
        Assert.False(json.RootElement.GetProperty("s4").GetProperty("inconclusive").GetBoolean());
        Assert.StartsWith("c-038", json.RootElement.GetProperty("s4").GetProperty("pass_condition").GetString(), StringComparison.Ordinal);

        // Both arms' logs label tool and done for calibration, never a seeded call.
        var outcomes = ToolCallingReport.AppendKevOutcomes(runner.Log(ToolCallingConfiguration.ShortlistKevTranscriptState),
            runs.Where(run => run.Configuration == ToolCallingConfiguration.ShortlistKevTranscriptState));
        Assert.Equal(new KevOutcomes(1, 0, 1, 0) { DoneAccepted = 1 }, outcomes);
        var narrowOutcomes = ToolCallingReport.AppendKevOutcomes(runner.Log(ToolCallingConfiguration.ShortlistKev),
            runs.Where(run => run.Configuration == ToolCallingConfiguration.ShortlistKev));
        Assert.Equal(new KevOutcomes(0, 1, 1, 0) { DoneCorrected = 1 }, narrowOutcomes);
        var labelled = Calibration.DecisionSet.Load([runner.LogPath(ToolCallingConfiguration.ShortlistKev)], cancellationToken: Ct).Records
            .Where(record => record.QuestionId is "tool" or "done" && record.Tier == Calibration.DecisionTier.Calibrated).ToList();
        Assert.Equal(["corrected", "corrected", "accepted"], labelled.Select(record => record.Outcome));
    }

    [Fact]
    public void The_s4_condition_allows_the_600_arm_three_points_below_on_tool_and_on_done()
    {
        static S4Arm Arm(int tool, int done) => new("x", 40, 40, 30, 100, tool, 100, done);

        Assert.True(S4Comparison.Passes(Arm(70, 80), Arm(73, 83)));
        Assert.True(S4Comparison.Passes(Arm(90, 90), Arm(73, 83)));
        Assert.False(S4Comparison.Passes(Arm(69, 80), Arm(73, 83)));
        Assert.False(S4Comparison.Passes(Arm(70, 79), Arm(73, 83)));
        // No decisions in an arm is no evidence: it fails.
        Assert.False(S4Comparison.Passes(new S4Arm("x", 40, 0, 0, 0, 0, 100, 80), Arm(73, 83)));
        Assert.False(S4Comparison.Passes(Arm(70, 80), new S4Arm("x", 40, 40, 30, 100, 73, 0, 0)));
        // A fallback or a compaction in either arm leaves calibrated decisions out of a rate unseen: inconclusive, not a pass.
        Assert.False(S4Comparison.IsInconclusive(Arm(90, 90), Arm(73, 83)));
        Assert.False(S4Comparison.Passes(Arm(90, 90), Arm(73, 83) with { Fallbacks = 1 }));
        Assert.False(S4Comparison.Passes(Arm(90, 90) with { Fallbacks = 2 }, Arm(73, 83)));
        Assert.False(S4Comparison.Passes(Arm(90, 90), Arm(73, 83) with { Compacted = 1 }));
        Assert.False(S4Comparison.Passes(Arm(90, 90) with { Compacted = 1 }, Arm(73, 83)));
        Assert.True(S4Comparison.IsInconclusive(Arm(90, 90), Arm(73, 83) with { Fallbacks = 1 }));
        var (low, high) = S4Comparison.Wilson(30, 40);
        Assert.Equal(59.8, low, 1);
        Assert.Equal(85.8, high, 1);
    }

    private const string OverCallCases = """
        {"id":"over","prompt":"Open notes.txt.","tags":["direct"],"expect":{"tool":"read_file"},"args":{"path":{"equals":"notes.txt"}},"results":{"read_file":"hello"}}
        {"id":"late","prompt":"Open late.txt.","tags":["direct"],"expect":{"tool":"read_file"},"results":{"read_file":"hello"}}
        {"id":"alternate","prompt":"Look up hiking trails.","tags":["near_miss"],"expect":{"any_of":["read_file","web__web_search"]}}
        {"id":"chain-over","prompt":"Read todo.txt into a note, then search.","tags":["multi_step"],"steps":[{"expect":{"tool":"read_file"}},{"expect":{"tool":"notes__create_note"}}],"results":{"read_file":"buy milk"}}
        {"id":"chain-wrong","prompt":"Read todo.txt into a note.","tags":["multi_step"],"steps":[{"expect":{"tool":"read_file"}},{"expect":{"tool":"notes__create_note"}}],"results":{"read_file":"buy milk"}}
        {"id":"chatty","prompt":"Thanks, that's all.","tags":["no_tool"],"expect":{"none":true}}
        """;

    /// <summary>Calls past the expected steps, and calls before them, for the over-call rule.</summary>
    private static ChatResponse OverCallScript(GeneratorTurn turn) => (turn.Prompt, turn.Results) switch
    {
        // The right tool, then a different one: over-called on a single-step case, as on a chain.
        ("Open notes.txt.", 0) => Call("read_file", """{"path":"notes.txt"}"""),
        ("Open notes.txt.", 1) => Call("web__web_search", """{"query":"notes"}"""),
        // A different tool, then the right one: the first call that ran was wrong.
        ("Open late.txt.", 0) => Call("web__web_search", """{"query":"late.txt"}"""),
        ("Open late.txt.", 1) => Call("read_file", """{"path":"late.txt"}"""),
        // Both tools the step accepts: the second is a repeat of the matched step, not an extra.
        ("Look up hiking trails.", 0) => Call("read_file", """{"path":"trails.txt"}"""),
        ("Look up hiking trails.", 1) => Call("web__web_search", """{"query":"hiking trails"}"""),
        ("Read todo.txt into a note, then search.", 0) => Call("read_file", """{"path":"todo.txt"}"""),
        ("Read todo.txt into a note, then search.", 1) => Call("notes__create_note", """{"title":"Todo","body":"buy milk"}"""),
        ("Read todo.txt into a note, then search.", 2) => Call("web__web_search", """{"query":"milk"}"""),
        ("Read todo.txt into a note.", 0) => Call("read_file", """{"path":"todo.txt"}"""),
        ("Read todo.txt into a note.", 1) => Call("web__web_search", """{"query":"todo"}"""),
        ("Thanks, that's all.", 0) => Call("read_file", """{"path":"thanks.txt"}"""),
        _ => Text("Done."),
    };

    [Fact]
    public async Task One_rule_scores_an_extra_call_on_single_and_multi_step_cases()
    {
        var set = ToolCallingSet.Parse(Catalogue, OverCallCases);
        var bindings = new ToolCallingBindings(
            new ModelBinding(Profile("fake-qwen", ToolCallSupport.TemplateRendered, 8_192), new ScriptedGenerator(OverCallScript)),
            new ModelBinding(Profile("keywords", ToolCallSupport.None, 512), new KeywordEmbedder("weather", "note", "file", "web", "online")),
            null, new ScriptedDecider((_, _, _) => null));
        var runner = new ToolCallingRunner(set, bindings, [ToolCallingConfiguration.QwenAll], _root);
        var runs = await runner.RunAsync(set.Cases, cancellationToken: Ct);
        var scores = runs.Select(run => ToolCallingScorer.Score(run, set)).ToList();
        const string a = "qwen-all";

        var over = Get(scores, "over", a);
        Assert.Equal(CaseOutcome.OverCalled, over.Outcome);
        Assert.Equal(["read_file", "web__web_search"], over.Calls.Select(call => call.Tool));
        Assert.False(over.ToolCorrect);
        Assert.False(over.ArgumentsCorrect);
        Assert.True(over.ToolCorrectOrOverCalled);
        Assert.True(over.ArgumentsCorrectOrOverCalled);
        Assert.True(over.ArgsMatch);

        var late = Get(scores, "late", a);
        Assert.Equal(CaseOutcome.WrongTool, late.Outcome);
        Assert.False(late.ToolCorrectOrOverCalled);
        Assert.Equal(CaseOutcome.Correct, Get(scores, "alternate", a).Outcome);

        var chainOver = Get(scores, "chain-over", a);
        Assert.Equal(CaseOutcome.OverCalled, chainOver.Outcome);
        Assert.Equal(2, chainOver.StepsMatched);
        var chainWrong = Get(scores, "chain-wrong", a);
        Assert.Equal(CaseOutcome.WrongTool, chainWrong.Outcome);
        Assert.Equal(1, chainWrong.StepsMatched);
        Assert.Equal(CaseOutcome.CalledOnNoTool, Get(scores, "chatty", a).Outcome);

        var report = ToolCallingReport.Build(new ToolCallingHeader("test.1", new string('a', 64), new string('b', 64), 4, "floors", "abc1234",
            ImmutableSortedDictionary<string, string>.Empty, ImmutableSortedDictionary<string, string>.Empty,
            ImmutableSortedDictionary<string, string>.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), scores);
        var summary = Assert.Single(report.Summaries);
        Assert.Equal(1, summary.ToolCorrect);
        Assert.Equal(2, summary.Outcomes["over_called"]);
        Assert.Equal(3, summary.ToolCorrectOrOverCalled);
        Assert.Contains("| qwen-all | 6 | 1 (16.7%) | 1 (16.7%) | 2 | 3 (50.0%) | 3 (50.0%) | 2 |", report.ToMarkdown(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal(3, json.RootElement.GetProperty("summaries")[0].GetProperty("tool_correct_or_over_called").GetInt32());
    }

    [Fact]
    public async Task A_wrong_tool_that_ran_does_not_move_the_expected_step_for_Kev_outcomes()
    {
        var set = ToolCallingSet.Parse(Catalogue, Cases);
        // Kev's top answer on the wrong-tool case is always the expected web search, below the floor so nothing acts and the
        // generator still calls read_file. Every one of those picks is right: the case never got its web search.
        var bindings = new ToolCallingBindings(
            new ModelBinding(Profile("fake-qwen", ToolCallSupport.TemplateRendered, 8_192), new ScriptedGenerator(Script)),
            new ModelBinding(Profile("keywords", ToolCallSupport.None, 512), new KeywordEmbedder("weather", "note", "file", "web", "online")),
            null, new ScriptedDecider((prompt, key, question) => key == "tool" && prompt.StartsWith("Search online", StringComparison.Ordinal)
                ? [.. question.Options.Select(option => option.Name == "web__web_search" ? 0.4 : 0.6 / (question.Options.Length - 1))]
                : null));
        var runner = new ToolCallingRunner(set, bindings, [ToolCallingConfiguration.ShortlistKev], _root);
        var runs = await runner.RunAsync([.. set.Cases.Where(item => item.Id == "wrong")], cancellationToken: Ct);
        var run = Assert.Single(runs);
        Assert.Equal(CaseOutcome.WrongTool, ToolCallingScorer.Score(run, set).Outcome);
        Assert.Contains(run.Events.OfType<ToolResult>(), result => !result.IsError);

        var outcomes = ToolCallingReport.AppendKevOutcomes(runner.Log(ToolCallingConfiguration.ShortlistKev), runs);

        // At ingress and at the step checkpoint after read_file ran: both still expect the web search, so both are accepted.
        Assert.True(outcomes.LaterAccepted > 0);
        Assert.Equal(0, outcomes.Corrected);
        var decisions = Calibration.DecisionSet.Load([runner.LogPath(ToolCallingConfiguration.ShortlistKev)], cancellationToken: Ct);
        Assert.All(decisions.Records.Where(record => record.QuestionId == "tool" && record.Tier == Calibration.DecisionTier.Calibrated
            && record.Answer is not null), record => Assert.Equal("accepted", record.Outcome));
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

/// <summary>
/// A minimal scripted generator: the script decides from the conversation so far, so turns can interleave. The prompt is the
/// turn's message, the last user message that is not the loop's nudge, and only results after it count, so a seeded history
/// (S4) neither renames the turn nor adds to its results.
/// </summary>
internal sealed class ScriptedGenerator(Func<GeneratorTurn, ChatResponse> script) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var nudge = TimedChatClient.NudgeOf(list);
        var last = list.FindLastIndex(nudge is null ? list.Count - 1 : list.Count - 2, message => message.Role == ChatRole.User);
        var prompt = list[last].Text;
        var results = list.Skip(last + 1).SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count();
        return Task.FromResult(script(new GeneratorTurn(prompt, results, nudge)));
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

/// <summary>A calibrated decider answering from a function of the whole state (S4's tests read <c>recent</c> and <c>step</c>).</summary>
internal sealed class StateScriptedDecider(Func<JsonElement, string, DecisionQuestion, double[]?> answer) : IDecider
{
    public string Name => "kev-test";

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        var answers = ImmutableDictionary.CreateBuilder<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (key, question) in request.Questions)
            if (answer(request.State, key, question) is { } probabilities)
                answers[key] = DecisionAnswer.For(question, probabilities);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, answers.ToImmutable()));
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
