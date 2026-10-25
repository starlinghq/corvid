using System.Text.Json.Nodes;
using Corvid.Host.Tests;
using Corvid.Kernel.Turns;

namespace Corvid.Acp.Tests;

/// <summary>A5: a child session in the editor: the spawn's permission card shows its plan, and the child is a call of its own.</summary>
public sealed class ChildUpdateTests
{
    private static object Prompt(string sessionId, string text) => new { sessionId, prompt = new object[] { new { type = "text", text } } };

    private static object Initialize() => new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false } }, clientInfo = new { name = "test", version = "1" } };

    [Fact]
    public async Task A_delegated_child_is_shown_as_its_own_tool_call_started_and_finished()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, """{"goal":"count the items","tools":["list_notes"]}"""),
            Reply.Say("Two items."),
            Reply.Say("Your helper counted two.")));
        await using var client = AcpClient.Start(TestHost.Create(models, new Dictionary<string, string?>
        {
            ["Corvid:Tools:Delegate"] = "true",
            ["Corvid:Approvals:Mode"] = "Manual",
        }));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var response = await client.RequestAsync("session/prompt", Prompt(session, "ask a helper"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        // The permission card carries the child's plan.
        var card = Assert.Single(client.PermissionRequests)["params"]!["toolCall"]!["content"]!.AsArray()
            .Select(item => item!["content"]?["text"]?.GetValue<string>() ?? "").ToList();
        Assert.Contains(card, text => text.Contains("Child: count the items", StringComparison.Ordinal)
            && text.Contains("tools: list_notes", StringComparison.Ordinal));
        var updates = client.UpdatesFor(session);
        var started = updates.Single(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call"
            && update["toolCallId"]!.GetValue<string>().StartsWith("child-", StringComparison.Ordinal));
        var childId = started["toolCallId"]!.GetValue<string>();
        Assert.Equal("Child session: count the items", started["title"]!.GetValue<string>());
        Assert.Equal("in_progress", started["status"]!.GetValue<string>());
        var ended = updates.Single(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call_update"
            && update["toolCallId"]!.GetValue<string>() == childId);
        Assert.Equal("completed", ended["status"]!.GetValue<string>());
        Assert.Equal("Two items.", ended["content"]![0]!["content"]!["text"]!.GetValue<string>());
    }
}
