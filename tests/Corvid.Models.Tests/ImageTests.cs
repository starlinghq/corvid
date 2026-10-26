using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Media;
using Corvid.Models.Images;
using Corvid.Models.Privacy;
using Corvid.Tests;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

/// <summary>K-8 (A6): reading and stripping images from their bytes, without an imaging library.</summary>
public sealed class ImageFileTests
{
    public static TheoryData<string, string, int, int> Fixtures => new()
    {
        { "png", ImageFiles.Png, 4, 3 },
        { "jpeg", ImageFiles.Jpeg, 640, 480 },
        { "gif", ImageFiles.Gif, 2, 2 },
        { "webp", ImageFiles.Webp, 16, 9 },
    };

    private static byte[] Fixture(string kind) => kind switch
    {
        "png" => TestImages.Png(),
        "jpeg" => TestImages.Jpeg(),
        "gif" => TestImages.Gif(),
        _ => TestImages.Webp(),
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_format_and_size_come_from_the_bytes(string kind, string mediaType, int width, int height)
    {
        var info = ImageFiles.Inspect(Fixture(kind));

        Assert.Equal(new ImageInfo(mediaType, width, height), info);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Metadata_is_stripped_and_the_picture_kept(string kind, string mediaType, int width, int height)
    {
        var original = Fixture(kind);
        Assert.True(TestImages.HoldsSecret(original));

        var (stripped, info) = ImageFiles.Prepare(original, "photo." + kind);

        Assert.False(TestImages.HoldsSecret(stripped));
        Assert.True(stripped.Length < original.Length);
        Assert.Equal(new ImageInfo(mediaType, width, height), info);
        // Still the same format with the same size, and stripping again changes nothing.
        Assert.Equal(info, ImageFiles.Inspect(stripped));
        Assert.Equal(stripped, ImageFiles.StripMetadata(stripped));
    }

    [Fact]
    public void A_png_keeps_its_drawing_chunks_and_loses_text_exif_and_time()
    {
        var stripped = ImageFiles.StripMetadata(TestImages.Png());

        var chunks = PngChunks(stripped);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks);
        Assert.Equal(TestImages.Png(metadata: false), stripped);
    }

    [Fact]
    public void A_jpeg_keeps_jfif_and_its_image_segments_and_loses_exif_xmp_and_comments()
    {
        var stripped = ImageFiles.StripMetadata(TestImages.Jpeg());

        Assert.Equal([0xE0, 0xDB, 0xC0, 0xDA], JpegMarkers(stripped));
        Assert.Equal([0xFF, 0xD9], stripped[^2..]);
    }

    [Fact]
    public void A_webp_loses_its_exif_and_xmp_chunks_and_their_flags_and_its_riff_size_is_right()
    {
        var stripped = ImageFiles.StripMetadata(TestImages.Webp());

        Assert.Equal(stripped.Length - 8, BitConverter.ToInt32(stripped, 4));
        Assert.Equal("VP8X", System.Text.Encoding.ASCII.GetString(stripped, 12, 4));
        Assert.Equal(0, stripped[20] & 0x0C);
        Assert.DoesNotContain("EXIF", System.Text.Encoding.ASCII.GetString(stripped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gif_keeps_its_loop_count_and_loses_comments_and_other_application_data()
    {
        var stripped = System.Text.Encoding.ASCII.GetString(ImageFiles.StripMetadata(TestImages.Gif()));

        Assert.Contains("NETSCAPE2.0", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("XMP DataXMP", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_not_an_image_is_refused_by_its_bytes_whatever_its_name()
    {
        var refused = Assert.Throws<ImageRefusedException>(() => ImageFiles.Prepare("#!/bin/sh\nrm -rf ~\n"u8.ToArray(), "cat.png"));

        Assert.Contains("'cat.png' is not a PNG, JPEG, GIF or WebP image", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ImageRefusedException>(() => ImageFiles.Prepare([], "empty.png"));
    }

    [Fact]
    public void A_malformed_image_is_refused_not_passed_on()
    {
        var png = TestImages.Png();
        // Cut inside the IDAT chunk: the walk finds a chunk running past the end.
        var cut = png[..(png.Length - 20)];

        var refused = Assert.Throws<ImageRefusedException>(() => ImageFiles.Prepare(cut, "cut.png"));

        Assert.Contains("could not be read as a PNG", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_declared_canvas_and_a_file_over_the_limit_are_refused()
    {
        var huge = TestImages.Png(width: 20_000, height: 2);
        Assert.Contains("20000×2 pixels", Assert.Throws<ImageRefusedException>(() => ImageFiles.Prepare(huge)).Message, StringComparison.Ordinal);

        var big = new byte[ImageFiles.MaxBytes + 1];
        TestImages.Png().CopyTo(big, 0);
        Assert.Contains("an image may be at most 20 MB", Assert.Throws<ImageRefusedException>(() => ImageFiles.Prepare(big)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_are_cleaned_of_directories_and_hidden_characters()
    {
        Assert.Equal("evil.png", ImageNames.Clean("/home/u/evil\u202E.png"));
        Assert.Equal("a (b).png", ImageNames.Clean("C:\\x\\a [b].png"));
        Assert.Null(ImageNames.Clean("\u200B"));
        Assert.Equal("image 2 (shot.png, 4×3 PNG, 84 KB)", ImageNames.Label(new ImageRef(new string('a', 64), ImageFiles.Png, 86_000, 4, 3, "shot.png"), 2));
    }

    private static List<string> PngChunks(byte[] data)
    {
        var chunks = new List<string>();
        for (var at = 8; at < data.Length;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            chunks.Add(System.Text.Encoding.ASCII.GetString(data, at + 4, 4));
            at += 12 + length;
        }
        return chunks;
    }

    private static List<int> JpegMarkers(byte[] data)
    {
        var markers = new List<int>();
        for (var at = 2; at < data.Length;)
        {
            var marker = data[at + 1];
            markers.Add(marker);
            if (marker == 0xDA)
                break;
            at += 2 + (data[at + 2] << 8 | data[at + 3]);
        }
        return markers;
    }
}

/// <summary>K-8 (A6): no model is sent an image it cannot read, and none is sent an image's metadata.</summary>
public sealed class ImageCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelProfile TextOnly = Profiles.Local("qwen");

    private static readonly ModelProfile Sees = Profiles.LocalServer("llava") with { Vision = true };

    private static List<ChatMessage> WithImage(byte[] bytes) =>
        [new(ChatRole.User, [new TextContent("what is in this?"), ImageContents.Data(new ImageReferenceContent(new ImageRef(new string('b', 64), ImageFiles.Png, bytes.Length, 4, 3, "shot.png"), 1), bytes)])];

    [Fact]
    public async Task A_model_that_reads_images_gets_the_bytes_without_metadata()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "a blue square")));
        var client = new ImageCapabilityChatClient(provider, Sees);

        await client.GetResponseAsync(WithImage(TestImages.Png()), cancellationToken: Ct);

        var sent = Assert.Single(provider.Calls[0][0].Contents.OfType<DataContent>());
        Assert.Equal(ImageFiles.Png, sent.MediaType);
        Assert.False(TestImages.HoldsSecret(sent.Data.Span));
        Assert.Equal(TestImages.Png(metadata: false), sent.Data.ToArray());
    }

    [Fact]
    public async Task A_text_only_model_gets_a_note_in_place_of_the_image()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new ImageCapabilityChatClient(provider, TextOnly);

        await client.GetResponseAsync(WithImage(TestImages.Png()), cancellationToken: Ct);

        var message = provider.Calls[0][0];
        Assert.DoesNotContain(message.Contents, content => content is DataContent);
        var size = ImageNames.Size(TestImages.Png().Length);
        Assert.Equal($"what is in this?[image 1 (shot.png, 4×3 PNG, {size}): not shown, because this model (qwen) does not read images]",
            string.Concat(message.Contents.OfType<TextContent>().Select(text => text.Text)));
    }

    [Fact]
    public async Task A_remote_image_and_an_unread_reference_are_never_passed_on()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new ImageCapabilityChatClient(provider, Sees);
        var reference = new ImageReferenceContent(new ImageRef(new string('c', 64), ImageFiles.Png, 10, 1, 1), 1);

        await client.GetResponseAsync([new(ChatRole.User, [new UriContent("https://example.com/x.png", "image/png"), reference])], cancellationToken: Ct);

        var texts = provider.Calls[0][0].Contents.Select(content => Assert.IsType<TextContent>(content).Text).ToList();
        Assert.Contains("a remote image is never fetched or passed on", texts[0], StringComparison.Ordinal);
        Assert.Contains("its bytes were not read for this request", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_without_images_pass_untouched()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new ImageCapabilityChatClient(provider, TextOnly);
        List<ChatMessage> messages = [new(ChatRole.User, "hello")];

        await client.GetResponseAsync(messages, cancellationToken: Ct);

        Assert.Same(messages[0], provider.Calls[0][0]);
    }
}

/// <summary>K-8 (A6): a gated model is sent an image only when its egress policy allows images.</summary>
public sealed class ImageEgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelProfile Cloud = Profiles.Cloud("claude") with { Vision = true };

    private static PrivacyEgress Egress(EgressPolicy policy) =>
        new(new FakePiiExtractor(("person", "Alice Smith")), new PrivacyEgressOptions { PolicyFor = profile => profile.Privacy == PrivacyClass.Cloud ? policy : null });

    private static List<ChatMessage> Request(string text) =>
        [new(ChatRole.User, [new TextContent(text), ImageContents.Data(new ImageReferenceContent(new ImageRef(new string('d', 64), ImageFiles.Png, 70, 4, 3, "letter.png"), 1), TestImages.Png(metadata: false))])];

    private static ChatOptions Session(bool tainted = false) => new ModelRequestContext("s1", tainted).Apply(new ChatOptions());

    [Theory]
    [InlineData(EgressAction.Send)]
    [InlineData(EgressAction.Redact)]
    [InlineData(EgressAction.Refuse)]
    public async Task An_image_is_refused_under_every_action_unless_the_policy_allows_images(EgressAction action)
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(EgressPolicy.Default with { Action = action }));

        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() => client.GetResponseAsync(Request("what is this?"), Session(), Ct));

        Assert.Contains("image 1 (letter.png, 4×3 PNG", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("does not allow images", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Allowed_images_pass_beside_redacted_text()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "a letter to <PERSON_1>")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(EgressPolicy.Default with { Images = true }));

        var response = await client.GetResponseAsync(Request("who is Alice Smith writing to?"), Session(tainted: true), Ct);

        var sent = provider.Calls[0][0];
        Assert.Equal("who is <PERSON_1> writing to?", Assert.IsType<TextContent>(sent.Contents[0]).Text);
        Assert.IsType<DataContent>(sent.Contents[1]);
        Assert.Equal("a letter to Alice Smith", response.Text);
    }

    [Fact]
    public async Task A_local_model_is_not_gated_so_its_images_pass()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Profiles.Local("vlm") with { Vision = true }, Egress(EgressPolicy.Default));

        await client.GetResponseAsync(Request("what is this?"), Session(), Ct);

        Assert.IsType<DataContent>(provider.Calls[0][0].Contents[1]);
    }
}

/// <summary>K-8 (A6): <see cref="ModelProfile.Vision"/> in the snapshots <c>ModelSwitched</c> records.</summary>
public sealed class VisionSnapshotTests
{
    [Fact]
    public void A_profile_without_vision_snapshots_as_before_and_an_old_snapshot_reads_as_text_only()
    {
        var profile = Profiles.Local("qwen");

        var snapshot = profile.ToSnapshot();

        Assert.False(snapshot.TryGetProperty("vision", out _));
        Assert.False(ModelProfile.FromSnapshot(snapshot).Vision);
    }

    [Fact]
    public void A_vision_profile_round_trips()
    {
        var profile = Profiles.LocalServer("llava") with { Vision = true };

        var snapshot = profile.ToSnapshot();

        Assert.True(snapshot.GetProperty("vision").GetBoolean());
        Assert.Equal(profile, ModelProfile.FromSnapshot(snapshot));
        // An older binary's reader skips unknown members; this one reads a snapshot from before as text only.
        var old = JsonSerializer.Deserialize<JsonElement>(snapshot.GetRawText().Replace(",\"vision\":true", "", StringComparison.Ordinal));
        Assert.False(ModelProfile.FromSnapshot(old).Vision);
    }
}
