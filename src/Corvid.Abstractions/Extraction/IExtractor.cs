using System.Collections.Immutable;

namespace Corvid.Abstractions.Extraction;

/// <summary>
/// The <c>extractor</c> slot's contract (plan §4.1): text and the labels to look for in, labelled spans out.
/// Implementations never truncate: input beyond the model's limit throws <see cref="ArgumentException"/>.
/// </summary>
public interface IExtractor
{
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Labels">The entity labels to extract, in the order results are returned. At least one.</param>
/// <param name="Threshold">Minimum span confidence, 0 to 1; null uses the extractor's default.</param>
public sealed record ExtractionRequest(string Text, ImmutableArray<string> Labels, float? Threshold = null);

/// <summary>One extracted span. <c>Text == request.Text[Start..End]</c>; offsets are UTF-16 code units.</summary>
public sealed record ExtractedSpan(string Label, string Text, int Start, int End, float Confidence);

/// <summary>Spans ordered by label in request order, then by descending confidence. Spans of different labels may overlap.</summary>
public sealed record ExtractionResult(ImmutableArray<ExtractedSpan> Spans);
