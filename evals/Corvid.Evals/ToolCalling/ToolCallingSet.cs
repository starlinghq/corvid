using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Corvid.Evals.ToolCalling;

/// <summary>One tool of <c>evals/tool-calling/catalogue.json</c>: a built-in snapshot or an MCP-like extra.</summary>
/// <param name="Origin"><c>builtin</c>, or <c>mcp:&lt;server&gt;</c> for an extra named <c>&lt;server&gt;__&lt;tool&gt;</c>.</param>
/// <param name="Parameters">The JSON Schema of the arguments, as the generator is shown it.</param>
/// <param name="Examples">Shortlist example requests; built-ins only, as in production (MCP tools have none, C-018).</param>
/// <param name="Source">Where an extra came from, for example <c>s5:create_reminder</c>.</param>
public sealed record CatalogueEntry(string Name, string Origin, string Description, JsonElement Parameters, ImmutableArray<string> Examples,
    string? Source)
{
    public const string BuiltinOrigin = "builtin";

    public bool IsBuiltin => Origin == BuiltinOrigin;

    /// <summary>Declared parameters in schema order.</summary>
    public ImmutableArray<string> ParameterNames =>
        Parameters.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(property => property.Name)]
            : [];

    public bool Declares(string parameter) => Schema(parameter) is not null;

    /// <summary>The schema of one parameter, or null when the tool does not declare it.</summary>
    public JsonElement? Schema(string parameter) =>
        Parameters.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(parameter, out var schema) ? schema : null;

    public bool IsRequired(string parameter) =>
        Parameters.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
        && required.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == parameter);
}

/// <summary>What a step expects: one tool, one of several (genuine ambiguity), or no call at all.</summary>
public sealed record Expectation(ImmutableArray<string> Tools, bool None)
{
    public static Expectation NoTool { get; } = new([], true);

    public bool Accepts(string tool) => Tools.Contains(tool, StringComparer.Ordinal);

    public override string ToString() => None ? "none" : Tools.Length == 1 ? Tools[0] : "any_of(" + string.Join(", ", Tools) + ")";
}

public enum ConstraintKind
{
    Equals,
    Contains,
    Regex,
    Enum,
    Present,
}

/// <summary>
/// One argument constraint. Strings compare trimmed and ignoring case (S5's labelled-argument rule); numbers by value; a
/// regex is .NET syntax, case-insensitive, against the value's text (a string's own text, anything else's JSON).
/// </summary>
public sealed record ArgumentConstraint(ConstraintKind Kind, ImmutableArray<JsonElement> Values, string? Pattern = null)
{
    private Regex? _regex;

