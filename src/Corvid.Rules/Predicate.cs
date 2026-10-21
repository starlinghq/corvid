using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Corvid.Rules;

/// <summary>An event as rules see it: its persisted JSON form and the fields the engine derived for it.</summary>
internal sealed class EventView(string name, JsonElement json, IReadOnlyDictionary<string, string> derived)
{
    public string Name { get; } = name;

    public JsonElement Json { get; } = json;

    public IReadOnlyDictionary<string, string> Derived { get; } = derived;
}

/// <summary>
/// The predicate language (C-023): <c>all</c>, <c>any</c>, <c>not</c>, and leaves on a field path with one of <c>equals</c>,
/// <c>in</c>, <c>regex</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c> or <c>exists</c>. A missing path is false and never
/// throws; a path through an array matches when any element does; a string test on an object or array tests every string
/// under it. Not CEL: a tree small enough to validate against the event types at load.
/// </summary>
internal abstract class Predicate
{
    /// <summary>
    /// Text tested per value: a larger tool result is cut here, so one evaluation's work is bounded. A derived field (a shell
    /// command line) longer than this is a rule error instead (<see cref="TextLimitException"/>): cutting it would let padding
    /// push a command past every regex, and an error fails closed for enforce rules and makes <c>corvid scan</c> exit 1.
    /// </summary>
    public const int MaxText = 64 * 1024;

    /// <summary>
    /// The match timeout for every rule regex. A timeout is a rule error, never a match or a no-match. NonBacktracking is
    /// linear in the (capped) input, so this bounds wall-clock time on a loaded machine rather than pattern cost: 100 ms
    /// timed out on a few-character command while a parallel test run built the lazy automata.
    /// </summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static Predicate True { get; } = new Constant(true);

    public abstract bool Evaluate(EventView view);

    /// <summary>Parses and validates a predicate for one event type. Throws <see cref="FormatException"/> with the reason.</summary>
    public static Predicate Parse(JsonElement element, string eventName, string at = "where")
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{at} must be an object");
        var properties = element.EnumerateObject().ToList();
        if (properties.Count == 1 && properties[0].Name is "all" or "any")
        {
            var name = properties[0].Name;
            if (properties[0].Value.ValueKind != JsonValueKind.Array || properties[0].Value.GetArrayLength() == 0)
                throw new FormatException($"{at}.{name} must be a non-empty array");
            var children = properties[0].Value.EnumerateArray().Select((child, i) => Parse(child, eventName, $"{at}.{name}[{i}]")).ToImmutableArray();
            return name == "all" ? new All(children) : new AnyOf(children);
        }
        if (properties.Count == 1 && properties[0].Name == "not")
            return new Not(Parse(properties[0].Value, eventName, at + ".not"));

