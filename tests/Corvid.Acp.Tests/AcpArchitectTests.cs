using System.Text.Json.Nodes;
using Corvid.Host.Tests;

namespace Corvid.Acp.Tests;

/// <summary>
/// A7: an architect/editor turn in the editor: the plan is a finished <c>think</c> step titled with who planned and who
/// edits, before the editor's call, and never answer text; <c>session/load</c> shows it the same way.
/// </summary>
public sealed class AcpArchitectTests
{
    private static object Prompt(string sessionId, string text) => new { sessionId, prompt = new object[] { new { type = "text", text } } };

    private static object Initialize() => new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false } }, clientInfo = new { name = "test", version = "1" } };

    private static string Kind(JsonNode update) => update["sessionUpdate"]!.GetValue<string>();

    [Fact]
    public async Task A_planned_turn_shows_its_plan_as_a_think_step_live_and_on_load()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        await client.RequestAsync("session/prompt", Prompt(session, "/architect"));
        var before = client.UpdatesFor(session).Count;
        var response = await client.RequestAsync("session/prompt", Prompt(session, "list my notes"));
        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());

        Check(client.UpdatesFor(session).Skip(before).ToList());

        var count = client.UpdatesFor(session).Count;
        await client.RequestAsync("session/load", new { sessionId = session, cwd = client.Workspace, mcpServers = Array.Empty<object>() });
        Check(client.UpdatesFor(session).Skip(count).ToList());

        static void Check(List<JsonObject> updates)
        {
            var plan = Assert.Single(updates, update => Kind(update) == "tool_call" && update["kind"]?.GetValue<string>() == "think");
            Assert.StartsWith("plan-", plan["toolCallId"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("Plan by fake-gen-large@cpu · edited by fake-gen@cpu", plan["title"]!.GetValue<string>());
            Assert.Equal("completed", plan["status"]!.GetValue<string>());
            Assert.Equal("Hello from the larger model.", plan["content"]![0]!["content"]!["text"]!.GetValue<string>());
            var call = updates.FindIndex(update => Kind(update) == "tool_call" && update["title"]?.GetValue<string>() == "list_notes");
            Assert.True(updates.IndexOf(plan) < call);
            var answer = string.Concat(updates.Where(update => Kind(update) == "agent_message_chunk").Select(update => update["content"]!["text"]!.GetValue<string>()));
            Assert.Equal("You have two notes.", answer);
        }
    }
}
