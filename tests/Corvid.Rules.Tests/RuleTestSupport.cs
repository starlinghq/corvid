using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;

namespace Corvid.Rules.Tests;

internal static class Rules
{
    public static readonly DateTimeOffset Epoch = RuleLoader.TestEpoch;

    /// <summary>A valid experimental single-event rule; <paramref name="change"/> edits its JSON before it is parsed.</summary>
    public static string Json(Action<JsonObject>? change = null, string match = """{"event":"tool_call_issued","where":{"field":"tool_name","equals":"shell"}}""")
    {
        var rule = new JsonObject
        {
            ["id"] = "test-rule",
            ["version"] = 1,
            ["title"] = "A test rule",
            ["severity"] = "low",
            ["status"] = "experimental",
            ["posture"] = "detect",
            ["reason"] = "it matched",
            ["category"] = "testing",
            ["match"] = JsonNode.Parse(match),
            ["tests"] = JsonNode.Parse("""[{"name":"one","verdict":"match","events":[{"type":"tool_call_issued","call_id":"c1","tool_name":"shell","arguments":{"command":"ls"}}]}]"""),
        };
        change?.Invoke(rule);
        return rule.ToJsonString();
    }

    public static DetectionRule Parse(Action<JsonObject>? change = null) => RuleLoader.Parse("test-rule.rule.json", Json(change));

    public static DetectionRule Single(string where, string eventName = "tool_call_issued") =>
        RuleLoader.Parse("test-rule.rule.json", Json(match: $$"""{"event":"{{eventName}}","where":{{where}}}"""));

    public static DetectionRule Correlation(string order, string window, params string[] steps) =>
        RuleLoader.Parse("test-rule.rule.json", Json(rule =>
        {
            rule.Remove("match");
            rule["correlation"] = JsonNode.Parse($$"""{"scope":"session","window":"{{window}}","order":"{{order}}","steps":[{{string.Join(",", steps)}}]}""");
            rule["posture"] = "enforce";
        }));

    public static string Step(string eventName, string where) => $$"""{"event":"{{eventName}}","where":{{where}}}""";

    public static DetectionEngine Engine(params DetectionRule[] rules) => new(RulePack.Of(rules));

    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static StoredEvent At(long sequence, double seconds, SessionEvent payload, string session = "s1") =>
        new(session, sequence, Guid.NewGuid(), Epoch + TimeSpan.FromSeconds(seconds), payload);

    public static ToolCallIssued Shell(string command, string call = "c1") =>
        new(call, "shell", Element(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command })));

    public static ToolCallIssued Tool(string name, string arguments, string call = "c1") => new(call, name, Element(arguments));

    /// <summary>Events at one-second steps, sequences from one.</summary>
    public static List<StoredEvent> Log(params SessionEvent[] events) =>
        [.. events.Select((payload, i) => At(i + 1, i, payload))];

    public static ImmutableArray<DetectionFinding> Findings(DetectionRule rule, IReadOnlyList<StoredEvent> events)
    {
        var scan = new DetectionEngine(RulePack.Empty).RunOn(rule, events);
        Assert.Empty(scan.Errors);
        return scan.Findings;
    }
}

internal static class EngineExtensions
{
    /// <summary>Scans with one rule, without building a pack.</summary>
    public static DetectionScan RunOn(this DetectionEngine engine, DetectionRule rule, IReadOnlyList<StoredEvent> events) =>
        engine.Scan(events, [rule]);
}
