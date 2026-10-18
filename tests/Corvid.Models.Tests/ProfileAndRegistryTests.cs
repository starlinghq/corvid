using Corvid.Abstractions.Extraction;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

public sealed class ProfileAndRegistryTests
{
    [Fact]
    public void Snapshot_round_trips_with_persisted_wire_names()
    {
        var profile = Profiles.LocalServer("qwen3.5-27b@llama.cpp");

        var snapshot = profile.ToSnapshot();

        Assert.Equal(profile, ModelProfile.FromSnapshot(snapshot));
        Assert.Equal("local-server", snapshot.GetProperty("privacy").GetString());
        Assert.Equal("native", snapshot.GetProperty("tool_calls").GetString());
        Assert.Equal("json_schema", snapshot.GetProperty("structured_output").GetString());
        Assert.Equal(32_000, snapshot.GetProperty("accepted_context").GetInt32());
        Assert.Equal(0m, snapshot.GetProperty("cost").GetProperty("input_per_million_tokens").GetDecimal());
    }

    [Fact]
    public void Snapshot_with_missing_or_unknown_values_is_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => ModelProfile.FromSnapshot(Profiles.Json("""{"id":"x"}""")));
        var unknownPrivacy = Profiles.Local("x").ToSnapshot().GetRawText().Replace("\"local\"", "\"orbit\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ModelProfile.FromSnapshot(Profiles.Json(unknownPrivacy)));
    }

    [Fact]
    public void Profiles_that_cannot_be_planned_against_are_rejected()
    {
        var good = Profiles.Local("x");
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { AcceptedContext = good.ContextWindow + 1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { ContextWindow = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { Cost = new ModelCost(-1, 0) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { Privacy = (PrivacyClass)7 }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Id = " " }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelBinding(good with { AcceptedContext = -1 }, new FakeChatClient("x")));
    }

    [Fact]
    public void Slots_enforce_their_contracts()
    {
        var registry = new ModelSlotRegistry();
        var chat = Profiles.Chat(Profiles.Local("qwen"));
        var embedder = new ModelBinding(Profiles.Local("embeddinggemma2"), new FakeEmbedder());

        registry.Bind(ModelSlots.Generator, chat).Bind(ModelSlots.TextEmbedder, embedder).Bind(ModelSlots.Vision, chat);

        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.Generator, embedder));
        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.TextEmbedder, chat));
        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.Summarizer, chat, fallbacks: [embedder]));
        Assert.Throws<ArgumentException>(() => registry.Bind("generator2", chat));
        // The decider contract arrives with C-004a; until then any service binds.
        registry.Bind(ModelSlots.Decider, new ModelBinding(Profiles.Local("kev-0.8b"), new object()));
        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.Extractor, chat));
        Assert.Equal(typeof(IExtractor), ModelSlots.ContractOf(ModelSlots.Extractor));
        Assert.Equal(typeof(IChatClient), ModelSlots.ContractOf(ModelSlots.Summarizer));
        Assert.Null(ModelSlots.ContractOf(ModelSlots.MediaEmbedder));
        Assert.Equal(7, ModelSlots.All.Count);
        Assert.Same(embedder, registry.Get(ModelSlots.TextEmbedder).Default);
        Assert.Throws<InvalidOperationException>(() => registry.Get(ModelSlots.Extractor));
    }

    [Fact]
    public async Task Embedding_slots_switch_like_chat_slots()
    {
        using var db = new TempLog();
        var first = new ModelBinding(Profiles.Local("embeddinggemma2"), new FakeEmbedder());
        var second = new ModelBinding(Profiles.Local("e5-small") with { Placement = "cpu" }, new FakeEmbedder());
        var slots = new SessionModelSlots(new ModelSlotRegistry().Bind(ModelSlots.TextEmbedder, first), db.Log, "s");

        var result = await slots.SwitchAsync(ModelSlots.TextEmbedder, second, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SwitchStatus.Switched, result.Status);
        Assert.Same(second, slots.Current(ModelSlots.TextEmbedder));
        Assert.Throws<ArgumentException>(() => slots.GetChatClient(ModelSlots.TextEmbedder));
    }
}
