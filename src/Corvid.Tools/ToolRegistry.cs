using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Corvid.Tools;

/// <summary>The tools Corvid can offer, keyed by name, with their risk metadata.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolFunction> _tools = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Add(ToolFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_gate)
        {
            // Names are what the model calls, so a second tool with the same name would be ambiguous.
            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException($"A tool named '{tool.Name}' is already registered.");
            }
        }
    }

    public void Add(IToolProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        AddRange(provider.CreateTools());
    }

    public void AddRange(IEnumerable<ToolFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (ToolFunction tool in tools)
        {
            Add(tool);
        }
    }

    /// <summary>Every registered tool's metadata, ordered by name.</summary>
    public IReadOnlyList<ToolDescriptor> List()
    {
        lock (_gate)
        {
            return [.. _tools.Values.Select(t => t.Descriptor).OrderBy(d => d.Name, StringComparer.Ordinal)];
        }
    }

    public bool TryGet(string name, [NotNullWhen(true)] out ToolFunction? tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out tool);
        }
    }

    /// <summary>
    /// The tools to offer a model, ordered by name. A tool with <see cref="Corvid.Vault.Client.SecretRef"/> parameters is
    /// offered with each such parameter's schema restricted to <paramref name="secretNames"/>, and a call naming any other
    /// secret is refused before the method runs. With no catalogue (Vault absent) or an empty one, those tools are left
    /// out (§10.5).
    /// </summary>
    public IReadOnlyList<AIFunction> GetFunctions(IReadOnlyCollection<string>? secretNames = null)
    {
        string[] names = secretNames is null ? [] : [.. secretNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var functions = new List<AIFunction>();
        lock (_gate)
        {
            foreach (ToolFunction tool in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (!tool.Descriptor.HasSecretParameters)
                {
                    functions.Add(tool);
                }
                else if (names.Length > 0)
                {
                    functions.Add(new SecretCatalogFunction(tool, names));
                }
            }
        }

        return functions;
    }

    /// <summary>A tool whose secret parameters are limited to the current Vault catalogue.</summary>
    private sealed class SecretCatalogFunction : DelegatingAIFunction
    {
        private readonly HashSet<string> _names;
        private readonly string[] _secretParameters;
        private readonly string _allowed;

        public SecretCatalogFunction(ToolFunction inner, string[] names)
            : base(inner)
        {
            _names = new HashSet<string>(names, StringComparer.Ordinal);
            _secretParameters = [.. inner.Descriptor.Parameters.Where(p => p.IsSecret).Select(p => p.Name)];
            _allowed = string.Join(", ", names);

            var schema = (JsonObject)JsonNode.Parse(inner.JsonSchema.GetRawText())!;
            var properties = (JsonObject)schema["properties"]!;
            foreach (string parameter in _secretParameters)
            {
                var property = (JsonObject)properties[parameter]!;
                var values = new JsonArray([.. names.Select(n => (JsonNode?)JsonValue.Create(n))]);
                // A nullable SecretRef keeps null as an allowed value.
                if (property["type"] is JsonArray)
                {
                    values.Add(null);
                }

                property["enum"] = values;
            }

            using JsonDocument document = JsonDocument.Parse(schema.ToJsonString());
            JsonSchema = document.RootElement.Clone();
        }

        public override JsonElement JsonSchema { get; }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            foreach (string parameter in _secretParameters)
            {
                if (ToolArguments.TryGet(arguments, parameter, out object? value))
                {
                    string name = ToolArguments.ReadSecretRef(value, parameter).Name;
                    if (!_names.Contains(name))
                    {
                        throw ToolArguments.NotAllowed(parameter, name, _allowed);
                    }
                }
            }

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
