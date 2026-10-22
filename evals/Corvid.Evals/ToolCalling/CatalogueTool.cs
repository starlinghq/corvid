using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Evals.ToolCalling;

/// <summary>
/// One catalogue tool as the turn loop sees it: a <see cref="ToolFunction"/> over a descriptor built from catalogue.json.
/// Every tool is read-only and offline (no side effect, no network), so no approval, checkpoint, sandbox or taint runs: the
/// eval measures the choice and the arguments, not the effect. An extra's <see cref="ToolDescriptor.Origin"/> is
/// <c>mcp:&lt;server&gt;</c>, as Corvid.Mcp sets it, and its description is trusted (not <c>OutsideDescription</c>).
/// Invoking records the call and returns the running case's canned result for the tool, or <c>{"ok":true}</c>.
/// </summary>
public sealed class CatalogueTool : ToolFunction
{
    public const string DefaultResult = """{"ok":true}""";

    private readonly CannedResults _results;

    public CatalogueTool(CatalogueEntry entry, CannedResults results, bool mcpOrigin = true)
        : base(Describe(entry, mcpOrigin))
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = results;
    }

    /// <summary>The descriptor the generator and the shortlist see: the snapshot's name, description and schema.</summary>
    /// <param name="mcpOrigin">False drops an extra's <c>mcp:</c> origin (the <c>shortlist-kev-no-desc</c> ablation).</param>
    public static ToolDescriptor Describe(CatalogueEntry entry, bool mcpOrigin = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var parameters = entry.ParameterNames.Select(name =>
        {
            var schema = entry.Schema(name)!.Value;
            var description = schema.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            return new ToolParameter(name, description, entry.IsRequired(name), IsSecret: false);
        }).ToImmutableArray();
        return new ToolDescriptor(entry.Name, entry.Description, entry.Parameters.GetRawText(),
            new ToolRisk(SideEffects.None, Network: false, Destructive: false), parameters)
        {
            Origin = entry.IsBuiltin || !mcpOrigin ? null : entry.Origin,
        };
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var text = _results.Invoked(Name);
        // A canned result that is JSON goes back as JSON, as a real tool's JsonNode would; anything else as text.
        try
        {
            using var document = JsonDocument.Parse(text);
            return ValueTask.FromResult<object?>(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return ValueTask.FromResult<object?>(text);
        }
    }

    /// <summary>A tool for every catalogue entry, sharing one <see cref="CannedResults"/>.</summary>
    public static ToolRegistry Registry(ToolCallingSet set, CannedResults results, bool mcpOrigins = true)
    {
        ArgumentNullException.ThrowIfNull(set);
        var registry = new ToolRegistry();
        foreach (var entry in set.Catalogue)
            registry.Add(new CatalogueTool(entry, results, mcpOrigins));
        return registry;
    }
}

/// <summary>The running case's canned results, and how often each tool was invoked. Cases run one at a time.</summary>
public sealed class CannedResults
{
    private readonly Lock _gate = new();
    private ImmutableSortedDictionary<string, string> _current = ImmutableSortedDictionary<string, string>.Empty;
    private readonly Dictionary<string, int> _invocations = new(StringComparer.Ordinal);

    public void Begin(ToolCallingCase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _current = item.Results;
            _invocations.Clear();
        }
    }

    public string Invoked(string tool)
    {
        lock (_gate)
        {
            _invocations[tool] = _invocations.GetValueOrDefault(tool) + 1;
            return _current.GetValueOrDefault(tool, CatalogueTool.DefaultResult);
        }
    }

    public int Invocations(string tool)
    {
        lock (_gate)
            return _invocations.GetValueOrDefault(tool);
    }
}
