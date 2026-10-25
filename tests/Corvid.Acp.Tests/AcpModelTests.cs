using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.Host.Tests;
using Corvid.Models;

namespace Corvid.Acp.Tests;

/// <summary>A5: the generator and the decider as ACP session configuration options, set by the client and pushed when they move.</summary>
public sealed class AcpModelTests
{
    private static object Prompt(string sessionId, string text) => new { sessionId, prompt = new object[] { new { type = "text", text } } };

    private static object Initialize() => new { protocolVersion = 1, clientInfo = new { name = "test", version = "1" } };

    private static JsonNode Option(JsonNode options, string id) => options.AsArray().Single(option => option!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public async Task TheSessionOffersItsModelsAndTheClientSwitchesThem()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var created = await client.RequestAsync("session/new", new { cwd = client.Workspace, mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(120));
        var session = created["result"]!["sessionId"]!.GetValue<string>();
        var options = created["result"]!["configOptions"]!;
        var model = Option(options, "model");
        Assert.Equal("model", model["category"]!.GetValue<string>());
        Assert.Equal("select", model["type"]!.GetValue<string>());
        Assert.Equal("fake-gen", model["currentValue"]!.GetValue<string>());
        Assert.Equal(["fake-gen", "fake-gen-large"], model["options"]!.AsArray().Select(value => value!["value"]!.GetValue<string>()));
        var decider = Option(options, "decider");
        Assert.Equal("_decider", decider["category"]!.GetValue<string>());
        Assert.Equal("degraded", decider["currentValue"]!.GetValue<string>());

        var set = await client.RequestAsync("session/set_config_option", new { sessionId = session, configId = "model", value = "fake-gen-large" });
        Assert.Equal("fake-gen-large", Option(set["result"]!["configOptions"]!, "model")["currentValue"]!.GetValue<string>());
        var runtime = await client.Runtime;
        Assert.Contains(runtime.Log.Read(session, cancellationToken: TestContext.Current.CancellationToken),
            item => item.Payload is ModelSwitched { Slot: ModelSlots.Generator, Reason: "session" });
        // The choice lasts for the session: the next prompt runs on it.
        await client.RequestAsync("session/prompt", Prompt(session, "hello"));
        Assert.Contains("Hello from the larger model.", string.Concat(client.UpdatesFor(session)
            .Where(update => update["sessionUpdate"]!.GetValue<string>() == "agent_message_chunk")
            .Select(update => update["content"]!["text"]!.GetValue<string>())), StringComparison.Ordinal);

        // The decider, per session.
        set = await client.RequestAsync("session/set_config_option", new { sessionId = session, configId = "decider", value = "fake-kev" });
        Assert.Equal("fake-kev", Option(set["result"]!["configOptions"]!, "decider")["currentValue"]!.GetValue<string>());
        Assert.True(runtime.TryGetSession(session, out var open));
        Assert.Equal("fake-kev", open.Slots.Current(ModelSlots.Decider).Profile.Id.Split('@')[0]);
    }

    [Fact]
    public async Task AChosenDeciderOrGeneratorCanFollowTheDefaultAgain()
    {
        // A5 review: once chosen, the decider had no value that went back to the degraded tier; "default" forgets the choice.
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        var runtime = await client.Runtime;
        static string[] Values(JsonNode option) => [.. option["options"]!.AsArray().Select(value => value!["value"]!.GetValue<string>())];

        var created = await client.RequestAsync("session/set_config_option", new { sessionId = session, configId = "decider", value = "fake-kev" });
        var decider = Option(created["result"]!["configOptions"]!, "decider");
        Assert.Equal("fake-kev", decider["currentValue"]!.GetValue<string>());
        Assert.Contains("default", Values(decider));
        Assert.DoesNotContain("default", Values(Option(created["result"]!["configOptions"]!, "model")));

        var reset = await client.RequestAsync("session/set_config_option", new { sessionId = session, configId = "decider", value = "default" });
        decider = Option(reset["result"]!["configOptions"]!, "decider");
        Assert.Equal("degraded", decider["currentValue"]!.GetValue<string>());
        Assert.DoesNotContain("default", Values(decider));
        Assert.True(runtime.TryGetSession(session, out var open));
        Assert.False(open.Slots.HasSessionChoice(ModelSlots.Decider));

        await client.RequestAsync("session/set_config_option", new { sessionId = session, configId = "model", value = "fake-gen-large" });
        reset = await client.RequestAsync("session/set_config_option", new { sessionId = session, configId = "model", value = "default" });
        Assert.Equal("fake-gen", Option(reset["result"]!["configOptions"]!, "model")["currentValue"]!.GetValue<string>());
        Assert.False(open.Slots.HasSessionChoice(ModelSlots.Generator));
    }

    [Fact]
    public async Task ABadOptionOrValueIsRefusedAndAHiddenNameIsNotOffered()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello.")));
        models.Provider.Add("fake‮gen", [ModelSlots.Generator], () => models.Larger);
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var created = await client.RequestAsync("session/new", new { cwd = client.Workspace, mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(120));
        var session = created["result"]!["sessionId"]!.GetValue<string>();
        Assert.DoesNotContain(Option(created["result"]!["configOptions"]!, "model")["options"]!.AsArray(),
            value => value!["value"]!.GetValue<string>().Contains('‮', StringComparison.Ordinal));

        foreach (var parameters in new object[]
        {
            new { sessionId = session, configId = "mode", value = "x" },
            new { sessionId = session, configId = "model", value = "qwen-9000" },
            new { sessionId = session, configId = "model", value = "fake‮gen" },
            new { sessionId = session, configId = "model", value = (object)true },
        })
        {
            var refused = await client.RequestAsync("session/set_config_option", parameters);
            Assert.Equal(-32602, refused["error"]!["code"]!.GetValue<int>());
        }
        var missing = await client.RequestAsync("session/set_config_option", new { sessionId = "acp-nope", configId = "model", value = "fake-gen" });
        Assert.Equal(-32002, missing["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ASwitchMadeElsewhereReachesTheClientAsAnUpdate()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        // /model through the prompt: the slot moved without session/set_config_option, so the client is told.
        await client.RequestAsync("session/prompt", Prompt(session, "/model 2"));
        var update = await client.WaitForUpdateAsync(session, item => item["sessionUpdate"]!.GetValue<string>() == "config_option_update");
        Assert.Equal("fake-gen-large", Option(update["configOptions"]!, "model")["currentValue"]!.GetValue<string>());
    }
}
