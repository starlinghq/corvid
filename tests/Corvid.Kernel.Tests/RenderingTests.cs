using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

public sealed class RenderingTests
{
    private static SessionSnapshot Snapshot(params SessionEvent[] events) =>
        SessionProjection.Build("s", events.Select((payload, index) => new StoredEvent("s", index + 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, payload)));

    private static readonly string Big = new('x', 4_000);

    [Fact]
    public void The_projection_renders_as_chat_messages_with_calls_grouped()
    {
        var snapshot = Snapshot(
            new UserMessage("find the notes"),
            new ToolCallIssued("a", "search", Samples.Json("""{"q":"boiler"}""")),
            new ToolCallIssued("b", "search", Samples.Json("""{"q":"service"}""")),
            new ToolResult("a", Samples.Json("\"notes/house.md\"")),
            new ToolResult("b", Samples.Json("[]")),
            new AssistantMessage("One note."));

        var prompt = PromptRenderer.Render(snapshot, "system", CharsPerTokenCounter.Instance, 10_000);

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool, ChatRole.Assistant],
            prompt.Messages.Select(message => message.Role));
        Assert.Equal(["a", "b"], prompt.Messages[2].Contents.OfType<FunctionCallContent>().Select(call => call.CallId));
        Assert.Equal("boiler", ((JsonElement)prompt.Messages[2].Contents.OfType<FunctionCallContent>().First().Arguments!["q"]!).GetString());
        Assert.False(prompt.OverBudget);
        Assert.Empty(prompt.Cleared);
    }

    [Fact]
    public void Clear_results_decisions_stub_or_drop_results_and_a_later_keep_does_not_restore_them()
    {
        var snapshot = Snapshot(
            new UserMessage("go"),
            new ToolCallIssued("a", "web_extract", Samples.Json("{}")),
            new ToolResult("a", JsonSerializer.SerializeToElement(Big)),
            new ToolCallIssued("b", "list", Samples.Json("{}")),
            new ToolResult("b", Samples.Json("\"short\"")),
            Clear("a", "stub_older_than_last_2"),
            Clear("b", "apply:drop"),
            Clear("a", "apply:keep"));

        var prompt = PromptRenderer.Render(snapshot, "system", CharsPerTokenCounter.Instance, 10_000);

        Assert.Equal(ResultView.Stub, prompt.Cleared["a"]);
        Assert.Equal(ResultView.Dropped, prompt.Cleared["b"]);
        var results = prompt.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => (string)result.Result!).ToList();
        Assert.Equal(["[cleared: web_extract result, 3.9 KB; re-run or see event 3]", "[dropped: list result; see event 5]"], results);
    }

    [Fact]
    public void Over_budget_the_oldest_results_are_stubbed_first_sparing_the_protected_and_the_newest()
    {
        var snapshot = Snapshot(
            new UserMessage("go"),
            new ToolCallIssued("a", "read", Samples.Json("{}")), new ToolResult("a", JsonSerializer.SerializeToElement(Big)),
            new ToolCallIssued("b", "read", Samples.Json("{}")), new ToolResult("b", JsonSerializer.SerializeToElement(Big)),
            new ToolCallIssued("c", "read", Samples.Json("{}")), new ToolResult("c", JsonSerializer.SerializeToElement(Big)));

        var roomy = PromptRenderer.Render(snapshot, "system", CharsPerTokenCounter.Instance, 2_600, new HashSet<string> { "c" });
        Assert.Equal(["a"], roomy.Cleared.Keys);
        Assert.False(roomy.OverBudget);

        var tight = PromptRenderer.Render(snapshot, "system", CharsPerTokenCounter.Instance, 500, new HashSet<string> { "b", "c" });
        Assert.Equal(["a", "b"], tight.Cleared.Keys.Order(StringComparer.Ordinal));
        // The newest result is never stubbed: the generator needs it to continue. Compaction is a later item.
        Assert.True(tight.OverBudget);
    }

    [Fact]
    public void Arguments_are_checked_against_the_generated_schema()
    {
        var schema = Samples.Json("""
            {"type":"object","properties":{"text":{"type":"string"},"priority":{"type":"string","enum":["Low","Normal","High"]},
             "tags":{"type":["array","null"],"items":{"type":"string"}},"limit":{"type":"integer"}},"required":["text"]}
            """);

        Assert.Empty(ArgumentValidator.Validate(schema, Samples.Json("""{"text":"a","priority":"High","tags":null,"limit":3}""")));
        Assert.Equal(
            [
                "'priority' is \"Urgent\", which is not one of: \"Low\", \"Normal\", \"High\"",
                "'tags[1]' must be string; got a number 2",
                "'limit' must be integer; got a number 2.5",
                "unknown argument 'colour'; expected one of: text, priority, tags, limit",
                "missing required argument 'text'",
            ],
            ArgumentValidator.Validate(schema, Samples.Json("""{"priority":"Urgent","tags":["a",2],"limit":2.5,"colour":"red"}""")));
        Assert.Equal(["arguments must be a JSON object; got an array"], ArgumentValidator.Validate(schema, Samples.Json("[]")));
    }

    [Fact]
    public void Values_from_chat_clients_and_tools_become_json_without_reflection()
    {
        var value = JsonValues.Object(new Dictionary<string, object?>
        {
            ["text"] = "note",
            ["count"] = 3,
            ["ratio"] = 0.5,
            ["when"] = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero),
            ["tags"] = new List<string> { "a", "b" },
            ["nested"] = new Dictionary<string, object?> { ["ok"] = true, ["none"] = null },
            ["json"] = Samples.Json("""{"x":1}"""),
        });

        Assert.Equal("""{"text":"note","count":3,"ratio":0.5,"when":"2026-10-19T09:00:00+00:00","tags":["a","b"],"nested":{"ok":true,"none":null},"json":{"x":1}}""",
            value.GetRawText());
        Assert.Equal("plain", JsonValues.Text(JsonValues.ToElement("plain")));
        // A JsonObject is also a collection of pairs; it must stay an object.
        Assert.Equal("""{"a":[1,"b"]}""",
            JsonValues.ToElement(new System.Text.Json.Nodes.JsonObject { ["a"] = new System.Text.Json.Nodes.JsonArray(1, "b") }).GetRawText());
    }

    private static Decision Clear(string callId, string action) =>
        new(Guid.NewGuid().ToString("N"), "clear_results/" + callId, ImmutableDictionary<string, double>.Empty, 0, 0.6, action);
}
