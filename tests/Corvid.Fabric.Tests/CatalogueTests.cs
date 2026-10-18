using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

public sealed class CatalogueTests
{
    [Fact]
    public void Default_catalogue_matches_plan_section_5_3()
    {
        var catalogue = QuestionCatalogue.Default;

        Assert.Equal(
            ["clarify", "clear_results", "delegate", "deliver", "difficulty", "done", "injection", "intent", "memory_op", "needs_recall",
             "remember", "risk", "skill", "skill_opportunity", "skill_write_safe", "stuck", "tool", "wake"],
            catalogue.Ids.Order(StringComparer.Ordinal));
        Assert.Equal(["memory_op", "risk", "skill_write_safe"],
            catalogue.Questions.Where(q => q.Role == QuestionRole.Gate).Select(q => q.Id).Order(StringComparer.Ordinal));
        Assert.All(catalogue.Questions.Where(q => q.Role == QuestionRole.Gate),
            gate => Assert.Equal(DegradedRule.ConservativeDefault, gate.Degraded));

        Assert.Equal(QuestionKind.Score, catalogue["risk"].Kind);
        Assert.Equal(["none", "low", "moderate", "high", "destructive"], catalogue["risk"].Options.Select(o => o.Name));
        Assert.Equal(5, catalogue["difficulty"].Options.Length);
        Assert.Equal([Checkpoint.Ingress, Checkpoint.Step], catalogue["tool"].Checkpoints);
        Assert.False(catalogue["tool"].HasFixedOptions);
        Assert.Equal([FabricConditions.AgentAuthored], catalogue["skill_write_safe"].Requires);
        Assert.Equal("stage_for_review", catalogue["memory_op"].BelowFloor);
        Assert.Equal(FabricActions.Ask, catalogue["risk"].BelowFloor);
    }

    [Fact]
    public void Request_keys_resolve_with_an_instance_suffix()
    {
        Assert.Equal("clear_results", QuestionCatalogue.Default.Resolve("clear_results/call-7").Id);
        Assert.False(QuestionCatalogue.Default.TryResolve("clear", out _));
        Assert.Throws<KeyNotFoundException>(() => QuestionCatalogue.Default.Resolve("unknown"));
    }

    [Fact]
    public void Questions_come_from_the_catalogue_with_fixed_or_caller_options()
    {
        var intent = QuestionCatalogue.Default["intent"].ToQuestion();
        var tool = QuestionCatalogue.Default["tool"].ToQuestion(Fixtures.Tools);

        Assert.Equal(5, intent.Options.Length);
        Assert.Equal("What does the user need right now?", intent.Instructions.Text);
        Assert.Equal(["cron.update", "message.send", "none"], tool.Options.Select(o => o.Name));
        Assert.Throws<ArgumentException>(() => QuestionCatalogue.Default["tool"].ToQuestion());
        Assert.Throws<ArgumentException>(() => QuestionCatalogue.Default["intent"].ToQuestion(Fixtures.Tools));
        Assert.Throws<ArgumentException>(() => QuestionCatalogue.Default["done"].ToQuestion(Fixtures.Tools));
    }

    [Fact]
    public void Default_floors_are_the_plan_starting_values()
    {
        var floors = FloorConfiguration.Default;

        Assert.Equal("2026-10-18.1", floors.Version);
        Assert.Equal(0.05, floors.DegradedChoiceMargin);
        Assert.Equal(QuestionCatalogue.Default.Ids.Count, floors.Floors.Count);
        Assert.Equal(0.5, floors.FloorOf("intent"));
        Assert.Equal(3.5, floors.FloorOf("difficulty"));
        Assert.Equal(0.45, floors.FloorOf("tool"));
        Assert.Equal(0.8, floors.FloorOf("done"));
        Assert.Equal(0.85, floors.FloorOf("skill_write_safe"));
    }

    [Fact]
    public void Floors_round_trip_through_source_generated_json()
    {
        var json = FloorConfiguration.Default.ToJson();

        var parsed = FloorConfiguration.Parse(json);

        Assert.Contains("\"degraded_choice_margin\": 0.05", json, StringComparison.Ordinal);
        Assert.Equal(FloorConfiguration.Default.Version, parsed.Version);
        Assert.Equal(FloorConfiguration.Default.Floors.OrderBy(p => p.Key), parsed.Floors.OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("""{"version":"x","degraded_choice_margin":0.05,"floors":{}}""", "no floor for")]
    [InlineData("""{"version":" ","degraded_choice_margin":0.05,"floors":{}}""", "no version")]
    [InlineData("""{"version":"x","degraded_choice_margin":1.5,"floors":{}}""", "degraded_choice_margin")]
    [InlineData("""{"version":"x","degraded_choice_margin":0.05}""", "Invalid floor configuration")]
    [InlineData("""{"version":"x","degraded_choice_margin":0.05,"floors":{},"extra":1}""", "Invalid floor configuration")]
    [InlineData("not json", "Invalid floor configuration")]
    public void Malformed_floor_files_fail_at_load(string json, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => FloorConfiguration.Parse(json));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Floors_must_cover_the_catalogue_exactly_and_stay_in_range()
    {
        var floors = FloorConfiguration.Default;

        Assert.Contains("unknown question", Assert.Throws<InvalidDataException>(() =>
            (floors with { Floors = floors.Floors.Add("vibes", 0.5) }).Validate(QuestionCatalogue.Default)).Message, StringComparison.Ordinal);
        Assert.Contains("unknown question", Assert.Throws<InvalidDataException>(() =>
            (floors with { Floors = floors.Floors.Add("tool/x", 0.5) }).Validate(QuestionCatalogue.Default)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => (floors with { Floors = floors.Floors.SetItem("done", 1.2) }).Validate(QuestionCatalogue.Default));
        // An expected-score floor is a level, so it may exceed 1 but not the top level.
        (floors with { Floors = floors.Floors.SetItem("difficulty", 4) }).Validate(QuestionCatalogue.Default);
        Assert.Throws<InvalidDataException>(() => (floors with { Floors = floors.Floors.SetItem("difficulty", 4.5) }).Validate(QuestionCatalogue.Default));
    }

    [Fact]
    public void Catalogue_rejects_inconsistent_rows()
    {
        var done = QuestionCatalogue.Default["done"];
        var risk = QuestionCatalogue.Default["risk"];

        Assert.Throws<ArgumentException>(() => new QuestionCatalogue([done, done]));
        Assert.Throws<ArgumentException>(() => new QuestionCatalogue([done with { Id = "done/x" }]));
        Assert.Throws<ArgumentException>(() => new QuestionCatalogue([done with { Measure = FloorMeasure.Confidence }]));
        Assert.Throws<ArgumentException>(() => new QuestionCatalogue([done with { Checkpoints = [] }]));
        Assert.Throws<ArgumentException>(() => new QuestionCatalogue([risk with { Degraded = DegradedRule.Heuristic }]));
        Assert.Throws<ArgumentException>(() => new QuestionCatalogue([done with { Degraded = DegradedRule.EmbeddingMargin }]));
    }
}
