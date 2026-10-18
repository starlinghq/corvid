using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Tokens;
using Microsoft.Extensions.AI;

namespace Corvid.Fabric;

/// <summary>
/// The decider when Kev is unavailable (plan §5.6). Every answer is uncalibrated:
/// <list type="bullet">
/// <item>Choices over named items: cosine similarity between the rendered state and each option's <c>name: description</c>;
/// the answer carries the top-1 margin, and policy acts only when it exceeds the configured margin (0.05).</item>
/// <item>Noul questions: the generator answers yes or no. An unparseable reply or a failed call leaves the question unanswered.</item>
/// <item>Gates and questions without a cheap signal: no answer, so the conservative default applies. The generator is not called.</item>
/// <item><c>difficulty</c>: a token-length and tool-step heuristic, logged but not acted on (escalation is manual).</item>
/// </list>
/// Questions not in the catalogue follow their shape: Choice by embedding, Noul by the generator, Score unanswered.
/// </summary>
public sealed class DegradedDecider : IDecider
{
    public const string DeciderName = "degraded";

    // Similarities become probabilities only for the decision log; the margin, not these, decides.
    private const double SoftmaxTemperature = 0.05;

    private const string SystemPrompt =
        "You answer one question about an assistant's current turn. Reply with exactly one word: yes or no.";

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IChatClient _generator;
    private readonly QuestionCatalogue _catalogue;
    private readonly ITokenCounter _counter;

    /// <param name="embedder">The <c>embedder.text</c> slot.</param>
    /// <param name="generator">The <c>generator</c> slot, for yes/no answers.</param>
    public DegradedDecider(IEmbeddingGenerator<string, Embedding<float>> embedder, IChatClient generator,
        QuestionCatalogue? catalogue = null, ITokenCounter? counter = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(generator);
        _embedder = embedder;
        _generator = generator;
        _catalogue = catalogue ?? QuestionCatalogue.Default;
        _counter = counter ?? CharsPerTokenCounter.Instance;
    }

    public string Name => DeciderName;

    public async ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stateText = RenderState(request.State);
        var answers = ImmutableDictionary.CreateBuilder<string, DecisionAnswer>(StringComparer.Ordinal);
        var choices = new List<KeyValuePair<string, DecisionQuestion>>();

        // Ordinal key order keeps generator calls deterministic.
        foreach (var (key, question) in request.Questions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            switch (RuleFor(key, question))
            {
                case DegradedRule.EmbeddingMargin:
                    choices.Add(new(key, question));
                    break;
                case DegradedRule.GeneratorYesNo:
                    if (await AskGeneratorAsync(stateText, question, cancellationToken) is { } yesNo)
                        answers[key] = yesNo;
                    break;
                case DegradedRule.Heuristic:
                    if (Difficulty(request.State, question) is { } level)
                        answers[key] = level;
                    break;
            }
        }

        if (choices.Count > 0)
            foreach (var (key, answer) in await EmbedChoicesAsync(stateText, choices, cancellationToken))
                answers[key] = answer;

