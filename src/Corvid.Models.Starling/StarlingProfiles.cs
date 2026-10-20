using Starlings.Models.EmbeddingGemma2;
using Starlings.Models.Gliner2;
using Starlings.Models.Kev;
using Starlings.Models.Qwen35;

namespace Corvid.Models.Starling;

/// <summary>
/// Model profiles of the in-process Starling slots (plan §2, §4.1). Every model is local and free; placement is
/// <c>cpu</c> because this adapter ships the CPU natives only (CUDA selection is a follow-up).
/// </summary>
public static class StarlingProfiles
{
    public const string CpuPlacement = "cpu";

    /// <summary>
    /// Qwen3.5-0.8B fp32x. Starling refuses prompt plus completion above <see cref="Qwen35ChatModel.MaxContextTokens"/>
    /// (8,192) before any native call and never truncates, so that is both the window and the accepted context; the
    /// model's own 262,144 is not reachable through this runtime. Prompts above 1,666 tokens are outside Starling's
    /// Stable contract (experimental but within its bounds). Tool calls are rendered into the chat template and parsed
    /// back out; nothing constrains decoding, so structured output is unsupported (U-2), and each request prefills the
    /// whole conversation, so there is no prompt caching (U-3).
    /// </summary>
    public static ModelProfile Qwen35 { get; } = new ModelProfile(
        "qwen3.5-0.8b@cpu",
        ContextWindow: Qwen35ChatModel.MaxContextTokens,
        AcceptedContext: Qwen35ChatModel.MaxContextTokens,
        ToolCallSupport.TemplateRendered,
        StructuredOutputSupport.None,
        PromptCachingStyle.None,
        ModelCost.Free,
        PrivacyClass.Local,
        CpuPlacement).Validate();

    /// <summary>GLiNER2 base: at most 512 tokens including the label schema; longer input throws, nothing is truncated.</summary>
    public static ModelProfile Gliner2 { get; } = Encoder("gliner2-base-v1@cpu", Gliner2ExtractorOptions.ProfileMaxSequenceLength);

    /// <summary>EmbeddingGemma 2 text: inputs up to 8,192 tokens; longer input throws, nothing is truncated.</summary>
    public static ModelProfile EmbeddingGemma2 { get; } =
        Encoder("embeddinggemma-2@cpu", EmbeddingGemma2Encoder.MaxSupportedSequenceLength);

    /// <summary>
    /// multilingual-e5-small: 512 tokens including the special tokens (the e5 README's limit; the package has no public
    /// constant). Unlike the others, its tokenizer truncates on the right to 512, as the reference model does.
    /// </summary>
    public static ModelProfile E5 { get; } = Encoder("multilingual-e5-small@cpu", 512);

    /// <summary>
    /// Kev-0.8B: states up to <see cref="KevModelProfile.MaxStateTokens"/> (8,192, the validated context) are accepted and
    /// longer ones refused, never truncated. It answers typed questions and generates nothing.
    /// </summary>
    public static ModelProfile Kev08B { get; } = Encoder("kev-0.8b@cpu", KevModelProfile.MaxStateTokens);

    /// <summary>Kev-4B: the same limits as Kev-0.8B.</summary>
    public static ModelProfile Kev4B { get; } = Encoder("kev-4b@cpu", KevModelProfile.MaxStateTokens);

    private static ModelProfile Encoder(string id, int maxTokens) => new ModelProfile(
        id, maxTokens, maxTokens, ToolCallSupport.None, StructuredOutputSupport.None, PromptCachingStyle.None,
        ModelCost.Free, PrivacyClass.Local, CpuPlacement).Validate();
}
