using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Corvid.Abstractions.Events;

namespace Corvid.Rules;

internal enum FieldKind
{
    String,
    Number,
    Boolean,
    Object,
    Array,
    /// <summary>A <see cref="JsonElement"/> or dictionary: validated up to the field, open beyond it.</summary>
    Open,
}

/// <summary>One field of an event's persisted form, as the field-path validator sees it.</summary>
internal sealed class FieldNode(FieldKind kind)
{
    public FieldKind Kind { get; } = kind;

    /// <summary>For an object, its properties by wire name.</summary>
    public Dictionary<string, FieldNode> Children { get; } = new(StringComparer.Ordinal);

    /// <summary>For an array, the element.</summary>
    public FieldNode? Element { get; set; }

    /// <summary>The kind a value at this path has once arrays are looked through.</summary>
    public FieldKind Leaf => Kind == FieldKind.Array && Element is { } element ? element.Leaf : Kind;
}

/// <summary>
/// The field catalogue (C-023): every event's wire name and the snake_case paths under it, read from the source-generated
/// serializer metadata rather than written by hand, so a new event or field is known to rules without an edit here. Paths
/// under <see cref="DerivedPrefix"/> are computed by the engine, never read from the event.
/// </summary>
internal static class EventFields
{
    public const string DerivedPrefix = "derived.";

    public const string Command = "command";
    public const string ToolName = "tool_name";
    public const string CallId = "call_id";
    public const string Action = "action";

    // Which derived fields each event gets (see DetectionEngine.Derive).
    private static readonly FrozenDictionary<string, ImmutableArray<string>> DerivedFields = new Dictionary<string, ImmutableArray<string>>
    {
        ["tool_call_issued"] = [Command],
        ["tool_result"] = [ToolName, Command],
        ["approval_resolved"] = [CallId, ToolName, Command, Action],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly Lazy<Catalogue> Built = new(Build);

    /// <summary>Wire name by event type, from the polymorphism metadata.</summary>
    public static IReadOnlyDictionary<Type, string> Names => Built.Value.Names;

    /// <summary>The persisted shape of each event, by wire name.</summary>
    public static IReadOnlyDictionary<string, FieldNode> Events => Built.Value.Events;

    public static ImmutableArray<string> Derived(string eventName) =>
        DerivedFields.TryGetValue(eventName, out var fields) ? fields : [];

    public static string NameOf(SessionEvent payload) =>
        Names.TryGetValue(payload.GetType(), out var name) ? name : throw new ArgumentException($"Unknown event type {payload.GetType().Name}.");

    /// <summary>The field a path names on an event, or why there is none.</summary>
    public static FieldNode? Resolve(string eventName, string path, out string? error)
    {
        error = null;
        if (!Events.TryGetValue(eventName, out var node))
        {
            error = $"unknown event '{eventName}'";
            return null;
        }
        if (path.StartsWith(DerivedPrefix, StringComparison.Ordinal))
        {
            var name = path[DerivedPrefix.Length..];
            if (Derived(eventName).Contains(name))
                return new FieldNode(FieldKind.String);
            var known = Derived(eventName);
            error = $"'{path}' is not a derived field of {eventName}" + (known.IsEmpty ? " (it has none)" : $" (it has {string.Join(", ", known.Select(field => DerivedPrefix + field))})");
            return null;
        }

        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0 || !segment.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                error = $"'{path}' is not a dotted snake_case field path";
                return null;
            }
            if (node.Kind == FieldKind.Open)
                return node;
            var container = node.Kind == FieldKind.Array ? Through(node) : node;
            if (container.Kind == FieldKind.Open)
                return container;
            if (container.Kind != FieldKind.Object || !container.Children.TryGetValue(segment, out var child))
            {
                error = $"{eventName} has no field '{path}'" + (container.Kind == FieldKind.Object
                    ? $" ('{segment}' is not one of {string.Join(", ", container.Children.Keys.Order(StringComparer.Ordinal))})"
                    : "");
                return null;
            }
            node = child;
        }
        return node;
    }

    private static FieldNode Through(FieldNode node)
    {
        while (node.Kind == FieldKind.Array && node.Element is { } element)
            node = element;
        return node;
    }

    private sealed record Catalogue(IReadOnlyDictionary<Type, string> Names, IReadOnlyDictionary<string, FieldNode> Events);

    private static Catalogue Build()
    {
        var context = RuleEventJsonContext.Default;
        var root = context.SessionEvent;
        var polymorphism = root.PolymorphismOptions ?? throw new InvalidOperationException("SessionEvent has no polymorphism metadata.");
        var names = new Dictionary<Type, string>();
        var events = new Dictionary<string, FieldNode>(StringComparer.Ordinal);
        var built = new Dictionary<Type, FieldNode>();
        foreach (var derived in polymorphism.DerivedTypes)
        {
            var name = derived.TypeDiscriminator as string
                ?? throw new InvalidOperationException($"{derived.DerivedType.Name} has no string discriminator.");
            names[derived.DerivedType] = name;
            events[name] = Node(context, derived.DerivedType, built);
        }
        return new Catalogue(names.ToFrozenDictionary(), events.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static FieldNode Node(RuleEventJsonContext context, Type type, Dictionary<Type, FieldNode> built)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(DateTimeOffset) || type == typeof(DateTime)
            || type.IsEnum)
            return new FieldNode(FieldKind.String);
        if (type == typeof(bool))
            return new FieldNode(FieldKind.Boolean);
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal)
            || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong))
            return new FieldNode(FieldKind.Number);
        if (type == typeof(JsonElement))
            return new FieldNode(FieldKind.Open);
        if (built.TryGetValue(type, out var existing))
            return existing;

        var info = context.GetTypeInfo(type) ?? throw new InvalidOperationException($"No serializer metadata for {type.Name}.");
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
            {
                var node = new FieldNode(FieldKind.Object);
                built[type] = node;
                foreach (var property in info.Properties)
                    node.Children[property.Name] = Node(context, property.PropertyType, built);
                return node;
            }
            case JsonTypeInfoKind.Enumerable:
            {
                var node = new FieldNode(FieldKind.Array);
                built[type] = node;
                node.Element = Node(context, info.ElementType!, built);
                return node;
            }
            case JsonTypeInfoKind.Dictionary:
                return new FieldNode(FieldKind.Open);
            default:
                return new FieldNode(FieldKind.String);
        }
    }
}
