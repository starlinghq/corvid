using System.Text.Json;

namespace Corvid.Models.Tests;

/// <summary>A5: the model mix's shape, as a tool argument and in configuration.</summary>
public sealed class ModelMixTests
{
    [Fact]
    public void A_mix_takes_a_name_or_an_object_per_slot()
    {
        var mix = ModelMix.Parse("""
            { "Generator": "qwen3.5-9b",
              "decider": { "model": "kev-4b", "escalate": false },
              "summarizer": { "model": "qwen3.5-0.8b", "ladder": ["qwen3.5-9b"], "fallbacks": [] } }
            """);

        Assert.Equal([ModelSlots.Decider, ModelSlots.Generator, ModelSlots.Summarizer], mix.Slots.Keys);
        Assert.Equal(new ModelMixSlot("qwen3.5-9b"), mix.Slots[ModelSlots.Generator]);
        var decider = mix.Slots[ModelSlots.Decider];
        Assert.Equal(("kev-4b", false), (decider.Model, decider.Escalate));
        Assert.Equal([], decider.Ladder!.Value);
        Assert.Equal(["qwen3.5-9b"], mix.Slots[ModelSlots.Summarizer].Ladder!.Value);
        Assert.Equal([], mix.Slots[ModelSlots.Summarizer].Fallbacks!.Value);
        Assert.Null(mix.Slots[ModelSlots.Generator].Ladder);
    }

    [Fact]
    public void A_mix_round_trips_through_its_json()
    {
        var mix = ModelMix.Parse("""{"generator":"a","decider":{"model":"k","escalate":false},"vision":{"model":"v","ladder":["w"]}}""");

        var again = ModelMix.Parse(mix.ToString());

        Assert.Equal(mix.ToString(), again.ToString());
        Assert.Equal(["w"], again.Slots[ModelSlots.Vision].Ladder!.Value);
        Assert.False(again.Slots[ModelSlots.Decider].Escalate);
        Assert.Equal("a", mix.ToJson()["generator"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"brain":"x"}""", "no slot named \"brain\"")]
    [InlineData("""{"generator":""}""", "blank model name")]
    [InlineData("""{"generator":{"ladder":["x"]}}""", "names no model")]
    [InlineData("""{"generator":{"model":"a","ladder":["b","B"]}}""", "names a model twice")]
    [InlineData("""{"generator":{"model":"a","ladder":["a"]}}""", "holds its own model")]
    [InlineData("""{"generator":{"model":"a","ladder":["b"],"escalate":false}}""", "may not escalate")]
    [InlineData("""{"generator":{"model":"a","temperature":1}}""", "no setting \"temperature\"")]
    [InlineData("""{"generator":7}""", "is a model name or an object")]
    [InlineData("""["generator"]""", "object keyed by slot")]
    [InlineData("""{"generator":"a","GENERATOR":"b"}""", "twice")]
    [InlineData("not json", "not JSON")]
    public void A_wrong_mix_is_refused_with_why(string json, string why)
    {
        var refused = Assert.Throws<ModelMixException>(() => ModelMix.Parse(json));

        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_hidden_characters_is_refused_and_shown_escaped()
    {
        // A right-to-left override and a zero-width space inside a model name.
        var refused = Assert.Throws<ModelMixException>(() => ModelMix.Parse("{\"generator\":\"qwen\\u202E\\u200Bcloud\"}"));

        Assert.Contains("qwen\\u202E\\u200Bcloud", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', refused.Message);
        Assert.DoesNotContain('​', refused.Message);
    }

    [Fact]
    public void An_over_long_chain_or_name_is_refused()
    {
        var ladder = string.Join(",", Enumerable.Range(0, ModelMix.MaxChain + 1).Select(i => $"\"m{i}\""));
        Assert.Throws<ModelMixException>(() => ModelMix.Parse("{\"generator\":{\"model\":\"a\",\"ladder\":[" + ladder + "]}}"));
        Assert.Throws<ModelMixException>(() => ModelMix.Parse("{\"generator\":\"" + new string('a', ModelMix.MaxNameLength + 1) + "\"}"));
    }

    [Fact]
    public void A_passed_mix_goes_over_a_configured_one()
    {
        var configured = ModelMix.Parse("""{"generator":"a","decider":"k"}""");
        var passed = ModelMix.Parse("""{"generator":"b"}""");

        var mix = configured.With(passed);

        Assert.Equal(("b", "k"), (mix.Slots[ModelSlots.Generator].Model, mix.Slots[ModelSlots.Decider].Model));
    }

    [Fact]
    public void Null_is_the_empty_mix_and_the_schema_names_every_slot()
    {
        using var document = JsonDocument.Parse("null");
        Assert.True(ModelMix.Parse(document.RootElement).IsEmpty);

        using var schema = JsonDocument.Parse(ModelMix.Schema);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.All(ModelSlots.All, slot => Assert.True(properties.TryGetProperty(slot, out _)));
    }

    [Fact]
    public void Create_checks_slots_from_configuration_as_parse_does()
    {
        var mix = ModelMix.Create([KeyValuePair.Create("Embedder.Text", new ModelMixSlot("  e5  "))]);

        Assert.Equal("e5", mix.Slots[ModelSlots.TextEmbedder].Model);
        Assert.Throws<ModelMixException>(() => ModelMix.Create([KeyValuePair.Create("generator", new ModelMixSlot("a\nb"))]));
    }
}
