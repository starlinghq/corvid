namespace Corvid.Abstractions.Tokens;

/// <summary>Counts tokens with a specific model's tokenizer, so budgets are planned in that model's units.</summary>
public interface ITokenCounter
{
    /// <summary>Tokens in <paramref name="text"/> on its own, without special tokens or a chat template.</summary>
    int CountTokens(string text);
}
