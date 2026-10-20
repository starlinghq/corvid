using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corvid.Fabric;

/// <summary>
/// Versioned floors for the catalogue (plan §5.3, §5.5). The version is recorded on every <c>Decision</c> event, so a
/// replay knows which floors produced each action. <c>corvid evals calibrate</c> (C-021) will write new versions.
/// </summary>
/// <param name="Floors">
/// Per question id: the confidence or P(yes) floor in [0, 1], or for an <see cref="FloorMeasure.ExpectedScore"/> question
/// the expected level at or above which it acts.
/// </param>
/// <param name="DegradedChoiceMargin">The degraded tier acts on a choice only when the top-1 similarity margin exceeds this (§5.6).</param>
/// <param name="DegradedTurnBlocking">
/// Whether an uncalibrated answer may divert the turn (<see cref="CatalogueQuestion.BlocksTurn"/>: <c>clarify</c>,
/// <c>stuck</c>). Off by default: with Qwen3.5-0.8B answering the degraded tier's yes/no questions, <c>clarify</c> said yes to
/// every real prompt tried (C-013), so no tool was ever offered. Below the floor these questions proceed (§5.3).
/// </param>
public sealed record FloorConfiguration(string Version, double DegradedChoiceMargin, ImmutableDictionary<string, double> Floors,
    bool DegradedTurnBlocking = false)
{
    private const string ResourceName = "Corvid.Fabric.floors.json";

    /// <summary>The starting floors of plan §5.3, shipped with the build.</summary>
    public static FloorConfiguration Default { get; } = LoadDefault();

    public double FloorOf(string questionId) =>
        Floors.TryGetValue(questionId, out var floor) ? floor : throw new KeyNotFoundException($"No floor for '{questionId}'.");

    /// <summary>Throws unless every catalogue question has exactly one floor in range. A mis-edited file fails at load, not silently.</summary>
    public FloorConfiguration Validate(QuestionCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (string.IsNullOrWhiteSpace(Version))
            throw new InvalidDataException("Floor configuration has no version.");
        if (!double.IsFinite(DegradedChoiceMargin) || DegradedChoiceMargin is < 0 or >= 1)
            throw new InvalidDataException($"Floors {Version}: degraded_choice_margin must be in [0, 1).");
        if (Floors is null)
            throw new InvalidDataException($"Floors {Version}: missing floors.");
        foreach (var id in Floors.Keys.Where(id => !catalogue.TryResolve(id, out var question) || question.Id != id))
            throw new InvalidDataException($"Floors {Version}: unknown question '{id}'.");
        foreach (var question in catalogue.Questions)
        {
            if (!Floors.TryGetValue(question.Id, out var floor))
                throw new InvalidDataException($"Floors {Version}: no floor for '{question.Id}'.");
            var max = question.Measure == FloorMeasure.ExpectedScore ? question.Options.Length - 1 : 1;
            if (!double.IsFinite(floor) || floor < 0 || floor > max)
                throw new InvalidDataException($"Floors {Version}: '{question.Id}' floor {floor} is outside [0, {max}].");
        }
        return this;
    }

    public static FloorConfiguration Parse(string json, QuestionCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        FloorFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, FloorJsonContext.Default.FloorFile);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid floor configuration.", exception);
        }
        if (file is null)
            throw new InvalidDataException("Null floor configuration.");
        return new FloorConfiguration(file.Version, file.DegradedChoiceMargin,
                file.Floors.ToImmutableDictionary(StringComparer.Ordinal), file.DegradedTurnBlocking)
            .Validate(catalogue ?? QuestionCatalogue.Default);
    }

    public string ToJson() => JsonSerializer.Serialize(
        new FloorFile(Version, DegradedChoiceMargin, new SortedDictionary<string, double>(Floors, StringComparer.Ordinal),
            DegradedTurnBlocking),
        FloorJsonContext.Default.FloorFile);

    private static FloorConfiguration LoadDefault()
    {
        using var stream = typeof(FloorConfiguration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

// degraded_turn_blocking is optional so files written before it (C-004a, C-021) still load, as off.
internal sealed record FloorFile(string Version, double DegradedChoiceMargin, IDictionary<string, double> Floors,
    bool DegradedTurnBlocking = false);

// Unknown properties fail the load: a misspelt key must not silently fall back to nothing.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(FloorFile))]
internal sealed partial class FloorJsonContext : JsonSerializerContext;
