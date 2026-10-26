using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Kernel.Media;
using Corvid.Models;
using Corvid.Tests;
using Microsoft.Extensions.AI;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// K-8 (A6), flow F19 on the engine: the composer's images are checked before they are queued, posted with the message,
/// stored stripped in the runtime's media store, shown in the transcript live and on replay, and read back by digest.
/// </summary>
public sealed class EngineImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeModels Models, FakeGenerator Vlm) Vision()
    {
        var vlm = new FakeGenerator(Reply.Say("A small blue picture."), Reply.Say("Still blue."));
        var models = new FakeModels();
        models.Provider.Add("fake-vlm", [ModelSlots.Generator, ModelSlots.Vision], () => vlm, vision: true);
        return (models, vlm);
    }

    private static readonly Dictionary<string, string?> VisionGenerator = new() { ["Corvid:Models:Slots:generator:Model"] = "fake-vlm" };

    [Fact]
    public async Task An_image_sent_from_the_composer_reaches_the_generator_and_shows_under_the_message_live_and_on_replay()
    {
        var (models, vlm) = Vision();
        await using var fixture = await EngineFixture.StartAsync(models, VisionGenerator);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        Assert.True(service.CanSendImages);
        await service.SendAsync(id, "what is this?", [new ImageAttachment(TestImages.Png(), "photo.png")], Ct);
        await service.IdleAsync();

        Assert.Equal(TestImages.Png(metadata: false), Assert.Single(vlm.Calls[^1].SelectMany(message => message.Contents).OfType<DataContent>()).Data.ToArray());
        var live = waiter.Updates.OfType<UserMessage>().Single();
        var image = Assert.Single(live.Images);
        Assert.Equal((MediaStore.Digest(TestImages.Png(metadata: false)), "image/png", 4, 3, "photo.png"),
            (image.Digest, image.MediaType, image.Width, image.Height, image.Name));
        Assert.Equal(TestImages.Png(metadata: false), await service.ReadImageAsync(id, image.Digest, Ct));
        Assert.Null(await service.ReadImageAsync(id, "../../etc/passwd", Ct));

        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, Ct);
        Assert.Equal(image, Assert.Single(reopened.History.OfType<UserMessage>().Single().Images));
    }

    [Fact]
    public async Task With_no_model_that_reads_images_the_send_is_refused_before_anything_is_queued()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        using var subscription = await service.OpenAsync(id, _ => { }, Ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendAsync(id, "what is this?", [new ImageAttachment(TestImages.Png())], Ct));

        Assert.Contains("does not read images, and no model is set for the vision slot", refused.Message, StringComparison.Ordinal);
        await service.IdleAsync();
        // Nothing queued or posted: the log holds only where the session works (WorkspaceBound, A6 review).
        Assert.Equal(1, fixture.Runtime.Log.GetHead(id, Ct));
    }

    [Fact]
    public async Task A_mode_line_cannot_carry_images_and_a_bad_image_is_named()
    {
        var (models, _) = Vision();
        await using var fixture = await EngineFixture.StartAsync(models, VisionGenerator);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        using var subscription = await service.OpenAsync(id, _ => { }, Ct);

        var shell = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(id, "!ls", [new ImageAttachment(TestImages.Png())], Ct));
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(id, "look", [new ImageAttachment("nope"u8.ToArray(), "x.gif")], Ct));

        Assert.Contains("cannot carry images", shell.Message, StringComparison.Ordinal);
        Assert.Contains("'x.gif' is not a PNG, JPEG, GIF or WebP image", bad.Message, StringComparison.Ordinal);
        // Nothing queued or posted: the log holds only where the session works (WorkspaceBound, A6 review).
        Assert.Equal(1, fixture.Runtime.Log.GetHead(id, Ct));
    }

    [Fact]
    public async Task A_dropped_file_is_read_under_the_hosts_rules()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var workspace = fixture.Runtime.Builtin.Environment.Workspace;
        File.WriteAllBytes(Path.Combine(workspace, "shot.png"), TestImages.Png());
        File.WriteAllBytes(Path.Combine(workspace, "server.key"), TestImages.Png());

        var read = await service.ReadImageFileAsync(id, Path.Combine(workspace, "shot.png"), Ct);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadImageFileAsync(id, Path.Combine(workspace, "server.key"), Ct));

        Assert.Equal("shot.png", read.Name);
        Assert.Equal(TestImages.Png(), read.Data);
        Assert.Contains("a credential file", refused.Message, StringComparison.Ordinal);
    }
}
