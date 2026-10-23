using System.Text.Json;
using Corvid.Evals.ToolCalling;
using Corvid.Kernel.Turns;

namespace Corvid.Evals.Tests;

/// <summary>The committed S2 tool-calling golden set (C-034) loads, validates and covers what the plan asks of it.</summary>
public sealed class ToolCallingSetTests
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "tool-calling");

    private static ToolCallingSet Committed() =>
        ToolCallingSet.Load(Path.Combine(Directory, "catalogue.json"), Path.Combine(Directory, "cases.jsonl"));

    [Fact]
    public void Every_committed_case_loads_and_validates()
    {
        var set = Committed();

        Assert.Equal("2026-10-23.1", set.Version);
        Assert.Equal(64, set.CatalogueSha256.Length);
        // S2's 208 single-turn cases and S4's history cases (C-038).
        Assert.InRange(set.Cases.Length, 240, 270);
        Assert.Equal(17, set.Catalogue.Count(tool => tool.IsBuiltin));
        Assert.Equal(13, set.Catalogue.Count(tool => !tool.IsBuiltin));
        // MCP-like extras carry no examples (as production MCP tools), built-ins carry BuiltinTools.Examples.
        Assert.All(set.Catalogue, tool => Assert.Equal(tool.IsBuiltin, !tool.Examples.IsEmpty));
        Assert.All(set.Catalogue.Where(tool => !tool.IsBuiltin), tool => Assert.StartsWith(tool.Origin["mcp:".Length..] + "__", tool.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void The_set_meets_its_coverage_minima()
    {
        var set = Committed();
        int Tagged(string tag) => set.Cases.Count(item => item.HasTag(tag));

        foreach (var builtin in set.Catalogue.Where(tool => tool.IsBuiltin))
            Assert.True(set.Cases.Count(item => item.ExpectedTools.Contains(builtin.Name)) >= 5, $"{builtin.Name} is expected in fewer than 5 cases");
        // Every extra is used too, or it would only be a distractor.
        foreach (var extra in set.Catalogue.Where(tool => !tool.IsBuiltin))
            Assert.Contains(set.Cases, item => item.ExpectedTools.Contains(extra.Name));
        Assert.True(Tagged("no_tool") >= 15, "no_tool");
        Assert.True(Tagged("required") >= 15, "required");
        Assert.InRange(set.Cases.Count(item => item.HasTag("multi_step") && !item.HasTag("history")), 8, 12);
        Assert.InRange(Tagged("near_miss"), 25, 120);
        Assert.Equal(100, Tagged("s5"));
        Assert.Equal(48, Tagged("tempting"));
        Assert.All(set.Cases.Where(item => item.HasTag("s5")), item => Assert.StartsWith("S5 #", item.Note, StringComparison.Ordinal));
        Assert.All(set.Cases.Where(item => item.HasTag("no_tool")), item => Assert.True(item.ExpectsNoTool));
        Assert.All(set.Cases.Where(item => item.MultiStep), item => Assert.InRange(item.Steps.Length, 2, 4));
        // A required case wants a call by construction: prose is wrong.
        Assert.All(set.Cases.Where(item => item.HasTag("required")), item => Assert.False(item.ExpectsNoTool));
    }

    [Fact]
    public void The_history_cases_meet_S4s_minima()
    {
        var set = Committed();
        var history = set.Cases.Where(item => item.HasTag("history")).ToList();
        var far = history.Where(item => item.HasTag("history_far")).ToList();
        var narrow = TurnOptions.Default.RecentEvents;

        Assert.True(history.Count >= 36, $"{history.Count} history cases");
        Assert.True(far.Count >= 15, $"{far.Count} history_far cases");
        Assert.All(set.Cases, item => Assert.Equal(item.HasTag("history"), !item.History.IsEmpty));
        Assert.All(history, item => Assert.InRange(item.History.Length, ToolCallingSet.MinHistory, ToolCallingSet.MaxHistory));
        // history_far: the cue is outside the 600 arm's window even counting one line per entry (a tool entry renders two).
        Assert.All(far, item => Assert.True(item.Cue < item.History.Length - narrow, $"{item.Id}: cue {item.Cue} of {item.History.Length}"));
        Assert.All(history.Where(item => !item.HasTag("history_far") && item.Cue is not null),
            item => Assert.True(item.Cue >= item.History.Length - narrow, $"{item.Id}: a near cue is in the last {narrow} entries"));
        // The wide arm is lossless for message text: TurnRunner cuts each one-liner at 80 characters, and every rendered line
        // of the history, plus a four-step turn's own eight, fits its RecentEvents.
        Assert.All(history.SelectMany(item => item.History.Where(entry => entry.Kind != HistoryKind.Tool).Select(entry => (item.Id, entry.Text))),
            pair => Assert.True(pair.Text.Length <= 80, $"{pair.Id}: '{pair.Text}' is over 80 characters"));
        Assert.All(history, item => Assert.True(item.History.Sum(entry => entry.RenderedLines) + 8 <= ToolCallingRunner.TranscriptRecentEvents, item.Id));
        // Every shape: single-step, chains and no-tool; history cases are not S5's or tempting ones (their counts are fixed).
        Assert.Contains(history, item => item.MultiStep);
        Assert.Contains(history, item => item.ExpectsNoTool);
        Assert.Contains(history, item => !item.MultiStep && !item.ExpectsNoTool);
        Assert.True(history.Count(item => item.Cue is null) >= 5, "controls: the current message alone decides");
        Assert.DoesNotContain(history, item => item.HasTag("s5") || item.HasTag("tempting"));
        Assert.All(history, item => Assert.StartsWith("S4 ", item.Note, StringComparison.Ordinal));
    }

    [Fact]
    public void Constraints_compare_as_the_labels_mean()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        var set = ToolCallingSet.Parse(Catalogue, """
            {"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"to":{"equals":" alice "},"channel":{"enum":["sms","email"]},"text":{"contains":["late","bus"]},"count":{"equals":3}}}
            {"id":"b","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"to":{"regex":"^(\\./)?a\\.txt$"},"text":{"present":true}}}
            """);
        var a = set.Cases[0].Steps[0].Args;
        var b = set.Cases[1].Steps[0].Args;

        Assert.True(a["to"].Holds(J("\"Alice\"")));
        Assert.False(a["to"].Holds(J("\"Alice B\"")));
        Assert.True(a["channel"].Holds(J("\"EMAIL\"")));
        Assert.True(a["text"].Holds(J("\"The BUS is late\"")));
        Assert.False(a["text"].Holds(J("\"late\"")));
        Assert.True(a["count"].Holds(J("3.0")));
        Assert.False(a["count"].Holds(null));
        Assert.True(b["to"].Holds(J("\"./A.txt\"")));
        Assert.False(b["to"].Holds(J("\"b/a.txt\"")));
        Assert.False(b["text"].Holds(J("null")));
    }

    private const string Catalogue = """
        {"version":"t","tools":[
          {"name":"send","origin":"builtin","description":"Send.","parameters":{"type":"object","properties":{"to":{"type":"string"},"text":{"type":"string"},"channel":{"type":"string","enum":["sms","email"]},"count":{"type":"integer"}},"required":["to"]},"examples":["send it"]},
          {"name":"notes__find","origin":"mcp:notes","description":"Find notes.","parameters":{"type":"object","properties":{"query":{"type":"string"}}}}
        ]}
        """;

    public static TheoryData<string, string> MalformedCases => new()
    {
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"colour":"red"}""", "unknown field 'colour'" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"post"}}""", "'post' is not in the catalogue" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"subject":{"equals":"x"}}}""", "no expected tool declares 'subject'" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"channel":{"equals":"fax"}}}""", "outside send's enum" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"channel":{"enum":["sms","pager"]}}}""", "outside send's enum" },
        { "{\"id\":\"a\",\"prompt\":\"p\",\"tags\":[\"direct\"],\"expect\":{\"tool\":\"send\"}}\n{\"id\":\"a\",\"prompt\":\"q\",\"tags\":[\"direct\"],\"expect\":{\"tool\":\"send\"}}", "duplicate id 'a'" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"to":{"regex":"("}}}""", "bad regex" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"args":{"to":{"equals":"x","contains":["y"]}}}""", "exactly one of equals" },
        { """{"id":"a","prompt":"p","tags":["no_tool"],"expect":{"none":true},"args":{"to":{"present":true}}}""", "constrains no arguments" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"none":true}}""", "go together" },
        { """{"id":"a","prompt":"p","tags":["direct","sparkly"],"expect":{"tool":"send"}}""", "unknown tag 'sparkly'" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"any_of":["send"]}}""", "at least two tools" },
        { """{"id":"a","prompt":"p","tags":["multi_step"],"steps":[{"expect":{"tool":"send"}}]}""", "at least two steps" },
        { """{"id":"a","prompt":"p","tags":["multi_step"],"expect":{"tool":"send"},"steps":[{"expect":{"tool":"send"}},{"expect":{"tool":"send"}}]}""", "no top-level expect" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"},"results":{"post":"x"}}""", "'post' is not in the catalogue" },
        { """{"id":"A b","prompt":"p","tags":["direct"],"expect":{"tool":"send"}}""", "lower-case words" },
        { """{"id":"a","prompt":"p","tags":["direct"],"expect":{"tool":"send"}""", "line 1" },
        { History("""{"tool":"post","args":{},"result":"ok"}"""), "'post' is not in the catalogue" },
        { History("""{"tool":"send","args":{"subject":"x"},"result":"ok"}"""), "send does not declare 'subject'" },
        { History("""{"tool":"send","args":{"to":"x"}}"""), "missing 'result'" },
        { History("""{"user":"hi","assistant":"hello"}"""), "is one of" },
        { History("""{"system":"be brief"}"""), "unknown field 'system'" },
        { History("", cue: 15), "cue 15 is out of range: the history has 15 entries" },
        { History("", cue: -1), "cue -1 is out of range" },
        { History("", count: 14), "history holds 15 to 40 entries, not 14" },
        { History("", count: 41), "not 41" },
        { History("", tags: "\"direct\""), "a history and the history tag go together" },
        { """{"id":"a","prompt":"p","tags":["direct","history_far"],"expect":{"tool":"send"}}""", "a history_far case has a history" },
        { History("", tags: "\"direct\",\"history\",\"history_far\""), "a history_far case has a history and names its cue" },
    };

    /// <summary>A case with a valid history of <paramref name="count"/> messages, the last replaced by <paramref name="last"/> when given.</summary>
    private static string History(string last, int count = 15, int? cue = null, string tags = "\"direct\",\"history\"")
    {
        var entries = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? $$"""{"user":"message {{i}}"}""" : $$"""{"assistant":"reply {{i}}"}""").ToList();
        if (last.Length > 0)
            entries[^1] = last;
        var cueField = cue is null ? "" : $",\"cue\":{cue}";
        return $$"""{"id":"a","prompt":"p","tags":[{{tags}}],"expect":{"tool":"send"}{{cueField}},"history":[{{string.Join(",", entries)}}]}""";
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void The_loader_rejects_malformed_cases(string cases, string message)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ToolCallingSet.Parse(Catalogue, cases));
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"version":"t","tools":[{"name":"x","origin":"builtin","description":"","parameters":{"type":"object"},"risk":"low"}]}""", "unknown field 'risk'")]
    [InlineData("""{"version":"t","tools":[{"name":"notes__find","origin":"mcp:mail","description":"","parameters":{"type":"object"}}]}""", "mcp:<server>")]
    [InlineData("""{"version":"t","tools":[{"name":"notes__find","origin":"mcp:notes","description":"","parameters":{"type":"object"},"examples":["a"]}]}""", "no examples")]
    [InlineData("""{"version":"t","tools":[{"name":"x","origin":"builtin","description":"","parameters":{"type":"object"}},{"name":"x","origin":"builtin","description":"","parameters":{"type":"object"}}]}""", "duplicate tool 'x'")]
    public void The_loader_rejects_a_malformed_catalogue(string catalogue, string message)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ToolCallingSet.Parse(catalogue, """{"id":"a","prompt":"p","tags":["no_tool"],"expect":{"none":true}}"""));
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }
}
