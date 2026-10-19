using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corvid.Approvals;

/// <summary>Persisted names for enums. Rule files and approval events store these; renaming one needs a migration.</summary>
internal static class WireNames
{
    private static readonly (ActionEffects Flag, string Name)[] EffectNames =
    [
        (ActionEffects.Read, "read"), (ActionEffects.Write, "write"), (ActionEffects.Delete, "delete"),
        (ActionEffects.Recursive, "recursive"), (ActionEffects.Execute, "execute"), (ActionEffects.Network, "network"),
        (ActionEffects.Signal, "signal"), (ActionEffects.Elevated, "elevated"), (ActionEffects.System, "system"),
        (ActionEffects.Configuration, "configuration"), (ActionEffects.Messaging, "messaging"),
        (ActionEffects.Schedule, "schedule"), (ActionEffects.Memory, "memory"), (ActionEffects.Remote, "remote"),
    ];

    public static string[] Effects(ActionEffects effects) =>
        [.. EffectNames.Where(pair => effects.HasFlag(pair.Flag)).Select(pair => pair.Name)];

    public static ActionEffects Effects(IEnumerable<string> names)
    {
        var effects = ActionEffects.None;
        foreach (var name in names)
        {
            var match = EffectNames.FirstOrDefault(pair => pair.Name == name);
            if (match.Name is null)
                throw new InvalidDataException($"Unknown effect '{name}'.");
            effects |= match.Flag;
        }

        return effects;
    }

    public static string Kind(TargetKind kind) => kind switch
    {
        TargetKind.Path => "path",
        TargetKind.Process => "process",
        TargetKind.ProcessName => "process_name",
        TargetKind.Host => "host",
        TargetKind.Url => "url",
        TargetKind.Recipient => "recipient",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Access(TargetAccess access) => access switch
    {
        TargetAccess.Read => "read",
        TargetAccess.Write => "write",
        TargetAccess.Delete => "delete",
        TargetAccess.Execute => "execute",
        TargetAccess.Signal => "signal",
        TargetAccess.Connect => "connect",
        TargetAccess.Send => "send",
        _ => throw new ArgumentOutOfRangeException(nameof(access)),
    };

    public static string? Location(PathLocation? location) => location switch
    {
        null => null,
        PathLocation.Inside => "inside",
        PathLocation.WorkspaceRoot => "workspace_root",
        PathLocation.Outside => "outside",
        PathLocation.Unresolved => "unresolved",
        _ => throw new ArgumentOutOfRangeException(nameof(location)),
    };

    public static string Scope(ApprovalScope scope) => scope switch
    {
        ApprovalScope.Once => "once",
        ApprovalScope.Session => "session",
        ApprovalScope.Always => "always",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

internal sealed record TargetDocument(string Kind, string Value, string Access, string? Location, bool Recursive, bool Pattern, bool Dynamic);

internal sealed record CommandDocument(string Verb, string? Subcommand, string[] Effects, string[] Via, bool Substitution,
    string[] Unresolved, TargetDocument[] Targets);

/// <summary>The parsed action as the decider reads it (plan §10.2 step 4) and as <c>ApprovalRequested</c> records it.</summary>
internal sealed record ActionDocument(string Kind, string Text, string? Unsupported, bool Tainted, bool? Reversible,
    CommandDocument[] Commands);

internal sealed record RiskDocument(string Question, ActionDocument Action);

internal sealed record RuleDocument(string Decision, string Kind, string Verb, string? Subcommand, string[] Effects, bool Elevated,
    string[] Paths, string[] Names, string Source, DateTimeOffset CreatedAt);

internal sealed record RuleFileDocument(string Format, int FormatVersion, RuleDocument[] Rules);

// Unknown properties fail the load: a misspelt key in a rule file must not silently widen or drop a rule.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ActionDocument))]
[JsonSerializable(typeof(RiskDocument))]
[JsonSerializable(typeof(RuleFileDocument))]
internal sealed partial class ApprovalJsonContext : JsonSerializerContext;

internal static class ActionDocuments
{
    public static ActionDocument From(ActionAnalysis analysis, bool tainted, bool? reversible) => new(
        analysis.Kind, analysis.Text, analysis.Unsupported, tainted, reversible,
        [.. analysis.Commands.Select(command => new CommandDocument(command.Verb, command.Subcommand,
            WireNames.Effects(command.Effects), [.. command.Via], command.Substitution, [.. command.Unresolved],
            [.. command.Targets.Select(target => new TargetDocument(WireNames.Kind(target.Kind), target.Value,
                WireNames.Access(target.Access), WireNames.Location(target.Location), target.Recursive, target.Pattern, target.Dynamic))]))]);

    public static string Json(ActionAnalysis analysis, bool tainted, bool? reversible) =>
        JsonSerializer.Serialize(From(analysis, tainted, reversible), ApprovalJsonContext.Default.ActionDocument);

    public static JsonElement Risk(string question, ActionAnalysis analysis, bool tainted, bool? reversible) =>
        JsonSerializer.SerializeToElement(new RiskDocument(question, From(analysis, tainted, reversible)),
            ApprovalJsonContext.Default.RiskDocument);
}

internal static class ImmutableArrayExtensions
{
    public static ImmutableArray<T> OrEmpty<T>(this ImmutableArray<T> array) => array.IsDefault ? [] : array;
}
