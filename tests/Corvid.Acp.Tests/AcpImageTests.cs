using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.Host.Tests;
using Corvid.Models;
using Corvid.Tests;
using Microsoft.Extensions.AI;

namespace Corvid.Acp.Tests;

/// <summary>K-8 (A6): ACP image content blocks in <c>session/prompt</c> and in <c>session/load</c>'s replay.</summary>
public sealed class AcpImageTests
{
    private static object Initialize() => new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false } }, clientInfo = new { name = "test", version = "1" } };

    private static object ImagePrompt(string sessionId, string text, string data, string mimeType = "image/png", string? uri = null) =>
        new { sessionId, prompt = new object[] { new { type = "text", text }, uri is null ? new { type = "image", data, mimeType } : (object)new { type = "image", data, mimeType, uri } } };

    private static (FakeModels Models, FakeGenerator Vlm) VisionModels(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script)
    {
        var vlm = new FakeGenerator(script);
        var models = new FakeModels();
        models.Provider.Add("fake-vlm", [ModelSlots.Generator, ModelSlots.Vision], () => vlm, vision: true);
        return (models, vlm);
    }

    [Fact]
    public async Task An_image_block_reaches_a_generator_that_reads_images_and_session_load_replays_it()
    {
        var (models, vlm) = VisionModels(Reply.Say("A tiny blue picture."));
        await using var client = AcpClient.Start(TestHost.Create(models, new Dictionary<string, string?> { ["Corvid:Models:Slots:generator:Model"] = "fake-vlm" }));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var answer = await client.RequestAsync("session/prompt", ImagePrompt(session, "what is this?", Convert.ToBase64String(TestImages.Png()), uri: "file:///home/u/photo.png"));

        Assert.Equal("end_turn", answer["result"]!["stopReason"]!.GetValue<string>());
        var sent = Assert.Single(vlm.Calls[^1].SelectMany(message => message.Contents).OfType<DataContent>());
        Assert.Equal(TestImages.Png(metadata: false), sent.Data.ToArray());
        var runtime = await client.Runtime;
        var logged = Assert.Single(runtime.Log.Read(session, cancellationToken: TestContext.Current.CancellationToken).Select(item => item.Payload).OfType<UserMessage>());
        Assert.Equal("photo.png", Assert.Single(logged.Images!.Value).Name);

        var before = client.UpdatesFor(session).Count;
        await client.RequestAsync("session/load", new { sessionId = session, cwd = client.Workspace, mcpServers = Array.Empty<object>() });
        var replay = client.UpdatesFor(session).Skip(before).Where(update => update["sessionUpdate"]!.GetValue<string>() == "user_message_chunk").ToList();
        Assert.Equal(2, replay.Count);
        Assert.Equal("what is this?", replay[0]["content"]!["text"]!.GetValue<string>());
        Assert.Equal("image", replay[1]["content"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", replay[1]["content"]!["mimeType"]!.GetValue<string>());
        // The replay sends the stored image: stripped of its metadata.
        Assert.Equal(TestImages.Png(metadata: false), Convert.FromBase64String(replay[1]["content"]!["data"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Without_a_model_that_reads_images_the_prompt_answers_why()
    {
        await using var client = AcpClient.Start(TestHost.Create());
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var answer = await client.RequestAsync("session/prompt", ImagePrompt(session, "what is this?", Convert.ToBase64String(TestImages.Png())));

        Assert.Equal("end_turn", answer["result"]!["stopReason"]!.GetValue<string>());
        var said = client.UpdatesFor(session).Last(update => update["sessionUpdate"]!.GetValue<string>() == "agent_message_chunk");
        Assert.Contains("does not read images, and no model is set for the vision slot", said["content"]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var runtime = await client.Runtime;
        // Nothing posted: the log holds only where the session works (WorkspaceBound, A6 review).
        Assert.Equal(1, runtime.Log.GetHead(session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_image_block_without_data_or_with_bad_base64_is_invalid()
    {
        await using var client = AcpClient.Start(TestHost.Create());
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var uriOnly = await client.RequestAsync("session/prompt", new { sessionId = session, prompt = new object[] { new { type = "image", mimeType = "image/png", uri = "https://example.com/x.png" } } });
        var garbled = await client.RequestAsync("session/prompt", ImagePrompt(session, "x", "not base64!!"));

        Assert.Equal(-32602, uriOnly["error"]!["code"]!.GetValue<int>());
        Assert.Contains("never fetches an image", uriOnly["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(-32602, garbled["error"]!["code"]!.GetValue<int>());
    }
}
