using System.Collections.Immutable;
using Corvid.Abstractions.Extraction;
using Starlings.Models.Gliner2;
using Starlings.Runtime.Onnx;

namespace Corvid.Models.Starling;

/// <summary>
/// <see cref="IExtractor"/> over Starling's GLiNER2 entity extractor (English, at most 512 tokens with the label
/// schema). Extraction is blocking native work, so it runs on <c>scheduler</c>, never on the caller's thread: the thread
/// pool by default, the device scheduler once it exists (plan §3.3).
/// </summary>
public sealed class Gliner2Extractor : IExtractor, IDisposable
{
    private readonly Func<string, IReadOnlyList<string>, float?, CancellationToken, IReadOnlyList<Gliner2Entity>> _extract;
    private readonly IDisposable? _owned;
    private readonly TaskScheduler _scheduler;

    internal Gliner2Extractor(Func<string, IReadOnlyList<string>, float?, CancellationToken, IReadOnlyList<Gliner2Entity>> extract,
        IDisposable? owned, TaskScheduler? scheduler)
    {
        _extract = extract;
        _owned = owned;
        _scheduler = scheduler ?? TaskScheduler.Default;
    }

    /// <summary>Loads the model on the CPU (this adapter ships CPU natives only).</summary>
    /// <remarks>On Linux, Starling requires the process to start with <c>MALLOC_MMAP_THRESHOLD_=65536</c> for this model.</remarks>
    public static Gliner2Extractor Create(Gliner2ModelFiles files, Gliner2ExtractorOptions? options = null, TaskScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (options?.Session is { Provider: not OnnxExecutionProvider.Cpu })
            throw new NotSupportedException("CUDA placement is built for Kev only (C-029); GLiNER2 and the embedders run on the CPU.");
        var extractor = Gliner2EntityExtractor.Create(files, options);
        return new Gliner2Extractor((text, labels, threshold, token) => extractor.Extract(text, labels, threshold, token).Entities,
            extractor, scheduler);
    }

    /// <exception cref="ArgumentException">No labels, or the text and labels exceed 512 tokens (nothing is truncated).</exception>
    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        if (request.Labels.IsDefaultOrEmpty)
            throw new ArgumentException("At least one label is required.", nameof(request));
        if (request.Threshold is { } threshold && (float.IsNaN(threshold) || threshold is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(request), threshold, "The threshold must be between 0 and 1.");

        return Task.Factory.StartNew(
            () => Map(_extract(request.Text, request.Labels, request.Threshold, cancellationToken)),
            cancellationToken, TaskCreationOptions.DenyChildAttach, _scheduler);
    }

    // Starling already orders by label in request order, then by descending confidence, and returns offsets into the
    // original text, which is the contract's order and offsets.
    internal static ExtractionResult Map(IReadOnlyList<Gliner2Entity> entities) =>
        new([.. entities.Select(e => new ExtractedSpan(e.Label, e.Text, e.Start, e.End, e.Confidence))]);

    public void Dispose() => _owned?.Dispose();
}
