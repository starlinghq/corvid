using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Tools.Fixtures;
using Corvid.Vault.Client;
using Microsoft.Extensions.AI;

namespace Corvid.Tools.Tests;

public sealed class RuntimeTests
{
    private static ToolRegistry Registry(NoteTools? notes = null)
    {
        var registry = new ToolRegistry();
        registry.Add(notes ?? new NoteTools());
        registry.AddRange(ApiTools.CreateTools());
        return registry;
    }

    /// <summary>A6 (G-4): a registry over another shadows by name, hides what it is told to, and sees later changes below.</summary>
    [Fact]
    public async Task A_registry_over_another_shadows_hides_and_follows_it()
    {
        var below = new ToolRegistry();
        var shared = new NoteTools();
        below.Add(shared);
        var own = new NoteTools();
        var over = new ToolRegistry(below, hidden: ["clear_notes"]);
        over.AddRange(own.CreateTools().Where(tool => tool.Name == "add_note"));

        await Tool(over, "add_note").InvokeAsync(Json("""{"text":"mine"}"""), TestContext.Current.CancellationToken);
        await Tool(over, "list_notes").InvokeAsync(Json("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(["mine"], own.Notes.Select(note => note.Text));
        Assert.Empty(shared.Notes);
        Assert.False(over.TryGet("clear_notes", out _));
        Assert.Equal(["add_note", "list_notes"], over.List().Select(tool => tool.Name));
        Assert.Equal(["add_note", "list_notes"], over.GetFunctions().Select(tool => tool.Name));
        // A tool added below later (an MCP server's) shows above; removing above takes only its own.
        below.AddRange(ApiTools.CreateTools().Where(tool => tool.Name == "ping"));
        Assert.Contains("ping", over.List().Select(tool => tool.Name));
        Assert.False(over.Remove("ping"));
        Assert.True(over.Remove("add_note"));
        Assert.True(over.TryGet("add_note", out var fallback) && below.TryGet("add_note", out var original) && ReferenceEquals(fallback, original));
    }

    /// <summary>Arguments as a tool-call parser produces them: each value a <see cref="JsonElement"/>.</summary>
    private static AIFunctionArguments Json(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => (object?)p.Value));

    private static AIFunction Tool(ToolRegistry registry, string name) =>
        registry.TryGet(name, out ToolFunction? tool) ? tool : throw new InvalidOperationException(name);

    [Fact]
    public void Registry_lists_tools_by_name_with_risk()
    {
        IReadOnlyList<ToolDescriptor> tools = Registry().List();

        Assert.Equal(["add_note", "call_api", "clear_notes", "describe_values", "list_notes", "ping"], tools.Select(t => t.Name));
        ToolDescriptor clear = tools.Single(t => t.Name == "clear_notes");
        Assert.Equal(new ToolRisk(SideEffects.Memory, Network: false, Destructive: true), clear.Risk);
        Assert.False(clear.Risk.ReadOnly);
        ToolDescriptor call = tools.Single(t => t.Name == "call_api");
        Assert.Equal(new ToolRisk(SideEffects.Remote | SideEffects.Messaging, Network: true, Destructive: false), call.Risk);
        Assert.True(tools.Single(t => t.Name == "list_notes").Risk.ReadOnly);
        Assert.Equal("List saved notes, oldest first.", tools.Single(t => t.Name == "list_notes").Description);
    }

    [Fact]
    public void Risk_is_also_visible_to_code_that_sees_only_an_AITool()
    {
        AITool tool = Tool(Registry(), "clear_notes");

        Assert.Equal(SideEffects.Memory, tool.AdditionalProperties[ToolFunction.SideEffectsKey]);
        Assert.Equal(true, tool.AdditionalProperties[ToolFunction.DestructiveKey]);
        Assert.Equal(false, tool.AdditionalProperties[ToolFunction.NetworkKey]);
        Assert.Equal("clear_notes", tool.GetService<ToolDescriptor>()?.Name);
        Assert.Same(tool, tool.GetService<object>());
    }

    [Fact]
    public void Schema_is_the_compile_time_text()
    {
        AIFunction tool = Tool(Registry(), "add_note");

        Assert.Equal(
            """{"type":"object","properties":{"text":{"description":"The note text.","type":"string"},"priority":{"description":"How urgent the note is.","type":"string","enum":["Low","Normal","High"],"default":"Normal"},"tags":{"type":["array","null"],"items":{"type":"string"},"default":null}},"required":["text"]}""",
            tool.JsonSchema.GetRawText());
    }

    [Fact]
    public void Duplicate_names_are_refused()
    {
        ToolRegistry registry = Registry();

        Assert.Throws<InvalidOperationException>(() => registry.Add(new NoteTools()));
    }

    [Fact]
    public async Task Invokes_instance_tool_with_json_arguments_and_defaults()
    {
        var notes = new NoteTools();
        ToolRegistry registry = Registry(notes);

        object? first = await Tool(registry, "add_note").InvokeAsync(Json("""{"text":"buy milk","tags":["home","shop"]}"""), TestContext.Current.CancellationToken);
        object? second = await Tool(registry, "add_note").InvokeAsync(Json("""{"text":"call Sam","priority":"High","tags":null}"""), TestContext.Current.CancellationToken);

        Assert.Equal("saved note 1", first);
        Assert.Equal("saved note 2", second);
        Assert.Equal(("buy milk", Priority.Normal), (notes.Notes[0].Text, notes.Notes[0].Priority));
        Assert.Equal(["home", "shop"], notes.Notes[0].Tags);
        Assert.Equal(Priority.High, notes.Notes[1].Priority);
        Assert.Empty(notes.Notes[1].Tags);
        Assert.Equal("buy milk", await Tool(registry, "list_notes").InvokeAsync(Json("""{"limit":1}"""), TestContext.Current.CancellationToken));
        Assert.Equal("buy milk\ncall Sam", await Tool(registry, "list_notes").InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Async_tool_receives_the_cancellation_token()
    {
        var notes = new NoteTools();
        notes.AddNote("one");
        using var cancellation = new CancellationTokenSource();

        object? result = await Tool(Registry(notes), "clear_notes").InvokeAsync(new AIFunctionArguments(), cancellation.Token);

        Assert.Equal(1, result);
        Assert.Equal(cancellation.Token, notes.LastToken);
        Assert.Empty(notes.Notes);
    }

    [Fact]
    public async Task Binds_every_supported_type_from_json()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        object? result = await Tool(Registry(), "describe_values").InvokeAsync(Json($$"""
            {
              "big": 9007199254740993, "ratio": 0.25, "scale": 2, "flag": true, "id": "{{id}}",
              "when": "2026-01-02T03:04:05Z", "day": "2026-05-06", "numbers": [1, 2, 3],
              "priorities": ["Low", "High"], "maybe": null, "note": "hi", "level": null
            }
            """), TestContext.Current.CancellationToken);

        Assert.Equal($"9007199254740993|0.25|2|True|{id}|1767323045|2026|1,2,3|Low,High|null|hi|1.5|null", result);
    }

    [Fact]
    public async Task Nullable_optional_parameter_takes_its_default_only_when_absent()
    {
        const string Required = """
            "big": 1, "ratio": 1, "scale": 1, "flag": false, "id": "0f8fad5b-d9cb-469f-a165-70867728950e",
            "when": "2026-01-02T03:04:05Z", "day": "2026-05-06", "numbers": [], "priorities": [], "maybe": 1, "note": null
            """;
        AIFunction tool = Tool(Registry(), "describe_values");

        object? absent = await tool.InvokeAsync(Json($$"""{ {{Required}} }"""), TestContext.Current.CancellationToken);
        object? explicitNull = await tool.InvokeAsync(Json($$"""{ {{Required}}, "level": null }"""), TestContext.Current.CancellationToken);

        Assert.EndsWith("|High", (string?)absent, StringComparison.Ordinal);
        Assert.EndsWith("|null", (string?)explicitNull, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binds_typed_clr_arguments()
    {
        var arguments = new AIFunctionArguments
        {
            ["big"] = 5,
            ["ratio"] = 0.5,
            ["scale"] = 1.5f,
            ["flag"] = false,
            ["id"] = Guid.Empty,
            ["when"] = DateTimeOffset.UnixEpoch,
            ["day"] = new DateTime(2020, 1, 1),
            ["numbers"] = new List<int> { 4 },
            ["priorities"] = new[] { Priority.Normal },
            ["maybe"] = 7,
            ["note"] = null,
            ["amount"] = 2m,
            ["level"] = Priority.Low,
        };

        object? result = await Tool(Registry(), "describe_values").InvokeAsync(arguments, TestContext.Current.CancellationToken);

        Assert.Equal($"5|0.5|1.5|False|{Guid.Empty}|0|2020|4|Normal|7|null|2|Low", result);
    }

    [Fact]
    public async Task Void_tool_returns_null()
    {
        Assert.Null(await Tool(Registry(), "ping").InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{}""", "text", "Missing required argument 'text'")]
    [InlineData("""{"text":3}""", "text", "must be a string; got 3")]
    [InlineData("""{"text":"x","priority":"Urgent"}""", "priority", "'Urgent', which is not one of: Low, Normal, High")]
    [InlineData("""{"text":"x","priority":1}""", "priority", "must be a string")]
    [InlineData("""{"text":"x","tags":["a",2]}""", "tags[1]", "must be a string")]
    public async Task Bad_arguments_name_the_parameter_and_the_problem(string json, string parameter, string message)
    {
        ToolArgumentException exception = await Assert.ThrowsAsync<ToolArgumentException>(
            async () => await Tool(Registry(), "add_note").InvokeAsync(Json(json), TestContext.Current.CancellationToken));

        Assert.Equal(parameter, exception.ParamName);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Integer_out_of_range_is_an_argument_error()
    {
        await Assert.ThrowsAsync<ToolArgumentException>(
            async () => await Tool(Registry(), "list_notes").InvokeAsync(Json("""{"limit":4294967296}"""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Secret_tools_are_unavailable_without_a_catalogue()
    {
        ToolRegistry registry = Registry();

        Assert.DoesNotContain("call_api", registry.GetFunctions().Select(f => f.Name));
        Assert.DoesNotContain("call_api", registry.GetFunctions([]).Select(f => f.Name));
        Assert.Contains("call_api", registry.List().Select(d => d.Name));
    }

    [Fact]
    public void Secret_parameters_are_an_enum_of_catalogue_names()
    {
        AIFunction tool = Registry().GetFunctions(["openai.key", "github.token", "github.token"]).Single(f => f.Name == "call_api");

        JsonNode schema = JsonNode.Parse(tool.JsonSchema.GetRawText())!;
        Assert.Equal("""["github.token","openai.key"]""", schema["properties"]!["secret"]!["enum"]!.ToJsonString());
        Assert.Equal("""["github.token","openai.key",null]""", schema["properties"]!["fallback"]!["enum"]!.ToJsonString());
        Assert.Null(schema["properties"]!["url"]!["enum"]);
        Assert.Equal("call_api", tool.GetService<ToolDescriptor>()?.Name);
    }

    [Fact]
    public async Task Secret_outside_the_catalogue_is_refused_before_the_method_runs()
    {
        AIFunction tool = Registry().GetFunctions(["github.token"]).Single(f => f.Name == "call_api");

        Assert.Equal(
            "github.token https://api.github.com/ -",
            await tool.InvokeAsync(Json("""{"secret":"github.token","url":"https://api.github.com/"}"""), TestContext.Current.CancellationToken));
        ToolArgumentException unknown = await Assert.ThrowsAsync<ToolArgumentException>(
            async () => await tool.InvokeAsync(Json("""{"secret":"openai.key","url":"https://x/"}"""), TestContext.Current.CancellationToken));
        Assert.Equal("secret", unknown.ParamName);
        await Assert.ThrowsAsync<ToolArgumentException>(
            async () => await tool.InvokeAsync(Json("""{"secret":"github.token","url":"https://x/","fallback":"stolen"}"""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_secret_value_cannot_be_passed_as_a_name()
    {
        // SecretRef accepts names only, so a pasted token fails before any catalogue lookup.
        ToolArgumentException exception = await Assert.ThrowsAsync<ToolArgumentException>(
            async () => await Tool(Registry(), "call_api").InvokeAsync(
                Json("""{"secret":"ghp_Q9xv2LmT7rWc4ZpK8sNd3JfYh6BuE1aGo0Ri","url":"https://x/"}"""), TestContext.Current.CancellationToken));

        Assert.Equal("secret", exception.ParamName);
        Assert.Equal(new SecretRef("github.token"), ToolArguments.ReadSecretRef(new SecretRef("github.token"), "secret"));
    }

    [Fact]
    public async Task The_call_context_is_bound_from_the_turn_and_not_offered_to_the_model()
    {
        ToolFunction whoami = new SessionTools().CreateTools().Single(t => t.Name == "whoami");

        Assert.Equal(["greeting"], whoami.Descriptor.Parameters.Select(p => p.Name));
        Assert.False(whoami.JsonSchema.GetProperty("properties").TryGetProperty("context", out _));
        AIFunctionArguments arguments = new ToolCallContext("s1", "call-1") { Tainted = true }.AttachTo(Json("""{"greeting":"hello"}"""));
        Assert.Equal("hello s1/call-1 tainted=True", await whoami.InvokeAsync(arguments, TestContext.Current.CancellationToken));

        // A tool that needs the context refuses a call made outside a turn; a nullable one gets null.
        InvalidOperationException outside = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await whoami.InvokeAsync(Json("{}"), TestContext.Current.CancellationToken));
        Assert.Contains("whoami", outside.Message, StringComparison.Ordinal);
        ToolFunction optional = new SessionTools().CreateTools().Single(t => t.Name == "session_or_none");
        Assert.Equal("none", await optional.InvokeAsync(Json("{}"), TestContext.Current.CancellationToken));
        Assert.Equal("s2", await optional.InvokeAsync(new ToolCallContext("s2", "c").AttachTo(Json("{}")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Corvid_Tools_has_no_compiler_dependency_at_runtime()
    {
        // The generator is build tooling: nothing it uses may reach the runtime library's references.
        string[] references = [.. typeof(ToolRegistry).Assembly.GetReferencedAssemblies().Select(a => a.Name!)];

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        Assert.DoesNotContain("Corvid.Tools.Generators", references);
        Assert.DoesNotContain(typeof(NoteTools).Assembly.GetReferencedAssemblies(), a => a.Name == "Corvid.Tools.Generators");
    }
}