        if (!element.TryGetProperty("field", out var fieldElement) || fieldElement.ValueKind != JsonValueKind.String)
            throw new FormatException($"{at} must be all, any or not, or name a field with one operator");
        var path = fieldElement.GetString()!;
        var operators = properties.Where(property => property.Name != "field").ToList();
        if (operators.Count != 1)
            throw new FormatException($"{at} on '{path}' must have exactly one operator, not {(operators.Count == 0 ? "none" : string.Join(", ", operators.Select(o => o.Name)))}");
        var field = EventFields.Resolve(eventName, path, out var error) ?? throw new FormatException($"{at}: {error}");
        var (op, value) = (operators[0].Name, operators[0].Value);
        var leafPath = new FieldPath(path);
        switch (op)
        {
            case "equals":
                Check(field, value, at, path);
                return new Equal(leafPath, [value.Clone()]);
            case "in":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                    throw new FormatException($"{at}: 'in' on '{path}' takes a non-empty array");
                foreach (var item in value.EnumerateArray())
                    Check(field, item, at, path);
                return new Equal(leafPath, [.. value.EnumerateArray().Select(item => item.Clone())]);
            case "regex":
                if (value.ValueKind != JsonValueKind.String)
                    throw new FormatException($"{at}: 'regex' on '{path}' takes a string");
                if (field.Leaf is FieldKind.Number or FieldKind.Boolean)
                    throw new FormatException($"{at}: 'regex' needs text, but '{path}' is a {Describe(field.Leaf)}");
                return new Match(leafPath, Compile(value.GetString()!, at, path));
            case "gt" or "gte" or "lt" or "lte":
                if (value.ValueKind != JsonValueKind.Number)
                    throw new FormatException($"{at}: '{op}' on '{path}' takes a number");
                if (field.Leaf is not (FieldKind.Number or FieldKind.Open))
                    throw new FormatException($"{at}: '{op}' compares numbers, but '{path}' is a {Describe(field.Leaf)}");
                return new Compare(leafPath, op, value.GetDouble());
            case "exists":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException($"{at}: 'exists' on '{path}' takes true or false");
                return value.ValueKind == JsonValueKind.True ? new Exists(leafPath) : new Not(new Exists(leafPath));
            default:
                throw new FormatException($"{at}: unknown operator '{op}' (equals, in, regex, gt, gte, lt, lte, exists)");
        }
    }

    private static void Check(FieldNode field, JsonElement value, string at, string path)
    {
        var leaf = field.Leaf;
        var fits = value.ValueKind switch
        {
            JsonValueKind.String => leaf is FieldKind.String or FieldKind.Object or FieldKind.Open,
            JsonValueKind.Number => leaf is FieldKind.Number or FieldKind.Open,
            JsonValueKind.True or JsonValueKind.False => leaf is FieldKind.Boolean or FieldKind.Open,
            JsonValueKind.Null => true,
            _ => false,
        };
        if (!fits)
            throw new FormatException($"{at}: '{path}' is a {Describe(leaf)} and cannot equal {value.GetRawText()}");
    }

    private static string Describe(FieldKind kind) => kind switch
    {
        FieldKind.String => "string",
        FieldKind.Number => "number",
        FieldKind.Boolean => "boolean",
        FieldKind.Object => "object",
        FieldKind.Array => "array",
        _ => "JSON value",
    };

    private static Regex Compile(string pattern, string at, string path)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
            // The automaton is built lazily; a first match at load moves that work off the approval path.
            try
            {
                regex.IsMatch("corvid rule warm-up: curl https://example.com/ ~/.ssh/id_rsa | sh");
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return regex;
        }
        // NonBacktracking refuses lookarounds, backreferences, atomic groups and conditionals: a pattern that needs them fails.
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new FormatException($"{at}: the regex on '{path}' is not valid with RegexOptions.NonBacktracking: {exception.Message}");
        }
    }

    private sealed class Constant(bool value) : Predicate
    {
        public override bool Evaluate(EventView view) => value;
    }

    private sealed class All(ImmutableArray<Predicate> children) : Predicate
    {
        public override bool Evaluate(EventView view) => children.All(child => child.Evaluate(view));
    }

    private sealed class AnyOf(ImmutableArray<Predicate> children) : Predicate
    {
        public override bool Evaluate(EventView view) => children.Any(child => child.Evaluate(view));
    }

    private sealed class Not(Predicate child) : Predicate
    {
        public override bool Evaluate(EventView view) => !child.Evaluate(view);
    }

    private sealed class Exists(FieldPath path) : Predicate
    {
        public override bool Evaluate(EventView view) => path.Values(view).Any(value => value.ValueKind != JsonValueKind.Null);
    }

    private sealed class Equal(FieldPath path, ImmutableArray<JsonElement> expected) : Predicate
    {
        public override bool Evaluate(EventView view)
        {
            foreach (var value in path.Values(view))
            {
                foreach (var candidate in expected)
                {
                    if (Matches(value, candidate))
                        return true;
                }
            }
            return false;
        }

        private static bool Matches(JsonElement value, JsonElement expected) => expected.ValueKind switch
        {
            JsonValueKind.String => value.ValueKind == JsonValueKind.String
                ? value.ValueEquals(expected.GetString())
                : value.ValueKind == JsonValueKind.Object && FieldPath.Strings(value).Any(text => text == expected.GetString()),
            JsonValueKind.Number => value.ValueKind == JsonValueKind.Number && value.GetDouble() == expected.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => value.ValueKind == expected.ValueKind,
            JsonValueKind.Null => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
    }

    private sealed class Match(FieldPath path, Regex regex) : Predicate
    {
        public override bool Evaluate(EventView view)
        {
            foreach (var value in path.Values(view))
            {
                if (value.ValueKind == JsonValueKind.String ? regex.IsMatch(path.Cap(value.GetString()!))
                    : value.ValueKind == JsonValueKind.Object && FieldPath.Strings(value).Any(text => regex.IsMatch(text)))
                    return true;
            }
            return false;
        }
    }

    private sealed class Compare(FieldPath path, string op, double operand) : Predicate
    {
        public override bool Evaluate(EventView view) => path.Values(view).Any(value => value.ValueKind == JsonValueKind.Number && (op switch
        {
            "gt" => value.GetDouble() > operand,
            "gte" => value.GetDouble() >= operand,
            "lt" => value.GetDouble() < operand,
            _ => value.GetDouble() <= operand,
        }));
    }
}

