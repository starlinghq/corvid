using Corvid.Abstractions.Tokens;
using Starlings.Models.Qwen35;
using Starlings.Tokenizers;

namespace Corvid.Models.Starling;

/// <summary>
/// Counts tokens with Qwen3.5's own tokenizer (Starling's <c>qwen35-bpe-chat</c> profile), loaded from the model's
/// <c>tokenizer.json</c> without loading the ONNX graph.
/// </summary>
/// <remarks>
/// It counts text, not a rendered conversation: the chat template adds role markers and tool blocks. The exact prompt
/// size of a request is the response's <c>Usage.InputTokenCount</c>.
/// </remarks>
public sealed class Qwen35TokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer;

    private Qwen35TokenCounter(Tokenizer tokenizer) => _tokenizer = tokenizer;

    public static Qwen35TokenCounter Create(Qwen35ModelFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return FromFile(files.TokenizerPath);
    }

    public static Qwen35TokenCounter FromFile(string tokenizerPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenizerPath);
        return new Qwen35TokenCounter(Tokenizer.FromFile(tokenizerPath));
    }

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenizer.Encode(text, addSpecialTokens: false).Count;
    }
}
