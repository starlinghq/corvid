using System.Runtime.CompilerServices;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Scheduling;

/// <summary>
/// Wraps a slot's service so its calls start on a device thread at one priority. The same model can be wrapped twice: the
/// generator at <see cref="DevicePriority.Interactive"/> for the turn, and at <see cref="DevicePriority.Fabric"/> for the
/// degraded decider's yes/no questions.
/// </summary>
public static class DeviceScheduling
{
    public static IChatClient Schedule(this IChatClient client, DeviceScheduler device, DevicePriority priority) =>
        new ScheduledChatClient(client, device, priority);

    public static IEmbeddingGenerator<string, Embedding<float>> Schedule(this IEmbeddingGenerator<string, Embedding<float>> embedder,
        DeviceScheduler device, DevicePriority priority) => new ScheduledEmbeddingGenerator(embedder, device, priority);

    public static IDecider Schedule(this IDecider decider, DeviceScheduler device, DevicePriority priority) =>
        new ScheduledDecider(decider, device, priority);

    /// <summary>For extractors that do not take a <see cref="TaskScheduler"/>; GLiNER2 takes <see cref="DeviceScheduler.GetTaskScheduler"/>.</summary>
    public static IExtractor Schedule(this IExtractor extractor, DeviceScheduler device, DevicePriority priority) =>
        new ScheduledExtractor(extractor, device, priority);

    // The inner call starts on the device thread. Starling returns a completed task, so the work happens there; a truly
    // asynchronous client continues wherever its own awaits resume, which is never the device thread (it has no context).
    internal static async Task<T> StartOn<T>(DeviceScheduler device, DevicePriority priority, Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        var started = await device.RunAsync(priority, call, cancellationToken).ConfigureAwait(false);
        return await started.ConfigureAwait(false);
    }
}

internal sealed class ScheduledChatClient(IChatClient inner, DeviceScheduler device, DevicePriority priority) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        return DeviceScheduling.StartOn(device, priority, token => base.GetResponseAsync(history, options, token), cancellationToken);
    }

    // Each step of the stream starts on the device: Starling decodes a token per MoveNext.
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var updates = await device.RunAsync(priority,
            token => base.GetStreamingResponseAsync(history, options, token).GetAsyncEnumerator(token), cancellationToken).ConfigureAwait(false);
        try
        {
            while (await DeviceScheduling.StartOn(device, priority, _ => updates.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
                yield return updates.Current;
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class ScheduledEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, DeviceScheduler device,
    DevicePriority priority) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var batch = values as IReadOnlyList<string> ?? values.ToList();
        return DeviceScheduling.StartOn(device, priority, token => base.GenerateAsync(batch, options, token), cancellationToken);
    }
}

internal sealed class ScheduledDecider(IDecider inner, DeviceScheduler device, DevicePriority priority) : IDecider
{
    public string Name => inner.Name;

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
        new(DeviceScheduling.StartOn(device, priority, token => inner.DecideAsync(request, token).AsTask(), cancellationToken));
}

internal sealed class ScheduledExtractor(IExtractor inner, DeviceScheduler device, DevicePriority priority) : IExtractor
{
    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default) =>
        DeviceScheduling.StartOn(device, priority, token => inner.ExtractAsync(request, token), cancellationToken);
}