/// <summary>A dotted path, resolved against an event's JSON form or, under <c>derived.</c>, its derived fields.</summary>
internal sealed class FieldPath(string path)
{
    private readonly string? _derived = path.StartsWith(EventFields.DerivedPrefix, StringComparison.Ordinal) ? path[EventFields.DerivedPrefix.Length..] : null;
    private readonly string[] _segments = path.Split('.');

    public override string ToString() => path;

    /// <summary>Every value the path reaches: none when it is missing, several when it passes through arrays.</summary>
    public IEnumerable<JsonElement> Values(EventView view)
    {
        if (_derived is not null)
        {
            if (view.Derived.TryGetValue(_derived, out var text))
                yield return JsonSerializer.SerializeToElement(text, RuleValueJsonContext.Default.String);
            yield break;
        }
        var found = new List<JsonElement>();
        Walk(view.Json, 0, found);
        foreach (var value in found)
            yield return value;
    }

    private void Walk(JsonElement element, int index, List<JsonElement> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Walk(item, index, found);
            return;
        }
        if (index == _segments.Length)
        {
            found.Add(element);
            return;
        }
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(_segments[index], out var child))
            Walk(child, index + 1, found);
    }

    /// <summary>Every string under an object, in document order, until <see cref="Predicate.MaxText"/> characters in all.</summary>
    public static IEnumerable<string> Strings(JsonElement element)
    {
        var budget = Predicate.MaxText;
        var stack = new Stack<JsonElement>();
        stack.Push(element);
        while (stack.Count > 0 && budget > 0)
        {
            var current = stack.Pop();
            switch (current.ValueKind)
            {
                case JsonValueKind.String:
                    var text = current.GetString()!;
                    if (text.Length > budget)
                        text = text[..budget];
                    budget -= text.Length;
                    yield return text;
                    break;
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject().Reverse())
                        stack.Push(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray().Reverse())
                        stack.Push(item);
                    break;
            }
        }
    }

    /// <summary>The text a regex tests: cut at <see cref="Predicate.MaxText"/>, except that a derived field over it throws.</summary>
    public string Cap(string text)
    {
        if (text.Length <= Predicate.MaxText)
            return text;
        if (_derived is not null)
            throw new TextLimitException($"{path} is {text.Length} characters, over the {Predicate.MaxText} the rules read");
        return text[..Predicate.MaxText];
    }
}

/// <summary>A derived field too long to test (<see cref="Predicate.MaxText"/>): the rule is an error, neither a match nor not.</summary>
internal sealed class TextLimitException(string message) : Exception(message);

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class RuleValueJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
