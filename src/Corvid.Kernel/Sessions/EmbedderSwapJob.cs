using Corvid.Memory;

namespace Corvid.Kernel.Sessions;

/// <summary>
/// Runs memory's re-embed job after an <c>embedder.text</c> switch (plan §4.3): <see cref="MemoryVectors.BeginSwap"/>, then
/// <see cref="MemoryVectors.ReembedAsync"/> batches until the swap completes. Until then retrieval keeps the old index and
/// FTS. The job runs on the thread pool; its model calls go to the device at background priority when the host wraps the
/// embedders <see cref="MemoryVectors"/> holds with <see cref="Scheduling.DeviceScheduling.Schedule(Microsoft.Extensions.AI.IEmbeddingGenerator{string, Microsoft.Extensions.AI.Embedding{float}}, Scheduling.DeviceScheduler, Scheduling.DevicePriority)"/>.
/// </summary>
/// <remarks>
/// <see cref="MemoryVectors"/> embeds only with embedders loaded when it was built, so the host builds it with every
/// <c>embedder.text</c> binding the registry lists (default, escalation and fallbacks).
/// </remarks>
public sealed class EmbedderSwapJob : IAsyncDisposable
{
    private readonly MemoryVectors _vectors;
    private readonly int _batchSize;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _running;

    public EmbedderSwapJob(MemoryVectors vectors, int batchSize = 64)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _vectors = vectors;
        _batchSize = batchSize;
    }

    /// <summary>The run started last, with its final progress. A run that stops short (an embedder failure) can be restarted.</summary>
    public Task<ReembedProgress?> Current { get; private set; } = Task.FromResult<ReembedProgress?>(null);

    /// <summary>Records the swap and starts re-embedding, replacing a run in progress.</summary>
    public EmbedderState Start(string embedderId)
    {
        var state = _vectors.BeginSwap(embedderId);
        lock (_gate)
        {
            _running?.Cancel();
            _running = new CancellationTokenSource();
            var token = _running.Token;
            Current = state.Pending is null ? Task.FromResult<ReembedProgress?>(null) : Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        return state;
    }

    private async Task<ReembedProgress?> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var progress = await _vectors.ReembedAsync(_batchSize, cancellationToken).ConfigureAwait(false);
            // No progress with items left means the embedder failed on them; stop rather than spin, and keep the swap pending.
            if (progress.Completed || progress.Embedded == 0)
                return progress;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? running;
        lock (_gate)
            running = _running;
        if (running is null)
            return;
        await running.CancelAsync().ConfigureAwait(false);
        await ((Task)Current).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        running.Dispose();
    }
}
