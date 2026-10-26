using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;
using Corvid.App.Core.Sessions;
using Corvid.Tests;

namespace Corvid.App.Core.Tests;

/// <summary>K-8 (A6), flow F19: images in the composer (paste, drop, attach) and in the transcript.</summary>
public sealed class SessionImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The demo's sessions with image support, as the engine has it: what is sent and read is recorded.</summary>
    private sealed class ImageService(DemoSessionService inner) : ISessionService
    {
        public List<(string Text, IReadOnlyList<ImageAttachment> Images)> Sent { get; } = [];

        public string? Refuse { get; set; }

        public Dictionary<string, byte[]> Stored { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public SessionUpdate? AfterOpen { get; set; }

        public bool CanSendImages => true;

        public Task SendAsync(string sessionId, string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken = default)
        {
            if (Refuse is { } reason)
                return Task.FromException(new InvalidOperationException(reason));
            Sent.Add((text, images));
            return Task.CompletedTask;
        }

        public Task<ImageAttachment> ReadImageFileAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Files.TryGetValue(path, out var bytes) ? Task.FromResult(new ImageAttachment(bytes, Path.GetFileName(path)))
                : Task.FromException<ImageAttachment>(new InvalidOperationException($"{path}: there is no such file."));

        public Task<byte[]?> ReadImageAsync(string sessionId, string digest, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.GetValueOrDefault(digest));

        public event EventHandler<SessionSummary>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public async Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
        {
            var subscription = await inner.OpenAsync(sessionId, onUpdate, cancellationToken);
            if (AfterOpen is { } update)
                onUpdate(update);
            return subscription;
        }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(sessionId, text, cancellationToken);
        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.QueueAsync(sessionId, text, cancellationToken);
        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.RemoveQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => inner.MoveQueuedAsync(sessionId, queuedId, index, cancellationToken);
        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.TakeBackQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SteerAsync(sessionId, text, cancellationToken);
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InterruptAsync(sessionId, cancellationToken);
        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) => inner.AnswerApprovalAsync(sessionId, approvalId, answer, cancellationToken);
        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) => inner.AnswerQuestionAsync(sessionId, questionId, answer, cancellationToken);
        public List<string> Commands { get; } = [];
        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
        {
            Commands.Add(line);
            return Task.CompletedTask;
        }
        public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default) => inner.SetApprovalModeAsync(sessionId, mode, cancellationToken);
        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => inner.SetGoalAsync(sessionId, goal, cancellationToken);
        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => inner.CompactAsync(sessionId, focus, cancellationToken);
        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => inner.ForkAsync(sessionId, throughPosition, cancellationToken);
        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => inner.RenameAsync(sessionId, title, cancellationToken);
    }

    private static async Task<(SessionViewModel View, ImageService Service, DemoSessionService Demo)> OpenAsync(Action<ImageService>? setup = null)
    {
        var time = ManualTimeProvider.Demo();
        var demo = new DemoSessionService(new InlineDispatcher(), time, DemoScene.Main);
        var service = new ImageService(demo);
        setup?.Invoke(service);
        var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);
        return (view, service, demo);
    }

    // A6 end-to-end: Uno's X11 clipboard lists a copied screenshot as the X target image/png, never as the Bitmap format.
    [Theory]
    [InlineData("TARGETS,image/png", "image/png")]
    [InlineData("TARGETS,image/jpeg,image/png", "image/png")]
    [InlineData("TARGETS,IMAGE/JPEG", "IMAGE/JPEG")]
    [InlineData("image/gif,image/webp", "image/webp")]
    [InlineData("TARGETS,image/tiff", null)]
    [InlineData("TARGETS,UTF8_STRING,text/plain", null)]
    [InlineData("", null)]
    public void A_clipboard_image_is_found_by_its_media_type(string formats, string? expected) =>
        Assert.Equal(expected, ImageSignature.ClipboardImageFormat(formats.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public async Task A_pasted_image_becomes_a_chip_and_goes_with_the_message()
    {
        var (view, service, demo) = await OpenAsync();
        using var _ = demo;
        using var __ = view;
        var png = TestImages.Png();

        Assert.True(view.Attach(new ImageAttachment(png)));
        var chip = Assert.Single(view.Attachments);
        Assert.Equal($"pasted image · PNG · {png.Length} B", chip.Label);
        Assert.True(view.HasAttachments);
        view.Draft = "what is this?";
        view.SubmitCommand.Execute(null);

        var (text, images) = Assert.Single(service.Sent);
        Assert.Equal("what is this?", text);
        Assert.Same(png, Assert.Single(images).Data);
        Assert.Empty(view.Attachments);
        Assert.Equal("", view.Draft);
    }

    [Fact]
    public async Task Images_alone_can_be_sent_and_a_refusal_gives_the_text_and_the_chips_back()
    {
        var (view, service, demo) = await OpenAsync(service => service.Refuse = "The message was not sent: its generator (qwen) does not read images.");
        using var _ = demo;
        using var __ = view;
        view.Attach(new ImageAttachment(TestImages.Png(), "a.png"));
        view.Attach(new ImageAttachment(TestImages.Jpeg(), "b.jpg"));
        Assert.True(view.CanSubmit);
        view.Draft = "compare";

        view.SubmitCommand.Execute(null);

        Assert.Equal("The message was not sent: its generator (qwen) does not read images.", view.Error);
        Assert.Equal("compare", view.Draft);
        Assert.Equal(["a.png · PNG", "b.jpg · JPEG"], view.Attachments.Select(chip => chip.Label[..chip.Label.LastIndexOf(" · ", StringComparison.Ordinal)]));

        service.Refuse = null;
        view.Draft = "";
        view.SubmitCommand.Execute(null);
        Assert.Equal("", Assert.Single(service.Sent).Text);
    }

    [Fact]
    public async Task What_is_not_an_image_too_many_and_a_service_without_images_are_refused_at_once()
    {
        var (view, _, demo) = await OpenAsync();
        using var __ = demo;
        using var ___ = view;

        Assert.False(view.Attach(new ImageAttachment("#!/bin/sh"u8.ToArray(), "run.png")));
        Assert.Equal("'run.png' is not a PNG, JPEG, GIF or WebP image.", view.Error);
        for (var i = 0; i < ImageSignature.MaxPerMessage; i++)
            Assert.True(view.Attach(new ImageAttachment(TestImages.Gif())));
        Assert.False(view.Attach(new ImageAttachment(TestImages.Gif())));
        Assert.Equal("A message may carry at most 8 images.", view.Error);

        using var demoOnly = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo(), DemoScene.Main);
        using var plain = new SessionViewModel(DemoSessionService.VaultSessionId, demoOnly, new InlineDispatcher(), ManualTimeProvider.Demo());
        await plain.OpenAsync(Ct);
        Assert.False(plain.CanAttachImages);
        Assert.False(plain.Attach(new ImageAttachment(TestImages.Png())));
        Assert.Equal("Images in messages come with the engine (K-8).", plain.Error);
    }

    [Fact]
    public async Task A_command_never_takes_the_images_which_wait_for_the_next_message()
    {
        var (view, service, demo) = await OpenAsync();
        using var _ = demo;
        using var __ = view;
        view.Attach(new ImageAttachment(TestImages.Png()));

        view.Draft = "/status";
        view.SubmitCommand.Execute(null);

        Assert.Equal(["/status"], service.Commands);
        Assert.Single(view.Attachments);
        Assert.Empty(service.Sent);
    }

    [Fact]
    public async Task A_dropped_image_is_read_through_the_service_and_any_other_file_becomes_an_at_reference()
    {
        var (view, service, demo) = await OpenAsync(service => service.Files["/home/u/shot.webp"] = TestImages.Webp());
        using var _ = demo;
        using var __ = view;

        await view.AttachFileAsync("/home/u/shot.webp");
        await view.AttachFileAsync("/home/u/src/main.cs");
        await view.AttachFileAsync("/home/u/my notes.md");
        await view.AttachFileAsync("/home/u/gone.png");

        Assert.StartsWith("shot.webp · WebP · ", Assert.Single(view.Attachments).Label, StringComparison.Ordinal);
        Assert.Equal("@/home/u/src/main.cs @\"/home/u/my notes.md\" ", view.Draft);
        Assert.Equal("/home/u/gone.png: there is no such file.", view.Error);

        await view.AttachFileAsync("/home/u/src/main.cs", imagesOnly: true);
        Assert.Equal("'main.cs' is not a PNG, JPEG, GIF or WebP file.", view.Error);
    }

    [Fact]
    public async Task A_messages_images_show_under_it_as_bounded_thumbnails_read_through_the_service()
    {
        var digest = new string('f', 64);
        var image = new MessageImage(digest, "image/png", 86_000, 1920, 1080, "screen‮.png");
        var (view, service, demo) = await OpenAsync(service =>
        {
            service.Stored[digest] = TestImages.Png();
            service.AfterOpen = new UserMessage("look") { Images = [image] };
        });
        using var _ = demo;
        using var __ = view;

        var block = view.Blocks.OfType<UserMessageBlock>().Last();
        var shown = Assert.Single(block.Images);
        Assert.True(block.HasImages);
        // The name is outside text: its hidden override is cleaned away for display.
        Assert.Equal("screen.png · 1920×1080 PNG · 84 KB", shown.Caption);
        Assert.Equal((360, 202), (shown.DisplayWidth, shown.DisplayHeight));
        Assert.Contains("(1 image: screen", block.AutomationName, StringComparison.Ordinal);
        Assert.Equal(TestImages.Png(), await shown.LoadAsync(Ct));
    }

    [Fact]
    public void The_fold_carries_a_messages_images_into_its_entry()
    {
        var image = new MessageImage(new string('a', 64), "image/gif", 100, 2, 2);

        var state = SessionFold.Replay("s", [new UserMessage("look") { Images = [image] }]);

        Assert.Equal(image, Assert.Single(state.Transcript.OfType<UserEntry>().Single().Images));
    }
}
