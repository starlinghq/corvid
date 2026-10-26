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

    // A6 review: no upper cap, since the full-size bytes are what is sent.
    [Fact]
    public void An_image_costs_tokens_in_the_prompt_by_its_size()
    {
        Assert.Equal(85, PromptRenderer.ImageTokens(4, 3));
        Assert.Equal(1_228, PromptRenderer.ImageTokens(1280, 720));
        Assert.Equal(16_000, PromptRenderer.ImageTokens(4000, 3000));
    }

    // A6 review (M5): a turn with several model calls reads, checks and hashes each image once.
    [Fact]
    public async Task A_turn_reads_each_image_from_the_store_once_however_many_model_calls_it_makes()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":5}"""), Reply.Call("list_notes", """{"limit":6}"""), Reply.Say("Two notes."));
        using var harness = new TurnHarness(generator, generatorVision: true) { MediaStore = new CountingStore() };
        var store = (CountingStore)harness.MediaStore!;
        store.Inner = harness.Media;

        var result = await SendAsync(harness, harness.Runner(), "list my notes and look at this", [new ImageInput(TestImages.Png(), "a.png")]);

        Assert.Equal(TurnStatus.Answered, result.Status);
        var digest = MediaStore.Digest(TestImages.Png(metadata: false));
        var withImage = generator.Calls.Count(call => call.Messages.SelectMany(item => item.Contents).OfType<DataContent>().Any());
        Assert.True(withImage >= 3, $"{withImage} model calls carried the image");
        Assert.Equal(1, store.Reads(digest));
    }

    // A6 review (M5): only the latest TurnOptions.ImageTurns messages' images are sent; an older one is a stub and is not read.
    [Fact]
    public async Task An_image_older_than_the_latest_image_turns_is_a_stub_and_is_not_read_again()
    {
        var generator = new FakeGenerator(Reply.Say("A square."), Reply.Say("Blue."), Reply.Say("Yes."));
        using var harness = new TurnHarness(generator, generatorVision: true, options: TurnOptions.Default with { ImageTurns = 2 })
        {
            MediaStore = new CountingStore(),
        };
        var store = (CountingStore)harness.MediaStore!;
        store.Inner = harness.Media;
        var runner = harness.Runner();
        await SendAsync(harness, runner, "what is this?", [new ImageInput(TestImages.Png(), "a.png")]);
        await harness.RunAsync(runner, "what colour is it?");
        var digest = MediaStore.Digest(TestImages.Png(metadata: false));
        var readsBefore = store.Reads(digest);

        await harness.RunAsync(runner, "is it square?");

        var third = generator.Calls.Last(call => call.LastText.Contains("square?", StringComparison.Ordinal));
        Assert.DoesNotContain(third.Messages.SelectMany(item => item.Contents), content => content is DataContent or ImageReferenceContent);
        Assert.Contains("what is this?\n[image: a.png, 4×3; shown in an earlier turn]", third.AllText, StringComparison.Ordinal);
        Assert.Equal(readsBefore, store.Reads(digest));
        var second = generator.Calls.Last(call => call.LastText.Contains("colour", StringComparison.Ordinal));
        Assert.Single(second.Messages.SelectMany(item => item.Contents).OfType<DataContent>());
    }

    [Fact]
    public void A_pasted_image_stub_names_its_kind_and_the_render_counts_the_stub_as_text()
    {
        var pasted = new ImageRef(new string('b', 64), ImageFiles.Jpeg, 9_000, 4000, 3000);
        var snapshot = SessionProjection.Build(TurnHarness.Session,
        [
            Stored(1, new UserMessage("look", Images: [pasted])),
            Stored(2, new AssistantMessage("A photo.")),
            Stored(3, new UserMessage("thanks")),
        ]);

        var all = PromptRenderer.Render(snapshot, "system", Corvid.Fabric.CharsPerTokenCounter.Instance, 100_000);
        var recent = PromptRenderer.Render(snapshot, "system", Corvid.Fabric.CharsPerTokenCounter.Instance, 100_000, imageTurns: 1);

        Assert.Contains("[image: a pasted JPEG, 4000×3000; shown in an earlier turn]", string.Join("\n", recent.Messages.Select(message => message.Text)), StringComparison.Ordinal);
        Assert.True(all.Tokens - recent.Tokens > 15_000, $"{all.Tokens} against {recent.Tokens}");
    }

    // A6 review (M5): the summarizer is told a message had images, by name.
    [Fact]
    public void A_compaction_line_names_the_messages_images()
    {
        var entry = new TranscriptEntry(7, 7, new UserMessage("compare these", Images:
            [new ImageRef(new string('a', 64), ImageFiles.Png, 10, 4, 3, "before.png"), new ImageRef(new string('c', 64), ImageFiles.Gif, 10, 2, 2)]));

        var line = Corvid.Kernel.Context.ContextCompactor.Line(entry, [], ImmutableDictionary<string, ResultView>.Empty);

        Assert.Equal("[7] user: compare these [image: before.png] [image: a pasted GIF]", line);
    }

    // A6 review: the vision slot counts only when its egress policy lets it be sent images; a gated describer with
    // Images: false no longer takes the message only to drop its image.
    [Fact]
    public async Task A_vision_model_whose_egress_policy_refuses_images_does_not_make_a_message_with_images_acceptable()
    {
        var vision = new RecordingVision("unused");
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("unused")), vision: vision)
        {
            EgressPolicyFor = profile => profile.Id == "vision" ? new Corvid.Models.Privacy.EgressPolicy { Action = Corvid.Models.Privacy.EgressAction.Send } : null,
        };

        var result = await SendAsync(harness, harness.Runner(), "what is this?", [new ImageInput(TestImages.Png(), "a.png")]);

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Contains("the vision slot's model (vision) may not be sent images under its egress policy", result.Text, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads());
        Assert.Empty(vision.Calls);
        Assert.Null(TurnRunner.ImageRefusal(harness.Slots, harness.Media,
            profile => new Corvid.Models.Privacy.EgressPolicy { Action = Corvid.Models.Privacy.EgressAction.Send, Images = true }));
    }

    // A6 review: a gated generator that reads images but may not be sent them gets the vision slot's description instead.
    [Fact]
    public async Task A_generator_that_may_not_be_sent_images_gets_the_vision_slots_description()
    {
        var vision = new RecordingVision("A blue square.");
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Blue.")), generatorVision: true, vision: vision)
        {
            EgressPolicyFor = profile => profile.Id == "gen-small" ? new Corvid.Models.Privacy.EgressPolicy { Action = Corvid.Models.Privacy.EgressAction.Send } : null,
        };

        var result = await SendAsync(harness, harness.Runner(), "what colour?", [new ImageInput(TestImages.Png(), "a.png")]);

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Single(vision.Calls);
        var call = harness.Generator.Calls.Last(item => item.LastText.Contains("what colour", StringComparison.Ordinal));
        Assert.DoesNotContain(call.Messages.SelectMany(item => item.Contents), content => content is DataContent);
        Assert.Contains("A blue square.", call.AllText, StringComparison.Ordinal);
    }

    private static StoredEvent Stored(long sequence, SessionEvent payload) =>
        new(TurnHarness.Session, sequence, Guid.NewGuid(), DateTimeOffset.UnixEpoch, payload);

    /// <summary>A media store that counts reads by digest and passes everything to the harness's own.</summary>
    private sealed class CountingStore : IMediaStore
    {
        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        public IMediaStore? Inner { get; set; }

        public int Reads(string digest)
        {
            lock (_reads)
                return _reads.GetValueOrDefault(digest);
        }

        public string Put(ReadOnlySpan<byte> data) => Inner!.Put(data);

        public byte[]? Read(string digest)
        {
            lock (_reads)
                _reads[digest] = _reads.GetValueOrDefault(digest) + 1;
            return Inner!.Read(digest);
        }
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

/// <summary>K-8 (A6): the content-addressed media store.</summary>
public sealed class MediaStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-media-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // A6 review (nit): a file of the right length under the digest is not taken as stored unless its bytes are these.
    [Fact]
    public void A_damaged_file_under_the_digest_is_replaced_not_taken_as_stored()
    {
        var store = new MediaStore(_root);
        var bytes = TestImages.Png(metadata: false);
        var digest = MediaStore.Digest(bytes);
        var path = Path.Combine(_root, "sha256", digest[..2], digest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes.Length]);
        Assert.Null(store.Read(digest));

        Assert.Equal(digest, store.Put(bytes));

        Assert.Equal(bytes, store.Read(digest));
        Assert.Equal(digest, store.Put(bytes));
        Assert.Equal(bytes, store.Read(digest));
    }
}
