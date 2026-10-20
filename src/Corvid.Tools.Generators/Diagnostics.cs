using System.Linq;
using Microsoft.CodeAnalysis;

namespace Corvid.Tools.Generators;

internal static class Diagnostics
{
    private const string Category = "Corvid.Tools";

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        "CVTOOL001", "Tool method must be in a top-level partial class",
        "Tool method '{0}' must be declared in a non-generic, top-level partial class so the generator can add its wrapper",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidName = new(
        "CVTOOL002", "Invalid tool name",
        "Tool name '{0}' is invalid: use 1 to 64 letters, digits, '_' or '-'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateName = new(
        "CVTOOL003", "Duplicate tool name",
        "Tool name '{0}' is used by more than one method in this assembly",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "CVTOOL004", "Unsupported tool parameter",
        "Parameter '{0}' of tool '{1}' has unsupported type '{2}': use string, bool, int, long, float, double, decimal, Guid, DateTime, DateTimeOffset, Uri, an enum, SecretRef, a nullable of these, an array or list of them (not SecretRef), CancellationToken or ToolCallContext; ref, out and params are not supported",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedReturn = new(
        "CVTOOL005", "Unsupported tool return type",
        "Tool '{0}' returns '{1}': return void, string, bool, a number, Guid, DateTime, DateTimeOffset, JsonElement, JsonNode or AIContent, optionally wrapped in Task or ValueTask, so the result serializes without reflection",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingDescription = new(
        "CVTOOL006", "Tool has no description",
        "Tool '{0}' needs a description for the model: set ToolAttribute.Description or add [Description] to the method",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DestructiveWithoutSideEffect = new(
        "CVTOOL007", "Destructive tool declares no side effect",
        "Tool '{0}' is [Destructive] but declares no [SideEffect]; say what it changes so approval policy applies",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericMethod = new(
        "CVTOOL008", "Tool method is generic",
        "Tool method '{0}' must not be generic",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor[] All =
    [
        ContainingTypeNotPartial, InvalidName, DuplicateName, UnsupportedParameter,
        UnsupportedReturn, MissingDescription, DestructiveWithoutSideEffect, GenericMethod,
    ];

    public static DiagnosticDescriptor ById(string id) => All.First(d => d.Id == id);
}
