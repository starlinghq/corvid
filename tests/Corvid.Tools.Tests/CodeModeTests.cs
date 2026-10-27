using System.Text;
using System.Text.Json;
using Corvid.Tools.CodeMode;
using Corvid.Tools.Fixtures;

namespace Corvid.Tools.Tests;

/// <summary>A7: code mode's descriptor JSON and the tools socket's wire format, host side.</summary>
public sealed class CodeModeTests
{
    private const string Token = "tok_0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolRegistry Registry()
    {
        var registry = new ToolRegistry();
        registry.Add(new NoteTools());
        registry.AddRange(ApiTools.CreateTools());
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void The_catalogue_is_the_registry_cut_to_the_offer_and_the_allowlist()
    {
        var registry = Registry();
        var offered = new HashSet<string>(["add_note", "list_notes", "call_api", "ping"], StringComparer.Ordinal);

        var tools = CodeModeCatalog.Describe(registry, offered, ["add_note", "list_notes", "call_api", "clear_notes", "missing"]);

        // clear_notes is allowed but not offered; ping offered but not allowed; call_api has a secret parameter.
        Assert.Equal(["add_note", "list_notes"], tools.Select(tool => tool.Name));
        Assert.Empty(CodeModeCatalog.Describe(registry, null, ["add_note"]));
        Assert.True(CodeModeCatalog.IsIdentifier("list_notes"));
        Assert.False(CodeModeCatalog.IsIdentifier("mcp.server-tool"));
        Assert.False(CodeModeCatalog.IsIdentifier("1tool"));
    }

    private sealed class Stub(string name) : ToolFunction(new ToolDescriptor(name, "A stand-in.", """{"type":"object","properties":{}}""",
        new ToolRisk(SideEffects.None, Network: false, Destructive: false), []))
    {
        protected override ValueTask<object?> InvokeCoreAsync(Microsoft.Extensions.AI.AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>("ran");
    }

    // A7 review (security L4): a tool that delegates, asks, runs scripts, writes memory, sends or schedules is never
    // described to a script, even when the turn offers it and the allowlist names it.
    [Fact]
    public void A_denied_tool_is_never_described_whatever_the_allowlist_says()
    {
        var registry = Registry();
        string[] denied = ["delegate_task", "ask_user", "execute_code", "memory_add", "send_message", "cron_create"];
        foreach (var name in denied)
            registry.Add(new Stub(name));
        var offered = new HashSet<string>([.. denied, "list_notes"], StringComparer.Ordinal);

        var tools = CodeModeCatalog.Describe(registry, offered, [.. denied, "list_notes"]);

        Assert.Equal(["list_notes"], tools.Select(tool => tool.Name));
        Assert.All(denied, name => Assert.Contains(name, CodeModeCatalog.Denied));
    }

    [Fact]
    public void The_descriptor_json_has_its_documented_form()
    {
        var registry = Registry();
        var tools = CodeModeCatalog.Describe(registry, new HashSet<string>(["list_notes"]), ["list_notes"]);

        using var document = JsonDocument.Parse(CodeModeCatalog.ToJson(tools));

        var root = document.RootElement;
        Assert.Equal(["version", "tools"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(["name", "description", "parameters"], tool.EnumerateObject().Select(p => p.Name));
        Assert.Equal("list_notes", tool.GetProperty("name").GetString());
        Assert.Equal(registry.List().Single(d => d.Name == "list_notes").Description, tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
        Assert.True(tool.GetProperty("parameters").GetProperty("properties").TryGetProperty("limit", out _));
    }

    /// <summary>Runs one connection over in-memory streams: the script's lines in, the host's lines out.</summary>
    private static async Task<List<JsonElement>> ExchangeAsync(CodeModeSession session, params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));
        var output = new MemoryStream();
        await session.ServeAsync(input, output, Ct);
        return [.. Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Json)];
    }

    private static string Hello(string token = Token) => $$"""{"token":"{{token}}","v":1}""";

    private sealed class Invoker
    {
        public List<(string Tool, string Arguments)> Calls { get; } = [];

        public Func<string, JsonElement, NestedToolResult> Answer { get; set; } =
            (tool, _) => new NestedToolResult($"p.{tool}", Json("""{"done":true}"""), IsError: false, Tainted: false);

        public Task<NestedToolResult> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls.Add((tool, arguments.GetRawText()));
            return Task.FromResult(Answer(tool, arguments));
        }
    }

    private static readonly CodeModeTool[] Tools = [new("list_notes", "List.", Json("{}")), new("add_note", "Add.", Json("{}"))];

    [Fact]
    public async Task A_run_greets_with_its_limits_then_answers_each_request_in_order()
    {
        var invoker = new Invoker
        {
            Answer = (tool, arguments) => tool == "add_note"
                ? new NestedToolResult("p.2", Json("""{"error":"not run","details":["denied by the user: add a note"]}"""), IsError: true, Tainted: false)
                : new NestedToolResult("p.1", Json("""["note one"]"""), IsError: false, Tainted: true),
        };
        var session = new CodeModeSession(Token, Tools, invoker.InvokeAsync, new CodeModeLimits { MaxCalls = 5 });

        var replies = await ExchangeAsync(session, Hello(),
            """{"id":1,"tool":"list_notes","args":{"limit":2}}""",
            """{"id":"b","tool":"add_note","args":{"text":"x"}}""",
            """{"id":3,"tool":"list_notes"}""");

        Assert.Equal("""{"ok":true,"v":1,"limits":{"calls":5,"request_bytes":262144,"response_bytes":1048576,"total_response_bytes":8388608}}""",
            replies[0].GetRawText());
        Assert.Equal("""{"id":1,"ok":true,"result":["note one"],"tainted":true}""", replies[1].GetRawText());
        Assert.Equal("""{"id":"b","ok":false,"error":{"code":"tool_error","message":"not run: denied by the user: add a note","detail":{"error":"not run","details":["denied by the user: add a note"]}},"tainted":false}""",
            replies[2].GetRawText());
        Assert.Equal(3, replies[3].GetProperty("id").GetInt32());
        Assert.Equal([("list_notes", """{"limit":2}"""), ("add_note", """{"text":"x"}"""), ("list_notes", "{}")], invoker.Calls);
        Assert.Equal(3, session.Calls);
    }

    [Theory]
    [InlineData("""{"token":"wrong_0123456789abcdef0123456789abcd","v":1}""")]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"id":1,"tool":"list_notes"}""")]
    [InlineData("not json")]
    public async Task A_connection_without_the_runs_token_is_refused_and_nothing_runs(string hello)
    {
        var invoker = new Invoker();
        var session = new CodeModeSession(Token, Tools, invoker.InvokeAsync);

        var replies = await ExchangeAsync(session, hello, """{"id":1,"tool":"list_notes"}""");

        var refusal = Assert.Single(replies);
        Assert.False(refusal.GetProperty("ok").GetBoolean());
        Assert.Equal(CodeModeErrors.Unauthorized, refusal.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task Bad_requests_unknown_tools_and_the_call_limit_are_refused_without_running_anything()
    {
        var invoker = new Invoker();
        var session = new CodeModeSession(Token, Tools, invoker.InvokeAsync, new CodeModeLimits { MaxCalls = 1 });

        var replies = await ExchangeAsync(session, Hello(),
            "not json",
            """{"tool":"list_notes"}""",
            """{"id":{"x":1},"tool":"list_notes"}""",
            """{"id":2,"tool":"list_notes","args":[1]}""",
            """{"id":3,"tool":"clear_notes"}""",
            """{"id":4,"tool":"list_notes"}""",
            """{"id":5,"tool":"list_notes"}""");

        string Code(int i) => replies[i].GetProperty("error").GetProperty("code").GetString()!;
        Assert.Equal(CodeModeErrors.BadRequest, Code(1));
        Assert.Equal(CodeModeErrors.BadRequest, Code(2));
        Assert.Equal(CodeModeErrors.BadRequest, Code(3));
        Assert.Equal(JsonValueKind.Null, replies[3].GetProperty("id").ValueKind);
        Assert.Equal(CodeModeErrors.BadRequest, Code(4));
        Assert.Equal(2, replies[4].GetProperty("id").GetInt32());
        Assert.Equal(CodeModeErrors.UnknownTool, Code(5));
        Assert.True(replies[6].GetProperty("ok").GetBoolean());
        Assert.Equal(CodeModeErrors.CallLimit, Code(7));
        Assert.Equal([("list_notes", "{}")], invoker.Calls);
    }

    [Fact]
    public async Task Size_limits_hold_for_a_request_one_result_and_the_whole_run()
    {
        var big = new string('x', 2_000);
        var invoker = new Invoker
        {
            Answer = (tool, _) => new NestedToolResult("p." + tool, Json(tool == "add_note" ? $"\"{big}\"" : "\"small\""), IsError: false, Tainted: false),
        };
        var session = new CodeModeSession(Token, Tools, invoker.InvokeAsync,
            new CodeModeLimits { MaxRequestBytes = 200, MaxResponseBytes = 1_000, MaxTotalResponseBytes = 300 });

        var replies = await ExchangeAsync(session, Hello(),
            "{\"id\":1,\"tool\":\"list_notes\",\"args\":{\"pad\":\"" + new string('y', 300) + "\"}}",
            """{"id":2,"tool":"add_note"}""",
            """{"id":3,"tool":"list_notes"}""",
            """{"id":4,"tool":"list_notes"}""",
            """{"id":5,"tool":"list_notes"}""");

        string Code(int i) => replies[i].GetProperty("error").GetProperty("code").GetString()!;
        // The oversized request never ran; the oversized result ran but is not returned; then the run's total runs out.
        Assert.Equal(CodeModeErrors.TooLarge, Code(1));
        Assert.Equal(CodeModeErrors.TooLarge, Code(2));
        Assert.Contains("the call ran", replies[2].GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(replies[3].GetProperty("ok").GetBoolean());
        Assert.Contains(replies.Skip(4), reply => !reply.GetProperty("ok").GetBoolean() && reply.GetProperty("error").GetProperty("code").GetString() == CodeModeErrors.OutputLimit);
        Assert.DoesNotContain(invoker.Calls, call => call.Arguments.Contains("yyy", StringComparison.Ordinal));
        Assert.True(session.ResponseBytes >= 300);
    }

    [Fact]
    public async Task A_finished_parent_closes_the_connection()
    {
        var session = new CodeModeSession(Token, Tools, (_, _, _) => throw new InvalidOperationException("The call has finished."));

        var replies = await ExchangeAsync(session, Hello(), """{"id":1,"tool":"list_notes"}""", """{"id":2,"tool":"list_notes"}""");

        Assert.Equal(2, replies.Count);
        Assert.Equal(CodeModeErrors.Closed, replies[1].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void A_new_token_is_one_the_sandbox_accepts()
    {
        var token = CodeModeProtocol.NewToken();
        Assert.Equal(43, token.Length);
        Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(token, CodeModeProtocol.NewToken());
    }
}
