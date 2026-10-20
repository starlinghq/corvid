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
            if (stream is not null && HostTelemetry.StreamingSessionOf(Activity.Current) is { } session && stream.HasObservers(session))
            {
                stream.Publish(session, observer => observer.GenerationStarted());
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    if (update.Text is { Length: > 0 } text)
                        stream.Publish(session, observer => observer.Text(text));
                }
                response = updates.ToChatResponse();
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
