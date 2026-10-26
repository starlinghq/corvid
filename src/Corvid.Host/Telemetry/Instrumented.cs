using System.Diagnostics;
using System.Runtime.CompilerServices;
using Corvid.Abstractions.Extraction;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Telemetry;

/// <summary>
/// The outermost layer of a chat binding: a <c>chat {model}</c> span with GenAI usage attributes, latency for
/// <c>/status</c>, and, for the turn's generator, streaming. The turn loop asks for whole responses; when a surface is
/// watching the session, this client asks the model for a stream instead, forwards each text delta, and reassembles the
/// same response (Starling's Qwen client guarantees the updates reassemble to the non-streamed contents, tool calls included).
/// </summary>
internal sealed class InstrumentedChatClient(IChatClient inner, ModelProfile profile, string slot, ModelLatency latency, TurnFeed? stream)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = HostTelemetry.StartChat(profile, slot);
        var started = Stopwatch.GetTimestamp();
        var failed = true;
        try
        {
            ChatResponse response;
            // A7: an architect/editor turn's plan is not streamed as the answer; surfaces show it once it is logged.
            if (stream is not null && HostTelemetry.StreamingSessionOf(Activity.Current) is { } session && stream.HasObservers(session)
                && options?.AdditionalProperties?.TryGetValue(global::Corvid.Models.Privacy.ModelRequestKeys.Unstreamed, out _) != true)
            {
                stream.Publish(session, observer => observer.GenerationStarted());
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    if (update.Text is { Length: > 0 } text)
                        stream.Publish(session, observer => observer.Text(text));
                    // K-1: the reasoning a model streams beside its text, for the transcript's thought row.
                    foreach (var reasoning in update.Contents.OfType<TextReasoningContent>())
                        if (reasoning.Text is { Length: > 0 } thought)
                            stream.Publish(session, observer => observer.Thought(thought));
                }
                response = updates.ToChatResponse();
                if (response.Usage is { } used && (used.InputTokenCount is not null || used.OutputTokenCount is not null))
                    stream.Publish(session, observer => observer.Usage(used.InputTokenCount, used.OutputTokenCount));
                else
                {
                    // L3 (A5 review): a provider that reports no usage is estimated, so a child's token budget still counts it.
                    var (input, output) = Estimate(messages, response);
                    stream.Publish(session, observer => observer.UsageEstimated(input, output));
                }
            }
            else
            {
                response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            Describe(activity, response.ModelId, response.Usage, response.FinishReason);
            failed = false;
            return response;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            latency.Record(Stopwatch.GetElapsedTime(started), failed);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = HostTelemetry.StartChat(profile, slot);
        var started = Stopwatch.GetTimestamp();
        var failed = true;
        UsageDetails? usage = null;
        ChatFinishReason? finish = null;
        string? model = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                model ??= update.ModelId;
                finish = update.FinishReason ?? finish;
                foreach (var content in update.Contents.OfType<UsageContent>())
                    usage = content.Details;
                yield return update;
            }
            Describe(activity, model, usage, finish);
            failed = false;
        }
        finally
        {
            if (failed)
                activity?.SetStatus(ActivityStatusCode.Error);
            latency.Record(Stopwatch.GetElapsedTime(started), failed);
        }
    }

    /// <summary>
    /// The tokens of a call its provider did not count: the request's and the response's text through the binding's token
    /// counter, or about four characters a token without one.
    /// </summary>
    private (long Input, long Output) Estimate(IEnumerable<ChatMessage> messages, ChatResponse response)
    {
        var counter = GetService(typeof(Abstractions.Tokens.ITokenCounter)) as Abstractions.Tokens.ITokenCounter;
        long Count(string text) => text.Length == 0 ? 0 : counter?.CountTokens(text) ?? (text.Length + 3) / 4;
        // Tool results too (a fetched page is most of a prompt), so a child's token budget holds on such a provider.
        long Of(ChatMessage message) => Count(message.Text) + message.Contents.OfType<FunctionCallContent>()
            .Sum(call => Count(call.Name) + Count(call.Arguments is null ? "" : string.Join(" ", call.Arguments.Select(pair => pair.Key + " " + pair.Value))))
            + message.Contents.OfType<FunctionResultContent>().Sum(result => Count(result.Result?.ToString() ?? ""));
        return (messages.Sum(Of), response.Messages.Sum(Of));
    }

    private static void Describe(Activity? activity, string? model, UsageDetails? usage, ChatFinishReason? finish)
    {
        if (activity is null)
            return;
        if (model is not null)
            activity.SetTag("gen_ai.response.model", model);
        if (usage?.InputTokenCount is { } input)
            activity.SetTag("gen_ai.usage.input_tokens", input);
        if (usage?.OutputTokenCount is { } output)
            activity.SetTag("gen_ai.usage.output_tokens", output);
        if (finish is { } reason)
            activity.SetTag("gen_ai.response.finish_reasons", new[] { reason.Value });
    }
}

/// <summary>Latency of an embedder binding, for <c>/status</c>.</summary>
internal sealed class InstrumentedEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, ModelLatency latency)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = true;
        try
        {
            var result = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
            failed = false;
            return result;
        }
        finally
        {
            latency.Record(Stopwatch.GetElapsedTime(started), failed);
        }
    }
}

/// <summary>Latency of an extractor binding, for <c>/status</c>.</summary>
internal sealed class InstrumentedExtractor(IExtractor inner, ModelLatency latency) : IExtractor
{
    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = true;
        try
        {
            var result = await inner.ExtractAsync(request, cancellationToken).ConfigureAwait(false);
            failed = false;
            return result;
        }
        finally
        {
            latency.Record(Stopwatch.GetElapsedTime(started), failed);
        }
    }
}