    public bool Holds(JsonElement? value)
    {
        if (value is not { } actual || actual.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;
        return Kind switch
        {
            ConstraintKind.Present => true,
            ConstraintKind.Equals or ConstraintKind.Enum => Values.Any(expected => Same(expected, actual)),
            ConstraintKind.Contains => Values.All(part => Text(actual).Contains(part.GetString()!, StringComparison.OrdinalIgnoreCase)),
            ConstraintKind.Regex => (_regex ??= Compile(Pattern!)).IsMatch(Text(actual)),
            _ => false,
        };
    }

    public override string ToString() => Kind switch
    {
        ConstraintKind.Present => "present",
        ConstraintKind.Regex => $"regex {Pattern}",
        ConstraintKind.Equals => $"equals {Values[0].GetRawText()}",
        ConstraintKind.Contains => "contains " + string.Join(", ", Values.Select(value => value.GetRawText())),
        _ => "enum " + string.Join(", ", Values.Select(value => value.GetRawText())),
    };

    internal static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The value's text: a string as it is, anything else as JSON.</summary>
    public static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>Equality under the case's rules: trimmed and case-insensitive text, or numeric value when both are numbers.</summary>
    public static bool Same(JsonElement expected, JsonElement actual)
    {
        if (TryNumber(expected, out var left) && TryNumber(actual, out var right))
            return left == right;
        return string.Equals(Text(expected).Trim(), Text(actual).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNumber(JsonElement value, out decimal number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number);
    }
}

/// <summary>One expected call: the tool and the constraints on its arguments.</summary>
public sealed record ExpectedStep(Expectation Expect, ImmutableSortedDictionary<string, ArgumentConstraint> Args);

/// <summary>One golden case. A single-step case has one step; a multi-step case expects its steps' calls in order.</summary>
public sealed record ToolCallingCase(
    string Id,
    string Prompt,
    ImmutableArray<string> Tags,
    ImmutableArray<ExpectedStep> Steps,
    bool MultiStep,
    ImmutableSortedDictionary<string, string> Results,
    string? Note)
{
    public bool ExpectsNoTool => !MultiStep && Steps[0].Expect.None;

    public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.Ordinal);

    /// <summary>Every tool some step accepts.</summary>
    public IEnumerable<string> ExpectedTools => Steps.SelectMany(step => step.Expect.Tools).Distinct(StringComparer.Ordinal);
}

/// <summary>
/// The S2 tool-calling golden set (C-034): <c>catalogue.json</c> and <c>cases.jsonl</c>, parsed and validated strictly. The
/// format is described by <c>case.schema.json</c>; this loader enforces it by hand (no schema package) plus what a schema
/// cannot say: unknown fields are refused, expected tools must be in the catalogue, a constrained parameter must be declared
/// by an expected tool, <c>equals</c> and <c>enum</c> values must lie inside a declared enum, ids are unique.
/// </summary>
public sealed class ToolCallingSet
{
    public static readonly ImmutableArray<string> KnownTags = ["direct", "near_miss", "no_tool", "required", "tempting", "multi_step", "s5"];

    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex ServerTool = new("^([A-Za-z0-9_-]+?)__[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    private ToolCallingSet(string version, ImmutableArray<CatalogueEntry> catalogue, ImmutableArray<ToolCallingCase> cases,
        string catalogueSha256, string casesSha256)
    {
        Version = version;
        Catalogue = catalogue;
        Cases = cases;
        CatalogueSha256 = catalogueSha256;
        CasesSha256 = casesSha256;
        Tools = catalogue.ToImmutableDictionary(entry => entry.Name, StringComparer.Ordinal);
    }

    /// <summary>The set's version (catalogue.json's <c>version</c>), recorded in every report.</summary>
    public string Version { get; }

    public ImmutableArray<CatalogueEntry> Catalogue { get; }

    public ImmutableDictionary<string, CatalogueEntry> Tools { get; }

    public ImmutableArray<ToolCallingCase> Cases { get; }

    public string CatalogueSha256 { get; }

    public string CasesSha256 { get; }

    /// <summary>The committed set, next to the test or tool binary (<c>tool-calling/</c>) or at the given paths.</summary>
    public static ToolCallingSet Load(string cataloguePath, string casesPath)
    {
        ArgumentNullException.ThrowIfNull(cataloguePath);
        ArgumentNullException.ThrowIfNull(casesPath);
        return Parse(File.ReadAllText(cataloguePath), File.ReadAllText(casesPath));
    }

    /// <exception cref="InvalidDataException">The catalogue or a case breaks the format; the message names the line and case.</exception>
    public static ToolCallingSet Parse(string catalogueJson, string casesJsonl)
    {
        ArgumentNullException.ThrowIfNull(catalogueJson);
        ArgumentNullException.ThrowIfNull(casesJsonl);
        var (version, catalogue) = ParseCatalogue(catalogueJson);
        var tools = catalogue.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var cases = ImmutableArray.CreateBuilder<ToolCallingCase>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lines = casesJsonl.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            string? id = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = ParseCase(document.RootElement, tools, out id);
                if (!ids.Add(item.Id))
                    throw new InvalidDataException($"duplicate id '{item.Id}'");
                cases.Add(item);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                throw new InvalidDataException($"cases.jsonl line {i + 1}{(id is null ? "" : $" ({id})")}: {exception.Message}", exception);
            }
        }
        if (cases.Count == 0)
            throw new InvalidDataException("cases.jsonl has no cases");
        return new ToolCallingSet(version, catalogue, cases.ToImmutable(), Sha256(catalogueJson), Sha256(casesJsonl));
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---- catalogue ----

