using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Tools.Fixtures;
using Microsoft.Extensions.AI;

namespace Corvid.Tools.Tests;

/// <summary>
/// The generator writes the schema itself because the compiler cannot run AIJsonUtilities. These tests compare the two
/// for every fixture tool, so a drift from what Microsoft.Extensions.AI would produce at runtime is caught.
/// </summary>
public sealed class SchemaParityTests
{
    public static TheoryData<string> Tools() =>
        [.. All().Select(t => t.Name)];

    private static IEnumerable<ToolFunction> All() =>
        new NoteTools().CreateTools().Concat(ApiTools.CreateTools()).Concat(new SessionTools().CreateTools());

    [Theory]
    [MemberData(nameof(Tools))]
    public void Generated_schema_matches_AIJsonUtilities(string name)
    {
        ToolFunction tool = All().Single(t => t.Name == name);
        MethodInfo method = typeof(NoteTools).GetMethods().Concat(typeof(ApiTools).GetMethods()).Concat(typeof(SessionTools).GetMethods())
            .Single(m => m.GetCustomAttribute<ToolAttribute>() is { } attribute && (attribute.Name ?? Snake(m.Name)) == name);

        // The call context comes from the turn, not the model, so neither schema has it.
        JsonElement expected = AIJsonUtilities.CreateFunctionJsonSchema(method, serializerOptions: Options(),
            inferenceOptions: new AIJsonSchemaCreateOptions { IncludeParameter = parameter => parameter.ParameterType != typeof(ToolCallContext) });

        string[] secrets = [.. tool.Descriptor.Parameters.Where(p => p.IsSecret).Select(p => p.Name)];
        Assert.Equal(Normalize(expected, secrets, runtime: true), Normalize(tool.JsonSchema, secrets, runtime: false));
    }

    private static JsonSerializerOptions Options()
    {
        // Enums travel as their names, which is how the generated binder reads them.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static string Snake(string name) => name switch
    {
        "ListNotes" => "list_notes",
        "ClearNotesAsync" => "clear_notes",
        "WhoAmI" => "whoami",
        "SessionOrNone" => "session_or_none",
        _ => name,
    };

    // Root-level title/description and property order are presentation, not contract. Two differences are deliberate:
    // SecretRef is a string (an enum of catalogue names at runtime) where System.Text.Json sees an object, and array
    // elements are non-null because the binder refuses null elements, where System.Text.Json assumes they may be null.
    private static string Normalize(JsonElement schema, string[] secrets, bool runtime)
    {
        var node = (JsonObject)JsonNode.Parse(schema.GetRawText())!;
        node.Remove("title");
        node.Remove("description");
        var properties = (JsonObject)node["properties"]!;
        foreach (string secret in secrets)
        {
            properties.Remove(secret);
        }

        if (runtime)
        {
            foreach (JsonObject items in properties.Select(p => p.Value?["items"]).OfType<JsonObject>())
            {
                if (items["type"] is JsonArray { Count: 2 } types && types[1]?.GetValue<string>() == "null")
                {
                    items["type"] = types[0]!.GetValue<string>();
                }
            }
        }

        return Sort(node)!.ToJsonString();
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sort)]),
        _ => node?.DeepClone(),
    };
}
