using System.Text.Json.Nodes;

namespace Corvid.Rules.Tests;

public sealed class LoaderTests
{
    private static string Fails(Action<JsonObject> change) =>
        Assert.Throws<RuleLoadException>(() => Rules.Parse(change)).Message;

    private static string FailsWhere(string where, string eventName = "tool_call_issued") =>
        Assert.Throws<RuleLoadException>(() => Rules.Single(where, eventName)).Message;

    [Fact]
    public void A_valid_rule_loads_with_its_fields()
    {
        var rule = Rules.Parse(rule => rule["description"] = "docs only");

        Assert.Equal("test-rule", rule.Id);
        Assert.Equal(1, rule.Version);
        Assert.Equal("docs only", rule.Description);
        Assert.Equal(RuleSeverity.Low, rule.Severity);
        Assert.False(rule.IsCorrelation);
        Assert.True(Assert.Single(rule.Tests).ExpectMatch);
    }

    [Theory]
    [InlineData("id", "\"Bad_Id\"", "must match")]
    [InlineData("version", "0", "version must be at least 1")]
    [InlineData("severity", "\"severe\"", "severity 'severe'")]
    [InlineData("status", "\"beta\"", "status 'beta'")]
    [InlineData("posture", "\"block\"", "posture 'block'")]
    [InlineData("reason", "\" \"", "reason must not be empty")]
    [InlineData("title", "\"\"", "title must not be empty")]
    public void Invalid_values_fail_the_load(string field, string value, string message)
    {
        Assert.Contains(message, Fails(rule => rule[field] = JsonNode.Parse(value)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("reason")]
    [InlineData("category")]
    [InlineData("tests")]
    public void Required_fields_fail_the_load_when_missing(string field)
    {
        Fails(rule => rule.Remove(field));
    }

    [Fact]
    public void Unknown_fields_fail_the_load()
    {
        Assert.Contains("severty", Fails(rule => rule["severty"] = "high"), StringComparison.Ordinal);
        Assert.Contains("wher", Assert.Throws<RuleLoadException>(() => RuleLoader.Parse("x", Rules.Json(match: """{"event":"tool_call_issued","wher":{}}"""))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_has_exactly_one_of_match_or_correlation()
    {
        Assert.Contains("exactly one of match or correlation", Fails(rule => rule.Remove("match")), StringComparison.Ordinal);
        Assert.Contains("exactly one of match or correlation", Fails(rule => rule["correlation"] = JsonNode.Parse(
            """{"scope":"session","window":"5m","steps":[{"event":"user_message"},{"event":"user_message"}]}""")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_name_must_be_the_id()
    {
        var message = Assert.Throws<RuleLoadException>(() => RuleLoader.Parse("other.rule.json", Rules.Json())).Message;

        Assert.Contains("must be named test-rule.rule.json", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_deny_is_refused_and_names_the_follow_up()
    {
        var message = Fails(rule =>
        {
            rule["posture"] = "enforce";
            rule["enforce"] = "deny";
        });

        Assert.Contains("C-023 follow-up", message, StringComparison.Ordinal);
        Assert.NotNull(Rules.Parse(rule =>
        {
            rule["posture"] = "enforce";
            rule["enforce"] = "ask";
        }));
        Assert.Contains("only to posture enforce", Fails(rule => rule["enforce"] = "ask"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_maturity_gate_applies_to_the_status()
    {
        Assert.Contains("experimental rule needs at least one test", Fails(rule => rule["tests"] = new JsonArray()), StringComparison.Ordinal);
        Assert.Contains("stable rule needs at least one match test and one no_match test", Fails(rule => rule["status"] = "stable"),
            StringComparison.Ordinal);
        var deprecated = Rules.Parse(rule =>
        {
            rule["status"] = "deprecated";
            rule["tests"] = new JsonArray();
        });
        Assert.Empty(deprecated.Tests);
    }

    [Fact]
    public void Embedded_test_events_are_session_events_in_the_persisted_form()
    {
        static void Events(JsonObject rule, string events) =>
            rule["tests"] = JsonNode.Parse($$"""[{"name":"t","verdict":"match","events":[{{events}}]}]""");

        Assert.Contains("not a session event", Fails(rule => Events(rule, """{"type":"tool_call","call_id":"c1"}""")), StringComparison.Ordinal);
        // A misspelt optional field is an error, not a silently missing value.
        Assert.Contains("not a session event", Fails(rule => Events(rule, """{"type":"tool_result","call_id":"c1","result":1,"is_eror":true}""")),
            StringComparison.Ordinal);
        Assert.Contains("not a session event", Fails(rule => Events(rule, """{"type":"user_message"}""")), StringComparison.Ordinal);
        Assert.Contains(".at must be", Fails(rule => Events(rule, """{"type":"user_message","text":"hi","at":-1}""")), StringComparison.Ordinal);

        // The discriminator may come after the fields, and at and session are taken off before the event is read.
        var rule = Rules.Parse(rule => Events(rule, """{"text":"hi","at":30,"session":"s9","type":"user_message"}"""));
        var stored = Assert.Single(Assert.Single(rule.Tests).Events);
        Assert.Equal("s9", stored.SessionId);
        Assert.Equal(Rules.Epoch.AddSeconds(30), stored.RecordedAt);
        Assert.Equal(new Corvid.Abstractions.Events.UserMessage("hi"), stored.Payload);
    }

    [Theory]
    [InlineData("""{"field":"tool_nme","equals":"shell"}""", "no field 'tool_nme'")]
    [InlineData("""{"field":"approved","equals":true}""", "no field 'approved'")]
    [InlineData("""{"field":"tool_name","gt":3}""", "compares numbers")]
    [InlineData("""{"field":"tool_name","equals":3}""", "cannot equal 3")]
    [InlineData("""{"field":"tool_name","regex":"(?<=a)b"}""", "NonBacktracking")]
    [InlineData("""{"field":"tool_name","regex":"(a)\\1"}""", "NonBacktracking")]
    [InlineData("""{"field":"tool_name","regex":"("}""", "NonBacktracking")]
    [InlineData("""{"field":"tool_name","matches":"x"}""", "unknown operator 'matches'")]
    [InlineData("""{"field":"tool_name","equals":"a","regex":"b"}""", "exactly one operator")]
    [InlineData("""{"field":"tool_name"}""", "exactly one operator")]
    [InlineData("""{"all":[]}""", "non-empty array")]
    [InlineData("""{"field":"tool_name","in":[]}""", "non-empty array")]
    [InlineData("""{"field":"derived.call_id","exists":true}""", "not a derived field of tool_call_issued")]
    [InlineData("""{"or":[]}""", "must be all, any or not")]
    public void Predicates_are_validated_against_the_event_type(string where, string message)
    {
        Assert.Contains(message, FailsWhere(where), StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_and_boolean_fields_are_typed()
    {
        Assert.NotNull(Rules.Single("""{"field":"confidence","gte":0.5}""", "decision"));
        Assert.Contains("needs text", FailsWhere("""{"field":"confidence","regex":"1"}""", "decision"), StringComparison.Ordinal);
        Assert.Contains("cannot equal \"yes\"", FailsWhere("""{"field":"approved","equals":"yes"}""", "approval_resolved"), StringComparison.Ordinal);
        Assert.NotNull(Rules.Single("""{"field":"derived.action","regex":"rm"}""", "approval_resolved"));
    }

    [Theory]
    [InlineData("detection_matched", "may not select detection_matched")]
    [InlineData("tool_called", "unknown event 'tool_called'")]
    public void Unknown_or_forbidden_events_fail_the_load(string eventName, string message)
    {
        var json = Rules.Json(match: $$"""{"event":"{{eventName}}"}""");

        Assert.Contains(message, Assert.Throws<RuleLoadException>(() => RuleLoader.Parse("x", json)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"scope":"user","window":"5m","steps":[{"event":"user_message"},{"event":"user_message"}]}""", "scope 'user'")]
    [InlineData("""{"scope":"session","window":"5 minutes","steps":[{"event":"user_message"},{"event":"user_message"}]}""", "must be a duration")]
    [InlineData("""{"scope":"session","window":"0s","steps":[{"event":"user_message"},{"event":"user_message"}]}""", "must be positive")]
    [InlineData("""{"scope":"session","window":"5m","order":"random","steps":[{"event":"user_message"},{"event":"user_message"}]}""", "order 'random'")]
    [InlineData("""{"scope":"session","window":"5m","steps":[{"event":"user_message"}]}""", "2 to 8 steps")]
    [InlineData("""{"scope":"session","window":"5m","steps":[{"id":"a","event":"user_message"},{"id":"a","event":"user_message"}]}""", "step id 'a'")]
    public void Correlations_are_validated(string correlation, string message)
    {
        var text = Fails(rule =>
        {
            rule.Remove("match");
            rule["correlation"] = JsonNode.Parse(correlation);
        });

        Assert.Contains(message, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("120s", 120)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    [InlineData("PT10M", 600)]
    public void Windows_take_short_and_iso_durations(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RuleLoader.Window(text));
    }

    [Fact]
    public void A_pack_refuses_duplicate_ids()
    {
        var json = Rules.Json();

        var exception = Assert.Throws<RuleLoadException>(() => RulePack.Load([KeyValuePair.Create("a/test-rule.rule.json", json),
            KeyValuePair.Create("b/test-rule.rule.json", json)]));
        Assert.Contains("already used", exception.Message, StringComparison.Ordinal);
    }
}
