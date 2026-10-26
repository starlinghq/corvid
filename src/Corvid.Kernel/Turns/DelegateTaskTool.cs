using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Turns;

/// <summary>
/// One child a <c>delegate_task</c> call asks for (A5, plan §14), as read from the call: its goal, its toolset (named tools,
/// a named preset, or both), the model mix, the budget and the output schema. Everything here is the model's request and
/// untrusted: the host clamps it to the parent session's ceilings and refuses what it cannot grant.
/// </summary>
public sealed record DelegationTask(string Goal)
{
    public ImmutableArray<string> Tools { get; init; } = [];

    /// <summary>A named toolset (<c>read_only</c>, or one configured under <c>Corvid:Agents:Toolsets</c>), added to <see cref="Tools"/>.</summary>
    public string? Toolset { get; init; }

    public ModelMix? Mix { get; init; }

    public string? MixName { get; init; }

    public int? MaxSteps { get; init; }

    public int? MaxTokens { get; init; }

    public int? MaxSeconds { get; init; }

    public JsonElement? OutputSchema { get; init; }
}

/// <summary>
/// <c>delegate_task</c> (A5, plan §14 and §17 row 1.7): the generator hands one goal, or several at once, to child sessions
/// with a fresh log, an explicit toolset, a model mix, a budget and an optional output schema, and reads their results as
/// this call's result. The kernel knows the tool by name: it is never offered to a guest's turn, its calls are approved as
/// a side effect (the card shows each child's plan), and the ingress <c>delegate</c> question is asked when a calibrated
/// decider and a generator with native tool calls could use it. The children themselves are started by the host, which
/// supplies <see cref="Runner"/>; a child is never given this tool (depth 1).
/// </summary>
public sealed class DelegateTaskTool : ToolFunction
{
    public const string ToolName = "delegate_task";

    /// <summary>Children one call may ask for (Hermes' 10, and <c>Corvid:Agents:MaxConcurrent</c>'s ceiling).</summary>
    public const int MaxTasks = 10;

    /// <summary>Requests it is shortlisted for (plan §5.4's multi-vector examples).</summary>
    public static ImmutableArray<string> Examples { get; } =
    [
        "research these three things in parallel", "have a subagent look into it", "delegate this to a helper",
        "split this up and work on the parts separately", "check each of these files independently", "summarise each document separately",
    ];

    /// <summary>Starts the children a valid call asks for and returns the call's result.</summary>
    public delegate Task<object?> DelegationRunner(ImmutableArray<DelegationTask> tasks, ToolCallContext context, CancellationToken cancellationToken);

    private readonly DelegationRunner _runner;

    public DelegateTaskTool(DelegationRunner runner)
        : base(new ToolDescriptor(ToolName,
            "Hand a self-contained goal to a child session (a subagent with a fresh context), or several goals at once in " +
            "'tasks' to run them in parallel. Give each child only the tools it needs. Its answer comes back as this call's " +
            "result. The child cannot ask the user anything, write memory, send messages or schedule jobs.",
            Schema, new ToolRisk(SideEffects.Process, Network: false, Destructive: false),
            [
                new ToolParameter("goal", "What the child is to do, self-contained.", false, false),
                new ToolParameter("tools", "The tools the child may use, by name.", false, false),
                new ToolParameter("toolset", "A named toolset: read_only, or a configured one.", false, false),
                new ToolParameter("mix", "Which model serves each slot of the child.", false, false),
                new ToolParameter("mix_name", "A configured model mix by name.", false, false),
                new ToolParameter("max_steps", "The most generator steps.", false, false),
                new ToolParameter("max_tokens", "The most tokens.", false, false),
                new ToolParameter("max_seconds", "The longest it may run.", false, false),
                new ToolParameter("output_schema", "A JSON Schema of an object the answer must meet.", false, false),
                new ToolParameter("tasks", "Several children to run in parallel, each with these fields.", false, false),
            ]))
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>The JSON Schema of the arguments: one child's fields, or <c>tasks</c> with several.</summary>
    public static string Schema { get; } = BuildSchema();

