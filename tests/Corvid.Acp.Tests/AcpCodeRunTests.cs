using System.Text.Json.Nodes;
using Corvid.Host.Tests;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Acp.Tests;

/// <summary>
/// A7: a code mode run in the editor. ACP has no nested calls, so each tool the script calls is a <c>tool_call</c> of its
/// own whose <c>_meta</c> names the run (<c>{"corvid":{"parentToolCallId":…}}</c>), live and on <c>session/load</c>; the
/// run's permission card shows its whole source and says each call is asked on its own. The model's own calls carry no
/// <c>_meta</c>, so the recorded exchanges are unchanged.
/// </summary>
public sealed class AcpCodeRunTests
{
    private static object Prompt(string sessionId, string text) => new { sessionId, prompt = new object[] { new { type = "text", text } } };

    private static object Initialize() => new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false } }, clientInfo = new { name = "test", version = "1" } };

    private sealed class Tools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    private static string? Parent(JsonObject update) => update["_meta"]?["corvid"]?["parentToolCallId"]?.GetValue<string>();

    [Fact]
    public async Task A_scripts_calls_name_their_run_in_meta_live_and_on_load_and_the_card_shows_the_source()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call("execute_code", """{"language":"python","code":"import corvid_tools as t\nprint(t.list_notes())"}"""),
            Reply.Say("The script listed your notes.")));
        var code = new FakeCodeTool(("list_notes", "{}"));
        await using var client = AcpClient.Start(TestHost.Create(models, new Dictionary<string, string?> { ["Corvid:Approvals:Mode"] = "Manual" },
            services => services.AddSingleton<IToolProvider>(new Tools(code))));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var response = await client.RequestAsync("session/prompt", Prompt(session, "run the weather script over my notes"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        Assert.False(Assert.Single(code.Results).IsError);
        // The run's card: the language, the limits, that each call is asked on its own, and every line of the source.
        var request = Assert.Single(client.PermissionRequests)["params"]!["toolCall"]!;
        Assert.Null(request["_meta"]);
        var card = string.Join("\n", request["content"]!.AsArray().Select(item => item!["content"]?["text"]?.GetValue<string>() ?? ""));
        Assert.Contains("Code run: Python, 2 lines", card, StringComparison.Ordinal);
        Assert.Contains(Corvid.Host.Sessions.CodeRunPresentation.EachCallAsked, card, StringComparison.Ordinal);
        Assert.Contains("  import corvid_tools as t\n  print(t.list_notes())", card, StringComparison.Ordinal);

        var live = client.UpdatesFor(session);
        var run = live.First(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call" && update["title"]!.GetValue<string>() != "list_notes");
        var runId = run["toolCallId"]!.GetValue<string>();
        Assert.Null(run["_meta"]);
        var nested = live.Where(update => update["toolCallId"]?.GetValue<string>() == runId + ".1").ToList();
        Assert.Contains(nested, update => update["sessionUpdate"]!.GetValue<string>() == "tool_call");
        Assert.Contains(nested, update => update["sessionUpdate"]!.GetValue<string>() == "tool_call_update" && update["status"]?.GetValue<string>() == "completed");
        Assert.All(nested.Where(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call"), update => Assert.Equal(runId, Parent(update)));
        Assert.All(live.Where(update => update["toolCallId"]?.GetValue<string>() == runId), update => Assert.Null(update["_meta"]));

        var before = client.UpdatesFor(session).Count;
        await client.RequestAsync("session/load", new { sessionId = session, cwd = client.Workspace, mcpServers = Array.Empty<object>() });

        var replay = client.UpdatesFor(session).Skip(before).ToList();
        var replayed = replay.Where(update => update["toolCallId"]?.GetValue<string>() == runId + ".1").ToList();
        Assert.Equal(["tool_call", "tool_call_update"], replayed.Select(update => update["sessionUpdate"]!.GetValue<string>()));
        Assert.All(replayed, update => Assert.Equal(runId, Parent(update)));
        Assert.All(replay.Where(update => update["toolCallId"]?.GetValue<string>() == runId), update => Assert.Null(update["_meta"]));
    }

    [Fact]
    public void The_model_s_own_calls_carry_no_meta()
    {
        Assert.Null(TurnReporter.ParentMeta(null));
        Assert.Equal("""{"corvid":{"parentToolCallId":"c1"}}""", TurnReporter.ParentMeta("c1")!.Value.GetRawText());
    }
}
