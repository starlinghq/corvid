using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Abstractions.Events;

namespace Corvid.Rules;

// The persisted event form, with exactly the options of Corvid.Kernel's EventJsonContext (C-002): rules match the wire names
// that golden trajectories and the event log use. A parity test fails if the two drift.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionEvent))]
internal sealed partial class RuleEventJsonContext : JsonSerializerContext;

// Embedded test events: the persisted form, but an unknown or misspelt field fails the load rather than being ignored, and the
// "type" discriminator may come anywhere in the object.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionEvent))]
internal sealed partial class RuleFixtureJsonContext : JsonSerializerContext;

// A rule file. Unknown properties fail the load: a misspelt key must not silently fall back to nothing.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RuleFile))]
internal sealed partial class RuleFileJsonContext : JsonSerializerContext;

internal sealed record RuleFile(
    string Id,
    int Version,
    string Title,
    string Severity,
    string Status,
    string Posture,
    string Reason,
    string Category,
    ImmutableArray<RuleTestFile> Tests,
    string? Description = null,
    string? Enforce = null,
    StepFile? Match = null,
    CorrelationFile? Correlation = null);

internal sealed record StepFile(string Event, JsonElement? Where = null, string? Id = null);

internal sealed record CorrelationFile(string Scope, string Window, ImmutableArray<StepFile> Steps, string? Order = null);

internal sealed record RuleTestFile(string Name, string Verdict, ImmutableArray<JsonElement> Events);
