using Corvid.Abstractions.Events;
using Corvid.Abstractions.Media;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Kernel.Media;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Models.Images;
using Corvid.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// K-8 (A6) in the composition: a session's message with images reaches a generator that reads them through the host's
/// bindings (stripped, stored in the data directory's media store, replayed with the message), a text-only generator reads
/// the vision slot's description, and a session where nothing reads images refuses before anything is posted.
/// </summary>
public sealed class ImageHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeModels WithVision(FakeGenerator vlm)
    {
        var models = new FakeModels();
        models.Provider.Add("fake-vlm", [ModelSlots.Generator, ModelSlots.Vision], () => vlm, vision: true);
        return models;
    }

    [Fact]
    public async Task A_generator_that_reads_images_gets_them_stripped_and_the_replay_names_them()
    {
        var vlm = new FakeGenerator(Reply.Say("A small blue picture."));
        await using var host = TestHost.Create(WithVision(vlm), new Dictionary<string, string?> { ["Corvid:Models:Slots:generator:Model"] = "fake-vlm" });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        var delivery = await session.SendAsync("what is this?", [new ImageInput(TestImages.Png(), "photo.png")], Ct);

        Assert.Equal(TurnStatus.Answered, delivery.Result!.Status);
        var sent = Assert.Single(vlm.Calls[^1].SelectMany(message => message.Contents).OfType<DataContent>());
        Assert.Equal(TestImages.Png(metadata: false), sent.Data.ToArray());
        // Stored owner-only under the data directory, by digest.
        var digest = MediaStore.Digest(TestImages.Png(metadata: false));
        Assert.StartsWith(Path.Combine(host.Root, "data", "media"), runtime.Media.Root, StringComparison.Ordinal);
        Assert.NotNull(runtime.Media.Read(digest));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(runtime.Media.Root));
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        var prompt = Assert.Single(replay.Updates.OfType<PromptSent>());
        Assert.Equal(digest, Assert.Single(prompt.Images).Digest);
        Assert.Equal("photo.png", prompt.Images[0].Name);
    }

    [Fact]
    public async Task A_text_only_generator_reads_the_vision_slots_description_through_the_host()
    {
        var vlm = new FakeGenerator(Reply.Say("A chart with three rising bars."));
        var models = WithVision(vlm);
        await using var host = TestHost.Create(models, new Dictionary<string, string?> { ["Corvid:Models:Slots:vision:Model"] = "fake-vlm" });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        await session.SendAsync("summarise the chart", [new ImageInput(TestImages.Png(), "chart.png")], Ct);

        var asked = Assert.Single(vlm.Calls);
        Assert.Equal(TurnRunner.DescribePrompt, asked[0].Text);
        var turn = models.Generator.Calls[^1];
        Assert.DoesNotContain(turn.SelectMany(message => message.Contents), content => content is DataContent);
        Assert.Contains("A chart with three rising bars.", string.Join("\n", turn.Select(message => message.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_that_reads_images_the_check_refuses_before_posting()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        var refusal = session.CheckImages([new ImageInput(TestImages.Png())]);

        Assert.Contains("its generator (fake-gen@cpu) does not read images", refusal, StringComparison.Ordinal);
        Assert.Null(session.CheckImages([]));
        Assert.Equal(0, runtime.Log.GetHead(session.Id, Ct));
    }

    [Fact]
    public async Task The_check_names_a_bad_image_before_posting()
    {
        var vlm = new FakeGenerator(Reply.Say("unused"));
        await using var host = TestHost.Create(WithVision(vlm), new Dictionary<string, string?> { ["Corvid:Models:Slots:generator:Model"] = "fake-vlm" });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        Assert.Equal("The message was not sent: 'x.png' is not a PNG, JPEG, GIF or WebP image (read from its bytes, not its name).",
            session.CheckImages([new ImageInput("hello"u8.ToArray(), "x.png")]));
        Assert.Null(session.CheckImages([new ImageInput(TestImages.Jpeg(), "x.jpg")]));
    }

    [Fact]
    public async Task A_fallback_that_cannot_read_images_gets_a_note_not_the_bytes()
    {
        // The vision generator fails; its fallback is text-only, and the host's binding guard turns the image into a note.
        var vlm = new FakeGenerator(_ => throw new HttpRequestException("the server went away"));
        var models = WithVision(vlm);
        await using var host = TestHost.Create(models, new Dictionary<string, string?>
        {
            ["Corvid:Models:Slots:generator:Model"] = "fake-vlm",
            ["Corvid:Models:Slots:generator:Fallbacks:0"] = "fake-gen",
        });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        await session.SendAsync("what is this?", [new ImageInput(TestImages.Png(), "photo.png")], Ct);

        var fallback = models.Generator.Calls[^1];
        Assert.DoesNotContain(fallback.SelectMany(message => message.Contents), content => content is DataContent);
        Assert.Contains("not shown, because this model (fake-gen@cpu) does not read images", string.Join("\n", fallback.Select(message => message.Text)),
            StringComparison.Ordinal);
    }
}

/// <summary>K-8 (A6): images named from a surface (<c>@path</c>, <c>/image</c>, a drop) under the file tools' rules.</summary>
public sealed class ImageAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-attach-" + Guid.NewGuid().ToString("N"));

    public ImageAttachmentTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "work", "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "home", "Pictures"));
        Directory.CreateDirectory(Path.Combine(_root, "vault"));
        File.WriteAllBytes(Path.Combine(_root, "work", "shot.png"), TestImages.Png());
        File.WriteAllBytes(Path.Combine(_root, "work", "docs", "my diagram.webp"), TestImages.Webp());
        File.WriteAllBytes(Path.Combine(_root, "home", "Pictures", "cat.jpg"), TestImages.Jpeg());
        File.WriteAllBytes(Path.Combine(_root, "vault", "key.png"), TestImages.Png());
        File.WriteAllBytes(Path.Combine(_root, "home", "id_rsa"), TestImages.Png());
    }

    private ApprovalEnvironment Environment() => new(Path.Combine(_root, "work"), Path.Combine(_root, "home"),
        ProtectedPaths.For(Path.Combine(_root, "home"), _ => null) with { Vault = [Path.Combine(_root, "vault")] });

    [Fact]
    public void References_are_image_names_after_an_at_sign_bare_or_quoted()
    {
        Assert.Equal(["shot.png", "docs/my diagram.webp", "a/b.JPG"],
            ImageAttachments.References("see @shot.png, and @\"docs/my diagram.webp\" (also @a/b.JPG). mail me@example.com @notes.txt"));
        Assert.Empty(ImageAttachments.References("email@host.png is not a reference"));
    }

    [Fact]
    public void Workspace_references_are_read_and_anything_outside_it_is_refused()
    {
        var (images, problems) = ImageAttachments.FromReferences("compare @shot.png with @\"docs/my diagram.webp\"", Environment());

        Assert.Equal(["shot.png", "my diagram.webp"], images.Select(image => image.Name));
        Assert.Equal(TestImages.Png(), images[0].Data);
        Assert.Empty(problems);

        var (none, refused) = ImageAttachments.FromReferences("@../home/Pictures/cat.jpg and @missing.png", Environment());
        Assert.Empty(none);
        Assert.Contains("outside the workspace", refused[0], StringComparison.Ordinal);
        Assert.Equal("@missing.png: there is no such file.", refused[1]);
    }

    [Fact]
    public void A_named_path_may_be_anywhere_but_a_protected_location_or_a_credential_file()
    {
        Assert.Equal("cat.jpg", ImageAttachments.FromPath("~/Pictures/cat.jpg", Environment()).Name);
        Assert.Equal("shot.png", ImageAttachments.FromPath("shot.png", Environment()).Name);

        Assert.Contains("inside a protected location", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath(Path.Combine(_root, "vault", "key.png"), Environment())).Message, StringComparison.Ordinal);
        Assert.Contains("a credential file", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath("~/id_rsa", Environment())).Message, StringComparison.Ordinal);
        Assert.Contains("there is no such file", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath("~/nope.png", Environment())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_into_a_protected_location_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        File.CreateSymbolicLink(Path.Combine(_root, "work", "innocent.png"), Path.Combine(_root, "vault", "key.png"));

        Assert.Contains("inside a protected location", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath("innocent.png", Environment())).Message, StringComparison.Ordinal);
        Assert.Contains("@innocent.png", ImageAttachments.FromReferences("@innocent.png", Environment()).Problems[0], StringComparison.Ordinal);
    }

    // A6 review: a link anywhere in the path (a directory's), not only the file's own, is followed before the check.
    [Fact]
    public void A_directory_link_into_a_protected_location_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "work", "pictures"), Path.Combine(_root, "vault"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "home", "Downloads"), Path.Combine(_root, "vault"));

        Assert.Contains("inside a protected location", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath("pictures/key.png", Environment())).Message, StringComparison.Ordinal);
        Assert.Contains("inside a protected location", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath("~/Downloads/key.png", Environment())).Message, StringComparison.Ordinal);
    }

    // A6 review: only a regular file is opened, so a FIFO (whose open would wait for a writer) or a device is refused at once.
    [Fact]
    public async Task A_pipe_or_a_device_is_refused_without_being_opened()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var fifo = Path.Combine(_root, "work", "pipe.png");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [fifo]))
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(fifo));

        // Run off the test's thread with a bound, so a regression fails instead of hanging the run.
        var refused = await Task.Run(() => Assert.Throws<ImageRefusedException>(() => ImageAttachments.FromPath(fifo, Environment())),
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains("not a regular file", refused.Message, StringComparison.Ordinal);
        Assert.Contains("@pipe.png: it is empty or not a regular file", Assert.Single(ImageAttachments.FromReferences("@pipe.png", Environment()).Problems),
            StringComparison.Ordinal);
        Assert.Contains("not a regular file", Assert.Throws<ImageRefusedException>(() =>
            ImageAttachments.FromPath("/dev/zero", Environment())).Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

/// <summary>K-8 (A6): which models read images and which may be sent them, from configuration.</summary>
public sealed class VisionConfigurationTests
{
    [Fact]
    public void A_model_reads_images_when_it_says_so_or_serves_the_vision_slot()
    {
        var provider = new RemoteModelProvider(new Dictionary<string, RemoteModelSettings>
        {
            ["llava"] = new() { Provider = "openai-compatible", Model = "llava", Endpoint = "http://127.0.0.1:8080/v1", Vision = true },
            ["describer"] = new() { Provider = "openai-compatible", Model = "m", Endpoint = "http://127.0.0.1:8081/v1", Slots = [ModelSlots.Vision] },
            ["plain"] = new() { Provider = "openai-compatible", Model = "m", Endpoint = "http://127.0.0.1:8082/v1" },
        }, vaultSocket: "/nonexistent/vault.sock");

        var vision = provider.Models.ToDictionary(model => model.Name, model => model.Profile.Vision);
        Assert.Equal(new Dictionary<string, bool> { ["describer"] = true, ["llava"] = true, ["plain"] = false }, vision);
    }

    [Fact]
    public async Task Images_go_to_a_gated_model_only_where_the_privacy_settings_or_the_model_allow_them()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Corvid:Privacy:LocalServer"] = "Redact",
            ["Corvid:Models:Remote:open:Provider"] = "openai-compatible",
            ["Corvid:Models:Remote:open:Model"] = "m",
            ["Corvid:Models:Remote:open:Endpoint"] = "http://127.0.0.1:8080/v1",
            ["Corvid:Models:Remote:open:Egress:Images"] = "true",
            ["Corvid:Models:Remote:closed:Provider"] = "openai-compatible",
            ["Corvid:Models:Remote:closed:Model"] = "m",
            ["Corvid:Models:Remote:closed:Endpoint"] = "http://127.0.0.1:8081/v1",
        };
        await using var host = TestHost.Create(settings: settings, services: services =>
            services.AddSingleton<IModelProvider>(provider => new RemoteModelProvider(provider.GetRequiredService<IConfiguration>(), network: null)));
        await host.Host.StartAsync(TestContext.Current.CancellationToken);
        var catalogue = host.Services.GetRequiredService<ModelCatalogue>();
        using var devices = new DeviceSchedulers();
        using var composer = new ModelComposer(catalogue, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorvidOptions>>().Value,
            devices, new Telemetry.ModelMetrics(), new Telemetry.TurnFeed());

        Assert.True(composer.PolicyFor(catalogue.Find("open").Profile)!.Images);
        Assert.False(composer.PolicyFor(catalogue.Find("closed").Profile)!.Images);
    }
}