        return new DeciderAnswers(Name, answers.ToImmutable());
    }

    private DegradedRule RuleFor(string key, DecisionQuestion question)
    {
        if (_catalogue.TryResolve(key, out var entry))
            return entry.Kind == question.Kind ? entry.Degraded : DegradedRule.ConservativeDefault;
        return question.Kind switch
        {
            QuestionKind.Choice => DegradedRule.EmbeddingMargin,
            QuestionKind.Noul => DegradedRule.GeneratorYesNo,
            _ => DegradedRule.ConservativeDefault,
        };
    }

    private async Task<IEnumerable<KeyValuePair<string, DecisionAnswer>>> EmbedChoicesAsync(string stateText,
        List<KeyValuePair<string, DecisionQuestion>> choices, CancellationToken cancellationToken)
    {
        // One batch: the state, then every option of every choice question.
        var inputs = new List<string> { stateText };
        foreach (var (_, question) in choices)
            inputs.AddRange(question.Options.Select(option => option.ToString()));

        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            embeddings = await _embedder.GenerateAsync(inputs, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
        if (embeddings.Count != inputs.Count)
            return [];

        var state = embeddings[0].Vector;
        var results = new List<KeyValuePair<string, DecisionAnswer>>();
        var next = 1;
        foreach (var (key, question) in choices)
        {
            var similarities = new double[question.Options.Length];
            for (var i = 0; i < similarities.Length; i++)
                similarities[i] = Cosine(state.Span, embeddings[next++].Vector.Span);
            if (similarities.Any(double.IsNaN))
                continue;
            var ordered = similarities.OrderDescending().ToArray();
            results.Add(new(key, DecisionAnswer.For(question, Softmax(similarities), calibrated: false,
                margin: ordered[0] - ordered[1])));
        }
        return results;
    }

    private async Task<DecisionAnswer?> AskGeneratorAsync(string stateText, DecisionQuestion question, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Turn state:\n{stateText}\n\nQuestion: {question.Instructions}\nAnswer yes or no."),
        ];
        ChatResponse response;
        try
        {
            response = await _generator.GetResponseAsync(messages, new ChatOptions { Temperature = 0, MaxOutputTokens = 4 },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
        return ParseYesNo(response.Text) is { } yes
            ? DecisionAnswer.Certain(question, yes ? DecisionQuestion.Yes : DecisionQuestion.No)
            : null;
    }

    /// <summary>The leading word, ignoring case and punctuation: "Yes.", "no, because..." and "**YES**" all parse.</summary>
    internal static bool? ParseYesNo(string? text)
    {
        if (text is null)
            return null;
        var word = new string(text.TrimStart(' ', '\t', '\r', '\n', '*', '"', '\'', '`', '_').TakeWhile(char.IsLetter).ToArray());
        return word.ToLowerInvariant() switch
        {
            "yes" => true,
            "no" => false,
            _ => null,
        };
    }

    /// <summary>
    /// Difficulty from the user message's length and the tool steps taken so far: a certain, uncalibrated level that policy
    /// logs but never escalates on.
    /// </summary>
    private DecisionAnswer? Difficulty(JsonElement state, DecisionQuestion question)
    {
        if (question.Kind != QuestionKind.Score || state.ValueKind != JsonValueKind.Object)
            return null;
        var message = state.TryGetProperty("user_message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
        var steps = state.TryGetProperty("step", out var step) && step.TryGetInt32(out var value) ? value : 0;
        var tokens = _counter.CountTokens(message);
        var level = tokens switch
        {
            < 16 => 0,
            < 64 => 1,
            < 192 => 2,
            < 512 => 3,
            _ => 4,
        };
        level += (steps >= 3 ? 1 : 0) + (steps >= 8 ? 1 : 0);
        level = Math.Min(level, question.Options.Length - 1);
        return DecisionAnswer.Certain(question, question.Options[level].Name);
    }

    /// <summary>Kev renders a JSON object as <c>key: value</c> lines; the degraded tier embeds and prompts with the same form.</summary>
    internal static string RenderState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
            return state.GetRawText();
        var builder = new StringBuilder();
        foreach (var property in state.EnumerateObject())
        {
            builder.Append(property.Name).Append(": ");
            builder.AppendLine(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText());
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>NaN for mismatched or zero vectors, which leaves the question unanswered.</summary>
    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return double.NaN;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? double.NaN : dot / Math.Sqrt(normA * normB);
    }

    private static double[] Softmax(double[] similarities)
    {
        var max = similarities.Max();
        var exp = similarities.Select(s => Math.Exp((s - max) / SoftmaxTemperature)).ToArray();
        var sum = exp.Sum();
        return [.. exp.Select(e => e / sum)];
    }
}