    private static (string Version, ImmutableArray<CatalogueEntry> Tools) ParseCatalogue(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Fields(root, "catalogue", ["version", "description", "tools"], ["version", "tools"]);
            var version = String(root, "version", "catalogue");
            var tools = ImmutableArray.CreateBuilder<CatalogueEntry>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.GetProperty("tools").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("catalogue: tools must be an array");
            foreach (var tool in root.GetProperty("tools").EnumerateArray())
            {
                var entry = ParseTool(tool);
                if (!names.Add(entry.Name))
                    throw new InvalidDataException($"catalogue: duplicate tool '{entry.Name}'");
                tools.Add(entry);
            }
            if (tools.Count == 0)
                throw new InvalidDataException("catalogue: no tools");
            return (version, tools.ToImmutable());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("catalogue: " + exception.Message, exception);
        }
    }

    private static CatalogueEntry ParseTool(JsonElement tool)
    {
        var name = tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "?";
        var where = $"catalogue tool '{name}'";
        Fields(tool, where, ["name", "origin", "description", "parameters", "examples", "source"], ["name", "origin", "description", "parameters"]);
        var origin = String(tool, "origin", where);
        if (origin != CatalogueEntry.BuiltinOrigin)
        {
            // As Corvid.Mcp registers them: server__tool, with the descriptor's Origin "mcp:<server>".
            var match = ServerTool.Match(name);
            if (!origin.StartsWith("mcp:", StringComparison.Ordinal) || !match.Success || match.Groups[1].Value != origin["mcp:".Length..])
                throw new InvalidDataException($"{where}: origin must be 'builtin' or 'mcp:<server>' for a tool named <server>__<tool>");
        }
        var parameters = tool.GetProperty("parameters");
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("type", out var type) || type.GetString() != "object")
            throw new InvalidDataException($"{where}: parameters must be an object schema");
        var examples = ImmutableArray<string>.Empty;
        if (tool.TryGetProperty("examples", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new InvalidDataException($"{where}: examples must be strings");
            examples = [.. list.EnumerateArray().Select(item => item.GetString()!)];
        }
        if (origin != CatalogueEntry.BuiltinOrigin && !examples.IsEmpty)
            throw new InvalidDataException($"{where}: an MCP-like tool has no examples, as in production");
        string? source = tool.TryGetProperty("source", out _) ? String(tool, "source", where) : null;
        return new CatalogueEntry(name, origin, String(tool, "description", where), parameters.Clone(), examples, source);
    }

    // ---- cases ----

    private static ToolCallingCase ParseCase(JsonElement item, Dictionary<string, CatalogueEntry> tools, out string? id)
    {
        id = null;
        Fields(item, "case", ["id", "prompt", "tags", "expect", "args", "steps", "results", "note"], ["id", "prompt", "tags"]);
        id = String(item, "id", "case");
        if (!IdPattern.IsMatch(id))
            throw new InvalidDataException($"id '{id}' must be lower-case words joined by '-'");
        var prompt = String(item, "prompt", "case");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidDataException("prompt is empty");

        var tagsElement = item.GetProperty("tags");
        if (tagsElement.ValueKind != JsonValueKind.Array || tagsElement.GetArrayLength() == 0)
            throw new InvalidDataException("tags must be a non-empty array");
        var tags = ImmutableArray.CreateBuilder<string>();
        foreach (var tag in tagsElement.EnumerateArray())
        {
            var value = tag.ValueKind == JsonValueKind.String ? tag.GetString()! : throw new InvalidDataException("a tag must be a string");
            if (!KnownTags.Contains(value, StringComparer.Ordinal))
                throw new InvalidDataException($"unknown tag '{value}'; known: {string.Join(", ", KnownTags)}");
            if (tags.Contains(value, StringComparer.Ordinal))
                throw new InvalidDataException($"tag '{value}' is repeated");
            tags.Add(value);
        }

        var hasSteps = item.TryGetProperty("steps", out var stepsElement);
        var hasExpect = item.TryGetProperty("expect", out var expectElement);
        ImmutableArray<ExpectedStep> steps;
        if (hasSteps)
        {
            if (hasExpect || item.TryGetProperty("args", out _))
                throw new InvalidDataException("a multi-step case has steps, and no top-level expect or args");
            if (stepsElement.ValueKind != JsonValueKind.Array || stepsElement.GetArrayLength() < 2)
                throw new InvalidDataException("steps must be an array of at least two steps");
            var builder = ImmutableArray.CreateBuilder<ExpectedStep>();
            var n = 0;
            foreach (var step in stepsElement.EnumerateArray())
            {
                n++;
                Fields(step, $"step {n}", ["expect", "args"], ["expect"]);
                var expect = ParseExpect(step.GetProperty("expect"), tools, $"step {n}");
                if (expect.None)
                    throw new InvalidDataException($"step {n}: a step expects a call, not none");
                builder.Add(new ExpectedStep(expect, ParseArgs(step, expect, tools, $"step {n}")));
            }
            steps = builder.ToImmutable();
            if (!tags.Contains("multi_step"))
                throw new InvalidDataException("a case with steps is tagged multi_step");
        }
        else
        {
            if (!hasExpect)
                throw new InvalidDataException("a case needs expect or steps");
            var expect = ParseExpect(expectElement, tools, "expect");
            steps = [new ExpectedStep(expect, ParseArgs(item, expect, tools, "args"))];
            if (tags.Contains("multi_step"))
                throw new InvalidDataException("a multi_step case has steps");
            if (expect.None != tags.Contains("no_tool"))
                throw new InvalidDataException("expect none and the no_tool tag go together");
        }

        var results = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("results", out var resultsElement))
        {
            if (resultsElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("results must be an object of tool name to text");
            foreach (var result in resultsElement.EnumerateObject())
            {
                if (!tools.ContainsKey(result.Name))
                    throw new InvalidDataException($"results: '{result.Name}' is not in the catalogue");
                if (result.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"results: '{result.Name}' must be text");
                results[result.Name] = result.Value.GetString()!;
            }
        }
        string? note = item.TryGetProperty("note", out _) ? String(item, "note", "case") : null;
        return new ToolCallingCase(id, prompt, tags.ToImmutable(), steps, hasSteps, results.ToImmutable(), note);
    }

    private static Expectation ParseExpect(JsonElement expect, Dictionary<string, CatalogueEntry> tools, string where)
    {
        if (expect.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where}: expect must be an object");
        var properties = expect.EnumerateObject().ToList();
        if (properties.Count != 1)
            throw new InvalidDataException($"{where}: expect has exactly one of tool, any_of or none");
        var property = properties[0];
        switch (property.Name)
        {
            case "tool":
                return new Expectation([Known(property.Value, tools, where)], false);
            case "any_of":
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() < 2)
                    throw new InvalidDataException($"{where}: any_of lists at least two tools");
                var names = property.Value.EnumerateArray().Select(name => Known(name, tools, where)).ToImmutableArray();
                if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    throw new InvalidDataException($"{where}: any_of repeats a tool");
                return new Expectation(names, false);
            case "none":
                if (property.Value.ValueKind != JsonValueKind.True)
                    throw new InvalidDataException($"{where}: none must be true");
                return Expectation.NoTool;
            default:
                throw new InvalidDataException($"{where}: unknown field '{property.Name}' in expect");
        }
    }

    private static string Known(JsonElement name, Dictionary<string, CatalogueEntry> tools, string where)
    {
        var value = name.ValueKind == JsonValueKind.String ? name.GetString()! : throw new InvalidDataException($"{where}: a tool name must be a string");
        return tools.ContainsKey(value) ? value : throw new InvalidDataException($"{where}: '{value}' is not in the catalogue");
    }

    private static ImmutableSortedDictionary<string, ArgumentConstraint> ParseArgs(JsonElement owner, Expectation expect,
        Dictionary<string, CatalogueEntry> tools, string where)
    {
        var args = ImmutableSortedDictionary.CreateBuilder<string, ArgumentConstraint>(StringComparer.Ordinal);
        if (!owner.TryGetProperty("args", out var element))
            return args.ToImmutable();
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where}: args must be an object");
        if (expect.None)
            throw new InvalidDataException($"{where}: a no-tool case constrains no arguments");
        foreach (var parameter in element.EnumerateObject())
        {
            var declaring = expect.Tools.Select(tool => tools[tool]).Where(tool => tool.Declares(parameter.Name)).ToList();
            if (declaring.Count == 0)
                throw new InvalidDataException($"{where}: no expected tool declares '{parameter.Name}'");
            args[parameter.Name] = ParseConstraint(parameter.Value, declaring, parameter.Name, where);
        }
        return args.ToImmutable();
    }

    private static ArgumentConstraint ParseConstraint(JsonElement constraint, List<CatalogueEntry> declaring, string parameter, string where)
    {
        where = $"{where}: '{parameter}'";
        if (constraint.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} needs a constraint object");
        var properties = constraint.EnumerateObject().ToList();
        if (properties.Count != 1)
            throw new InvalidDataException($"{where} has exactly one of equals, contains, regex, enum or present");
        var (kind, value) = (properties[0].Name, properties[0].Value);
        switch (kind)
        {
            case "equals":
                Scalar(value, where);
                InsideEnums(value, declaring, parameter, where);
                return new ArgumentConstraint(ConstraintKind.Equals, [value.Clone()]);
            case "enum":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                    throw new InvalidDataException($"{where}: enum lists at least one value");
                foreach (var option in value.EnumerateArray())
                {
                    Scalar(option, where);
                    InsideEnums(option, declaring, parameter, where);
                }
                return new ArgumentConstraint(ConstraintKind.Enum, [.. value.EnumerateArray().Select(option => option.Clone())]);
            case "contains":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
                    || value.EnumerateArray().Any(part => part.ValueKind != JsonValueKind.String || part.GetString()!.Length == 0))
                    throw new InvalidDataException($"{where}: contains lists non-empty strings");
                return new ArgumentConstraint(ConstraintKind.Contains, [.. value.EnumerateArray().Select(part => part.Clone())]);
            case "regex":
                var pattern = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"{where}: regex must be a string");
                try
                {
                    ArgumentConstraint.Compile(pattern);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException($"{where}: bad regex: {exception.Message}");
                }
                return new ArgumentConstraint(ConstraintKind.Regex, [], pattern);
            case "present":
                if (value.ValueKind != JsonValueKind.True)
                    throw new InvalidDataException($"{where}: present must be true");
                return new ArgumentConstraint(ConstraintKind.Present, []);
            default:
                throw new InvalidDataException($"{where}: unknown constraint '{kind}'");
        }
    }

    private static void Scalar(JsonElement value, string where)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{where}: values are strings, numbers or booleans");
    }

    // An equals or enum value must be one the tool could accept: inside the parameter's enum when it declares one.
    private static void InsideEnums(JsonElement value, List<CatalogueEntry> declaring, string parameter, string where)
    {
        foreach (var tool in declaring)
        {
            var schema = tool.Schema(parameter)!.Value;
            if (schema.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array
                && !options.EnumerateArray().Any(option => ArgumentConstraint.Same(option, value)))
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"{where}: {value.GetRawText()} is outside {tool.Name}'s enum {options.GetRawText()}"));
        }
    }

    private static void Fields(JsonElement element, string where, string[] known, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} must be an object");
        foreach (var property in element.EnumerateObject())
            if (!known.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"{where}: unknown field '{property.Name}'");
        foreach (var name in required)
            if (!element.TryGetProperty(name, out _))
                throw new InvalidDataException($"{where}: missing '{name}'");
    }

    private static string String(JsonElement element, string name, string where) =>
        element.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new InvalidDataException($"{where}: '{name}' must be a string");
}
