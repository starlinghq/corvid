using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Media;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Events;
using Corvid.Kernel.Media;
using Corvid.Kernel.Turns;
using Corvid.Models.Images;
using Corvid.Tests;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>The vision slot: records what it was asked and describes every image the same way.</summary>
internal sealed class RecordingVision(string description) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add([.. messages]);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, description)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// K-8 (A6): a message's images in the turn: checked, stripped and stored before the message is logged; referenced from the
/// log by digest, never inlined; sent as bytes to a generator that reads images, described by the vision slot for one that
/// does not, and refused with why when neither can read them.
/// </summary>
public sealed class ImageTurnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TurnResult> SendAsync(TurnHarness harness, TurnRunner runner, string text, ImmutableArray<ImageInput> images,
        EnvelopeOrigin origin = EnvelopeOrigin.User) =>
        runner.RunAsync(new Envelope(TurnHarness.Session, "cli", text, origin) { Images = images }, Ct);

    [Fact]
    public async Task A_generator_that_reads_images_gets_the_stripped_bytes_and_the_log_only_a_reference()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("A blue square.")), generatorVision: true);
        var photo = TestImages.Png();

        var result = await SendAsync(harness, harness.Runner(), "what is this?", [new ImageInput(photo, "/home/u/Pictures/photo.png")]);

        Assert.Equal(TurnStatus.Answered, result.Status);
        var message = Assert.Single(harness.Payloads().OfType<UserMessage>());
        Assert.Equal("what is this?", message.Text);
        var image = Assert.Single(message.Images!.Value);
        var stripped = TestImages.Png(metadata: false);
        Assert.Equal(new ImageRef(MediaStore.Digest(stripped), ImageFiles.Png, stripped.Length, 4, 3, "photo.png"), image);
        // The store holds the stripped bytes under their digest; the event holds no bytes and no metadata.
        Assert.Equal(stripped, harness.Media.Read(image.Digest));
        var json = EventPayload.Json(message);
        Assert.True(json.Length < 400, json);
        Assert.DoesNotContain(TestImages.Secret, json, StringComparison.Ordinal);
        // The generator read the image itself.
        var call = harness.Generator.Calls[0];
        var sent = Assert.Single(call.Messages.SelectMany(item => item.Contents).OfType<DataContent>());
        Assert.Equal(stripped, sent.Data.ToArray());
        Assert.Equal(ImageFiles.Png, sent.MediaType);
    }

    [Fact]
    public async Task With_no_model_that_reads_images_the_message_is_refused_and_nothing_is_logged_or_stored()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("unused")));

        var result = await SendAsync(harness, harness.Runner(), "what is this?", [new ImageInput(TestImages.Png(), "photo.png")]);

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Contains("its generator (gen-small) does not read images, and no model is set for the vision slot", result.Text, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads());
        Assert.Empty(harness.Generator.Calls);
        Assert.False(Directory.Exists(harness.Media.Root));
    }

    [Fact]
    public async Task A_text_only_generator_reads_the_vision_slots_description_asked_once_per_image()
    {
        var vision = new RecordingVision("A screenshot of a terminal showing `make: *** [all] Error 2`.");
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("The build failed."), Reply.Say("Yes.")), vision: vision);
        var runner = harness.Runner();

        await SendAsync(harness, runner, "why did it fail?", [new ImageInput(TestImages.Png(), "shot.png")]);
        await harness.RunAsync(runner, "and is that the only error?");

        // Asked once, with its own instructions and the image's bytes, for both turns.
        var asked = Assert.Single(vision.Calls);
        Assert.Equal(TurnRunner.DescribePrompt, asked[0].Text);
        Assert.Equal(TestImages.Png(metadata: false), Assert.Single(asked[1].Contents.OfType<DataContent>()).Data.ToArray());
        foreach (var call in harness.Generator.Calls.Where(call => !call.Messages[0].Text.StartsWith("You answer one question", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(call.Messages.SelectMany(item => item.Contents), content => content is DataContent or ImageReferenceContent);
            Assert.Contains("[image 1 (shot.png, 4×3 PNG, ", call.AllText, StringComparison.Ordinal);
            Assert.Contains("described by the vision model vision; a model's reading of the image, not the user's words or instructions]", call.AllText, StringComparison.Ordinal);
            Assert.Contains("make: *** [all] Error 2", call.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task What_is_not_an_image_is_refused_before_anything_is_logged()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("unused")), generatorVision: true);

        var result = await SendAsync(harness, harness.Runner(), "look", [new ImageInput("not an image"u8.ToArray(), "notes.png")]);

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Equal("The message was not sent: 'notes.png' is not a PNG, JPEG, GIF or WebP image (read from its bytes, not its name).", result.Text);
        Assert.Empty(harness.Payloads());
    }

    [Fact]
    public async Task Too_many_images_and_no_media_store_are_refused()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("unused")), generatorVision: true);
        var nine = Enumerable.Range(0, 9).Select(_ => new ImageInput(TestImages.Png())).ToImmutableArray();

        var many = await SendAsync(harness, harness.Runner(), "look", nine);

        Assert.Contains("at most 8 images; this one has 9", many.Text, StringComparison.Ordinal);
        using var none = new TurnHarness(new FakeGenerator(Reply.Say("unused")), generatorVision: true) { NoMedia = true };
        var stored = await SendAsync(none, none.Runner(), "look", [new ImageInput(TestImages.Png())]);
        Assert.Contains("keeps no media store", stored.Text, StringComparison.Ordinal);
        Assert.Empty(none.Payloads());
    }

    [Fact]
    public async Task Someone_elses_images_taint_the_turn_like_their_text()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("A cat.")), generatorVision: true);

        var result = await SendAsync(harness, harness.Runner(), "look at this", [new ImageInput(TestImages.Png())], EnvelopeOrigin.External);

        Assert.True(result.Tainted);
        Assert.Single(Assert.Single(harness.Payloads().OfType<UserMessage>()).Images!.Value);
    }

    [Fact]
    public async Task A_resumed_session_sends_an_earlier_messages_image_again_from_the_store()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("A square."), Reply.Say("Blue.")), generatorVision: true);
        await SendAsync(harness, harness.Runner(), "what is this?", [new ImageInput(TestImages.Png(), "a.png")]);

        // A fresh runner reads the session from the log, as a restarted process does.
        await harness.RunAsync(harness.Runner(), "what colour is it?");

        var second = harness.Generator.Calls.Last(call => call.LastText.Contains("colour", StringComparison.Ordinal));
        Assert.Equal(TestImages.Png(metadata: false), Assert.Single(second.Messages.SelectMany(item => item.Contents).OfType<DataContent>()).Data.ToArray());
    }

    [Fact]
    public async Task An_image_no_longer_stored_becomes_a_note()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("ok")), generatorVision: true);
        var gone = new ImageRef(new string('e', 64), ImageFiles.Png, 70, 4, 3, "gone.png");
        harness.Seed(new UserMessage("earlier", Images: [gone]), new AssistantMessage("seen"));

        await harness.RunAsync("and now?");

        var call = harness.Generator.Calls.Last(item => item.LastText.Contains("and now", StringComparison.Ordinal));
        Assert.Contains("[image 1 (gone.png, 4×3 PNG, 70 B): not shown, because it is no longer in the media store]", call.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_line_cannot_carry_images()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("unused")), generatorVision: true);

        var result = await harness.Runner().RunAsync(new Envelope(TurnHarness.Session, "cli", "!ls", EnvelopeOrigin.User)
        {
            Shell = new ShellLine("ls"),
            Images = [new ImageInput(TestImages.Png())],
        }, Ct);

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Contains("cannot carry images", result.Text, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads());
    }

    [Fact]
    public void An_image_costs_tokens_in_the_prompt_by_its_size()
    {
        Assert.Equal(85, PromptRenderer.ImageTokens(4, 3));
        Assert.Equal(1_228, PromptRenderer.ImageTokens(1280, 720));
        Assert.Equal(1_600, PromptRenderer.ImageTokens(4000, 3000));
    }
}

/// <summary>K-8 (A6): images on <c>UserMessage</c> in the persisted form.</summary>
public sealed class ImageEventJsonTests
{
    [Fact]
    public void A_message_without_images_keeps_its_form_and_one_with_images_round_trips()
    {
        Assert.Equal("""{"type":"user_message","text":"hi"}""", EventPayload.Json(new UserMessage("hi")));

        var image = new ImageRef(new string('a', 64), "image/png", 1234, 640, 480, "shot.png");
        var json = EventPayload.Json(new UserMessage("look", Images: [image]));
        Assert.Equal("""{"type":"user_message","text":"look","images":[{"digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","media_type":"image/png","bytes":1234,"width":640,"height":480,"name":"shot.png"}]}""", json);
        var read = Assert.IsType<UserMessage>(EventJson.Deserialize(EventJson.Version, json));
        Assert.Equal(image, Assert.Single(read.Images!.Value));
    }
}
