using Corvid.Host.Tests;
using Corvid.Models;
using Corvid.Tests;
using Microsoft.Extensions.AI;

namespace Corvid.Cli.Tests;

/// <summary>K-8 (A6): <c>/image &lt;path&gt;</c> and <c>@path</c> in <c>corvid chat</c>.</summary>
public sealed class ChatImageTests
{
    private static (FakeModels Models, FakeGenerator Vlm) Vision(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script)
    {
        var vlm = new FakeGenerator(script);
        var models = new FakeModels();
        models.Provider.Add("fake-vlm", [ModelSlots.Generator, ModelSlots.Vision], () => vlm, vision: true);
        return (models, vlm);
    }

    private static IEnumerable<byte[]> SentImages(FakeGenerator generator, int call) =>
        generator.Calls[call].SelectMany(message => message.Contents).OfType<DataContent>().Select(data => data.Data.ToArray());

    [Fact]
    public async Task An_image_staged_with_slash_image_goes_with_the_next_message_only()
    {
        var (models, vlm) = Vision(Reply.Say("A blue picture."), Reply.Say("Nothing new."));
        using var run = new CliRun(models);
        var photo = Path.Combine(run.Root, "photo.png");
        File.WriteAllBytes(photo, TestImages.Png());

        await run.RunAsync(["chat", "--set", "Models:Slots:generator:Model=fake-vlm"], "/image", $"/image {photo}", "/image", "what is this?", "and now?", "/exit");

        Assert.Contains("No image is staged.", run.Output, StringComparison.Ordinal);
        Assert.Contains("Staged photo.png (4×3 PNG,", run.Output, StringComparison.Ordinal);
        Assert.Contains("↳ with photo.png (4×3 PNG,", run.Output, StringComparison.Ordinal);
        Assert.Equal([TestImages.Png(metadata: false)], SentImages(vlm, 0));
        // The second message carries none of its own; the first one's image is in the history it sends.
        Assert.Single(SentImages(vlm, 1));
        Assert.Equal(2, vlm.Calls.Count);
    }

    [Fact]
    public async Task An_at_reference_attaches_a_workspace_image_and_a_bad_one_sends_nothing()
    {
        var (models, vlm) = Vision(Reply.Say("A diagram."));
        using var run = new CliRun(models);
        File.WriteAllBytes(Path.Combine(run.Root, "work", "diagram.webp"), TestImages.Webp());

        await run.RunAsync(["chat", "--set", "Models:Slots:generator:Model=fake-vlm"], "explain @missing.png", "explain @diagram.webp", "/exit");

        Assert.Contains("@missing.png: there is no such file.", run.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", run.Output, StringComparison.Ordinal);
        var call = Assert.Single(vlm.Calls);
        Assert.Equal("explain @diagram.webp", call.Last(message => message.Role == ChatRole.User).Text);
        Assert.Single(SentImages(vlm, 0));
    }

    [Fact]
    public async Task Without_a_model_that_reads_images_the_message_is_not_sent_and_the_image_stays_staged()
    {
        using var run = new CliRun();
        var photo = Path.Combine(run.Root, "photo.png");
        File.WriteAllBytes(photo, TestImages.Png());
        File.WriteAllBytes(Path.Combine(run.Root, "id_ed25519"), TestImages.Png());
        File.WriteAllText(Path.Combine(run.Root, "notes.png"), "not an image");

        await run.RunAsync(["chat"], $"/image {photo}", "what is this?", "/image", $"/image {Path.Combine(run.Root, "id_ed25519")}",
            $"/image {Path.Combine(run.Root, "notes.png")}", "/image clear", "/exit");

        Assert.Contains("does not read images, and no model is set for the vision slot", run.Output, StringComparison.Ordinal);
        Assert.Contains("Staged for your next message: photo.png", run.Output, StringComparison.Ordinal);
        Assert.Contains("is a credential file", run.Output, StringComparison.Ordinal);
        Assert.Contains("is not a PNG, JPEG, GIF or WebP image (read from its bytes)", run.Output, StringComparison.Ordinal);
        Assert.Contains("1 staged image dropped.", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Models!.Generator.Calls);
    }
}