    /// <summary>
    /// Reads a call's children: <c>tasks</c> when given, else the one child the top-level fields describe. False with the
    /// reason, in a sentence the model can act on, when the call is malformed.
    /// </summary>
    public static bool TryRead(JsonElement arguments, out ImmutableArray<DelegationTask> tasks, out string? error)
    {
        tasks = [];
        error = null;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "the arguments must be a JSON object";
            return false;
        }
        if (arguments.TryGetProperty("tasks", out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                error = "'tasks' must be a non-empty array of children";
                return false;
            }
            if (list.GetArrayLength() > MaxTasks)
            {
                error = $"'tasks' holds {list.GetArrayLength()} children; one call starts at most {MaxTasks}";
                return false;
            }
            if (arguments.TryGetProperty("goal", out _))
            {
                error = "give either 'goal' (one child) or 'tasks' (several), not both";
                return false;
            }
            var read = ImmutableArray.CreateBuilder<DelegationTask>();
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                if (!TryReadOne(item, out var task, out var why))
                {
                    error = $"tasks[{index}]: {why}";
                    return false;
                }
                read.Add(task!);
                index++;
            }
            tasks = read.ToImmutable();
            return true;
        }
        if (!TryReadOne(arguments, out var single, out error))
            return false;
        tasks = [single!];
        return true;
    }

    private static bool TryReadOne(JsonElement item, out DelegationTask? task, out string? error)
    {
        task = null;
        error = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = "a child is a JSON object";
            return false;
        }
        var goal = item.TryGetProperty("goal", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()!.Trim() : "";
        if (goal.Length == 0)
        {
            error = "'goal' is required: what the child is to do, in words";
            return false;
        }
        var tools = ImmutableArray<string>.Empty;
        if (item.TryGetProperty("tools", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            if (t.ValueKind != JsonValueKind.Array || t.EnumerateArray().Any(name => name.ValueKind != JsonValueKind.String))
            {
                error = "'tools' must be an array of tool names";
                return false;
            }
            tools = [.. t.EnumerateArray().Select(name => name.GetString()!.Trim()).Where(name => name.Length > 0)];
        }
        ModelMix? mix = null;
        if (item.TryGetProperty("mix", out var m) && m.ValueKind != JsonValueKind.Null)
        {
            if (!ModelMix.TryParse(m, out var parsed, out var why))
            {
                error = "'mix': " + why;
                return false;
            }
            mix = parsed;
        }
        JsonElement? schema = null;
        if (item.TryGetProperty("output_schema", out var s) && s.ValueKind != JsonValueKind.Null)
        {
            if (s.ValueKind != JsonValueKind.Object)
            {
                error = "'output_schema' must be a JSON Schema object";
                return false;
            }
            schema = s.Clone();
        }
        if (!Number(item, "max_steps", out var steps, ref error) || !Number(item, "max_tokens", out var tokens, ref error)
            || !Number(item, "max_seconds", out var seconds, ref error))
            return false;
        task = new DelegationTask(goal)
        {
            Tools = tools,
            Toolset = Text(item, "toolset"),
            Mix = mix,
            MixName = Text(item, "mix_name"),
            MaxSteps = steps,
            MaxTokens = tokens,
            MaxSeconds = seconds,
            OutputSchema = schema,
        };
        return true;

        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
                ? text
                : null;

        static bool Number(JsonElement item, string name, out int? value, ref string? error)
        {
            value = null;
            if (!item.TryGetProperty(name, out var number) || number.ValueKind == JsonValueKind.Null)
                return true;
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var parsed) || parsed < 1)
            {
                error = $"'{name}' must be a positive whole number";
                return false;
            }
            value = parsed;
            return true;
        }
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = ToolCallContext.Require(arguments, ToolName);
        var element = JsonValues.Object(arguments);
        if (!TryRead(element, out var tasks, out var error))
            throw new ToolArgumentException(error!, "tasks");
        return await _runner(tasks, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The mix the agent may pass: every slot but the decider. Security M1 (A5 review): a child's decider is its parent's
    /// unless the person or a configured mix names another, so the host refuses an agent's decider entry.
    /// </summary>
    private static JsonNode AgentMix()
    {
        var mix = JsonNode.Parse(ModelMix.Schema)!.AsObject();
        mix["properties"]!.AsObject().Remove(ModelSlots.Decider);
        mix["description"] = "Which model serves each slot of the child session; slots left out keep the parent's. The decider is always this session's.";
        return mix;
    }

    private static string BuildSchema()
    {
        JsonObject Child() => new()
        {
            ["goal"] = new JsonObject { ["type"] = "string", ["description"] = "What the child is to do, self-contained: it sees nothing else of this conversation." },
            ["tools"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "The tools the child may use, by name; only tools this session has. Leave out for none.",
            },
            ["toolset"] = new JsonObject { ["type"] = "string", ["description"] = "A named toolset to add: read_only, or a configured one." },
            ["mix"] = AgentMix(),
            ["mix_name"] = new JsonObject { ["type"] = "string", ["description"] = "A configured model mix by name (Corvid:Agents:Mixes)." },
            ["max_steps"] = new JsonObject { ["type"] = "integer", ["description"] = "The most generator steps it may take." },
            ["max_tokens"] = new JsonObject { ["type"] = "integer", ["description"] = "The most tokens it may use." },
            ["max_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "The longest it may run, in seconds." },
            ["output_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "A JSON Schema of an object; the child's answer must be such an object, checked on return.",
            },
        };
        var properties = Child();
        properties["tasks"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = MaxTasks,
            ["description"] = "Several children to run in parallel, each with the fields above (goal required); instead of a top-level goal.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = Child(),
                ["required"] = new JsonArray("goal"),
                ["additionalProperties"] = false,
            },
        };
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false }.ToJsonString();
    }
}
