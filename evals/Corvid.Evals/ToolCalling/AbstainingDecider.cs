using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Corvid.Abstractions.Fabric;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Evals.ToolCalling;

/// <summary>
/// The degraded tier of configurations (a) and (b): every question answered with uniform probabilities, uncalibrated and
/// without a margin, so the policy acts on nothing. A choice has no embedding margin (below), a yes/no is P(yes) = 0.5
/// against floors above 0.5, a score is a heuristic (logged only), a gate needs a calibrated answer. The turn then offers
/// the shortlist, never clarifies, never escalates on difficulty and never stops early: Qwen alone, through the real loop.
/// </summary>
public sealed class AbstainingDecider : IDecider
{
    public const string DeciderName = "abstain";

    public string Name => DeciderName;

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var answers = request.Questions.ToImmutableDictionary(pair => pair.Key,
            pair => DecisionAnswer.For(pair.Value, Enumerable.Repeat(1.0 / pair.Value.Options.Length, pair.Value.Options.Length), calibrated: false),
            StringComparer.Ordinal);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, answers));
    }
}

/// <summary>What one case's turn spent, collected by the timed wrappers. Cases run one at a time; calls within one may overlap.</summary>
public sealed class TurnMeter
{
    private readonly Lock _gate = new();
    private readonly List<GeneratorCallRecord> _generator = [];
    private readonly List<double> _decider = [];
    private readonly List<double> _embed = [];

    public void Reset()
    {
        lock (_gate)
        {
            _generator.Clear();
            _decider.Clear();
            _embed.Clear();
        }
    }

    public void Generator(GeneratorCallRecord call)
    {
        lock (_gate)
            _generator.Add(call);
    }

    public void Decider(double ms)
    {
        lock (_gate)
            _decider.Add(ms);
    }

    public void Embed(double ms)
    {
        lock (_gate)
            _embed.Add(ms);
    }

    public ImmutableArray<GeneratorCallRecord> GeneratorCalls
    {
        get
        {
            lock (_gate)
                return [.. _generator];
        }
    }

    public ImmutableArray<double> DeciderCalls
    {
        get
        {
            lock (_gate)
                return [.. _decider];
        }
    }

    public ImmutableArray<double> EmbedCalls
    {
        get
        {
            lock (_gate)
                return [.. _embed];
        }
    }
}

/// <summary>One generator request of a turn: what it was offered, what it was told, what came back and how long it took.</summary>
/// <param name="Nudge">The loop's appended instruction, if any: <c>required:&lt;tool&gt;</c> (§7 step 6's re-prompt), <c>clarify</c>, <c>finish</c> or <c>budget</c>.</param>
/// <param name="Calls">The response's tool calls as <c>name {arguments}</c>.</param>
public sealed record GeneratorCallRecord(
    string Model,
    double Ms,
    ImmutableArray<string> Offered,
    string? Nudge,
    int? InputTokens,
    int? OutputTokens,
    string Text,
    ImmutableArray<string> Calls);

/// <summary>Times each generator call and records it on the meter. Token counting and other services pass through.</summary>
public sealed class TimedChatClient(IChatClient inner, TurnMeter meter, string model) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var watch = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        var ms = watch.Elapsed.TotalMilliseconds;
        meter.Generator(new GeneratorCallRecord(model, ms,
            [.. options?.Tools?.Select(tool => tool.Name) ?? []],
            NudgeOf(list),
            response.Usage?.InputTokenCount is { } input ? (int)input : null,
            response.Usage?.OutputTokenCount is { } output ? (int)output : null,
            Short(response.Text, 600),
            [.. response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
                .Select(call => call.Name + " " + Corvid.Kernel.Turns.JsonValues.Object(call.Arguments).GetRawText())]));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // TurnRunner does not stream; a streamed call is timed as one response.
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    /// <summary>Which of TurnRunner's appended instructions ends the prompt, by its wording (the constants are private to the loop).</summary>
    public static string? NudgeOf(IList<ChatMessage> messages)
    {
        if (messages.Count == 0 || messages[^1].Role != ChatRole.User)
            return null;
        var text = messages[^1].Text;
        if (text.StartsWith("Call the ", StringComparison.Ordinal) && text.Contains(" tool now", StringComparison.Ordinal))
            return "required:" + text["Call the ".Length..text.IndexOf(" tool now", StringComparison.Ordinal)];
        if (text.StartsWith("The request is ambiguous.", StringComparison.Ordinal))
            return "clarify";
        if (text.StartsWith("Answer the user now from the results above.", StringComparison.Ordinal))
            return "finish";
        if (text.StartsWith("The step budget for this turn is used up.", StringComparison.Ordinal))
            return "budget";
        return null;
    }

    internal static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Times each checkpoint's decider call (the whole request, every question in it).</summary>
public sealed class TimedDecider(IDecider inner, TurnMeter meter) : IDecider
{
    public string Name => inner.Name;

    public async ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await inner.DecideAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            meter.Decider(watch.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>Times each embedding batch (the shortlist's query plus any tool text not cached yet).</summary>
public sealed class TimedEmbedder(IEmbeddingGenerator<string, Embedding<float>> inner, TurnMeter meter)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            meter.Embed(watch.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>
/// The generator slot's one escalation target. Escalation leaves the local generator, so the eval stops there: this answers
/// with fixed text and no call, and <c>TurnResult.Escalations</c> names the reason (difficulty, repair, stuck or context)
/// instead of every escalation ending the turn as Failed. Its window is large, so a context escalation does not fail again.
/// </summary>
public sealed class EscalationStub : IChatClient
{
    public const string Answer = "[escalated: a larger model would take this turn]";

    public static ModelProfile Profile { get; } = new ModelProfile("escalation-stub", 131_072, 131_072, ToolCallSupport.Native,
        StructuredOutputSupport.None, PromptCachingStyle.None, ModelCost.Free, PrivacyClass.Local, "none").Validate();

    public static ModelBinding Binding() => new(Profile, new EscalationStub());

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
