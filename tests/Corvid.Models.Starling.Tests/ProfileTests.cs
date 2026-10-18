using Corvid.Abstractions.Extraction;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Starling.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void Qwen_profile_states_the_measured_limits()
    {
        var qwen = StarlingProfiles.Qwen35;

        // Starling refuses prompt plus completion above 8,192 tokens (plan §2: "about 8,200").
        Assert.Equal(8_192, qwen.ContextWindow);
        Assert.Equal(8_192, qwen.AcceptedContext);
        Assert.Equal(ToolCallSupport.TemplateRendered, qwen.ToolCalls);
        Assert.Equal(StructuredOutputSupport.None, qwen.StructuredOutput);
        Assert.Equal(PromptCachingStyle.None, qwen.Caching);
        Assert.Equal(PrivacyClass.Local, qwen.Privacy);
        Assert.Equal(ModelCost.Free, qwen.Cost);
        Assert.Equal("cpu", qwen.Placement);
        Assert.Equal(qwen, ModelProfile.FromSnapshot(qwen.ToSnapshot()));
    }

    [Theory]
    [InlineData(StarlingModel.Gliner2, "gliner2-base-v1@cpu", 512)]
    [InlineData(StarlingModel.EmbeddingGemma2, "embeddinggemma-2@cpu", 8_192)]
    [InlineData(StarlingModel.E5, "multilingual-e5-small@cpu", 512)]
    public void Encoder_profiles_are_local_cpu_and_generate_nothing(StarlingModel model, string id, int maxTokens)
    {
        var profile = StarlingModels.Get(model).Profile;

        Assert.Equal(id, profile.Id);
        Assert.Equal(maxTokens, profile.ContextWindow);
        Assert.Equal(maxTokens, profile.AcceptedContext);
        Assert.Equal(ToolCallSupport.None, profile.ToolCalls);
        Assert.Equal(StructuredOutputSupport.None, profile.StructuredOutput);
        Assert.Equal(PrivacyClass.Local, profile.Privacy);
        Assert.Equal("cpu", profile.Placement);
        Assert.Equal(profile, ModelProfile.FromSnapshot(profile.ToSnapshot()));
    }

    [Fact]
    public void Catalogue_names_each_model_once_with_its_slot_and_lock()
    {
        Assert.Equal(["qwen3.5-0.8b", "gliner2-base-v1", "embeddinggemma-2", "multilingual-e5-small"],
            StarlingModels.All.Select(m => m.Name));
        Assert.Equal(Enum.GetValues<StarlingModel>(), StarlingModels.All.Select(m => m.Model));
        Assert.Equal(ModelSlots.Generator, StarlingModels.Qwen35.Slot);
        Assert.Equal(ModelSlots.Extractor, StarlingModels.Gliner2.Slot);
        Assert.Equal(ModelSlots.TextEmbedder, StarlingModels.EmbeddingGemma2.Slot);
        Assert.Equal(ModelSlots.TextEmbedder, StarlingModels.E5.Slot);
        Assert.Equal("qwen3.5-0.8b/ortgenai-fp32x-cpu", StarlingModels.Qwen35.StarlingProfile);
        Assert.Equal("gliner2-base-v1/onnx-fp32-cpu", StarlingModels.Gliner2.StarlingProfile);
        Assert.All(StarlingModels.All, m =>
        {
            Assert.Same(m, StarlingModels.Get(m.Model));
            Assert.Equal(40, m.Lock.Revision.Length);
            Assert.True(m.DownloadBytes > 0);
        });
        // Qwen fp32x is about 3 GB; nobody should download it by accident.
        Assert.True(StarlingModels.Qwen35.DownloadBytes > 2L << 30);

        Assert.True(StarlingModels.TryFind("GLiNER2-base-v1", out var found));
        Assert.Same(StarlingModels.Gliner2, found);
        Assert.False(StarlingModels.TryFind("kev-0.8b", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarlingModels.Get((StarlingModel)42));
    }

    [Fact]
    public void Slot_contracts_match_what_the_adapter_binds()
    {
        Assert.Equal(typeof(IChatClient), ModelSlots.ContractOf(StarlingModels.Qwen35.Slot));
        Assert.Equal(typeof(IExtractor), ModelSlots.ContractOf(StarlingModels.Gliner2.Slot));
        Assert.Equal(typeof(IEmbeddingGenerator<string, Embedding<float>>), ModelSlots.ContractOf(StarlingModels.E5.Slot));
        Assert.True(typeof(IExtractor).IsAssignableFrom(typeof(Gliner2Extractor)));
        Assert.True(typeof(IEmbeddingGenerator<string, Embedding<float>>).IsAssignableFrom(
            typeof(Starlings.Models.E5.Extensions.AI.E5EmbeddingGenerator)));
        Assert.True(typeof(IEmbeddingGenerator<string, Embedding<float>>).IsAssignableFrom(
            typeof(Starlings.Models.EmbeddingGemma2.Extensions.AI.EmbeddingGemma2EmbeddingGenerator)));
    }
}
