using System.Diagnostics;
using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Kernel.Media;
using Corvid.Models;
using Corvid.Tests;
using Microsoft.Extensions.AI;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// Where A6's groups meet over attached mode (G-1): an attached client sends a message with an image (K-8) that reaches the
/// engine's generator, sees it under the message and reads its thumbnail back; and starts sessions in a second workspace
/// or a new worktree (G-4), the worktree only after the person approves it on the card the client shows.
/// </summary>
public sealed class EngineAttachedMeetingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Workspaces = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Sessions:WorktreesDirectory"] = "{root}/worktrees",
    };

    [Fact]
    public async Task AnImageSentFromAnAttachedClientReachesTheGeneratorAndItsThumbnailIsReadBack()
    {
        var vlm = new FakeGenerator(Reply.Say("A small blue picture."));
        var models = new FakeModels();
        models.Provider.Add("fake-vlm", [ModelSlots.Generator, ModelSlots.Vision], () => vlm, vision: true);
        await using var fixture = await EngineFixture.StartAsync(models, new Dictionary<string, string?> { ["Corvid:Models:Slots:generator:Model"] = "fake-vlm" });
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new EngineAttachedTests.Served(fixture);
        var (client, _) = await served.ConnectAsync();
        await using var _client = client;
        var waiter = new SessionWaiter();
        using var subscription = await client.OpenAsync(id, waiter.Add, Ct);

        Assert.True(client.CanSendImages);
        await client.SendAsync(id, "what is this?", [new ImageAttachment(TestImages.Png(), "/home/someone/photo.png")], Ct);
        await waiter.WaitAsync<TurnEnded>();

        // The generator read the stripped image; the update crossed the wire with it (a kind the wire could not carry
        // would have been dropped), and the client reads its bytes back by digest.
        var stripped = TestImages.Png(metadata: false);
        Assert.Equal(stripped, Assert.Single(vlm.Calls[^1].SelectMany(message => message.Contents).OfType<DataContent>()).Data.ToArray());
        var image = Assert.Single(waiter.Updates.OfType<UserMessage>().Single().Images);
        Assert.Equal((MediaStore.Digest(stripped), "image/png", 4, 3, "photo.png"), (image.Digest, image.MediaType, image.Width, image.Height, image.Name));
        Assert.Equal(stripped, await client.ReadImageAsync(id, image.Digest, Ct));
        Assert.Null(await client.ReadImageAsync(id, "../../etc/passwd", Ct));
    }

    [Fact]
    public async Task AnAttachedClientsImageTheEngineRefusesGivesTheReasonAndQueuesNothing()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new EngineAttachedTests.Served(fixture);
        var (client, _) = await served.ConnectAsync();
        await using var _client = client;
        using var subscription = await client.OpenAsync(id, _ => { }, Ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(id, "what is this?", [new ImageAttachment(TestImages.Png())], Ct));
        Assert.Contains("no model is set for the vision slot", refused.Message, StringComparison.Ordinal);
        var notImage = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(id, "and this?", [new ImageAttachment("not an image at all"u8.ToArray(), "notes.png")], Ct));
        Assert.NotEmpty(notImage.Message);
        await served.Service.IdleAsync();
        Assert.Equal(0, fixture.Runtime.Log.GetHead(id, Ct));
    }

    [Fact]
    public async Task AnAttachedClientStartsASessionInASecondWorkspaceAndTheEngineChecksThePath()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Workspaces);
        var other = Directory.CreateDirectory(Path.Combine(fixture.Host.Root, "other")).FullName;
        await using var served = new EngineAttachedTests.Served(fixture);
        var (client, _) = await served.ConnectAsync();
        await using var _client = client;

        Assert.True(client.CanChooseWorkspace);
        var id = await client.CreateAsync(new NewSessionRequest(other), Ct);

        Assert.Equal(other, fixture.Runtime.WorkspaceOf(id).Path);
        Assert.Equal(other, (await client.ListAsync(Ct)).Single(summary => summary.Id == id).Workspace);
        var relative = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.CreateAsync(new NewSessionRequest("src"), Ct));
        Assert.Contains("absolute path", relative.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageFileNamedInASessionOfAnotherWorkspaceIsThatWorkspacesFile()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Workspaces);
        var other = Directory.CreateDirectory(Path.Combine(fixture.Host.Root, "other")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(other, "shot.png"), TestImages.Png(), Ct);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Runtime.DefaultWorkspace.Root, "shot.png"), TestImages.Gif(), Ct);
        using var service = fixture.Sessions();
        var elsewhere = await service.CreateAsync(new NewSessionRequest(other), Ct);
        var home = await service.CreateAsync(Ct);

        Assert.Equal(TestImages.Png(), (await service.ReadImageFileAsync(elsewhere, "shot.png", Ct)).Data);
        Assert.Equal(TestImages.Gif(), (await service.ReadImageFileAsync(home, "shot.png", Ct)).Data);
    }

    [Fact]
    public async Task AnAttachedClientReadsADroppedFileOnItsOwnDeviceNotTheEngines()
    {
        var models = new FakeModels();
        models.Provider.Add("fake-vlm", [ModelSlots.Generator, ModelSlots.Vision], () => new FakeGenerator(), vision: true);
        await using var fixture = await EngineFixture.StartAsync(models, new Dictionary<string, string?> { ["Corvid:Models:Slots:generator:Model"] = "fake-vlm" });
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new EngineAttachedTests.Served(fixture);
        var (client, _) = await served.ConnectAsync();
        await using var _client = client;
        var here = Directory.CreateTempSubdirectory("corvid-drop-").FullName;
        try
        {
            var dropped = Path.Combine(here, "drop.png");
            await File.WriteAllBytesAsync(dropped, TestImages.Png(), Ct);
            await File.WriteAllTextAsync(Path.Combine(here, "fake.png"), "not an image", Ct);

            var image = await client.ReadImageFileAsync(id, dropped, Ct);
            Assert.Equal(TestImages.Png(), image.Data);
            Assert.Equal("drop.png", image.Name);
            var notImage = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadImageFileAsync(id, Path.Combine(here, "fake.png"), Ct));
            Assert.Contains("not a PNG, JPEG, GIF or WebP image", notImage.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadImageFileAsync(id, Path.Combine(here, "missing.png"), Ct));
            var relative = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadImageFileAsync(id, "drop.png", Ct));
            Assert.Contains("not a full path", relative.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(here, recursive: true);
        }
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var argument in new[] { "-c", "user.name=User", "-c", "user.email=user@example.com", "-c", "init.defaultBranch=main" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    [Fact]
    public async Task AWorktreeAnAttachedClientAsksForIsMadeOnlyAfterItsCardIsApproved()
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "git"))))
            Assert.Skip("git is not installed");
        await using var fixture = await EngineFixture.StartAsync(null, Workspaces);
        var work = fixture.Runtime.DefaultWorkspace.Root;
        Git(work, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "hello\n", Ct);
        Git(work, "add", "README.md");
        Git(work, "commit", "-q", "-m", "first");
        await using var served = new EngineAttachedTests.Served(fixture);
        var (client, _) = await served.ConnectAsync();
        await using var _client = client;
        var asking = await client.CreateAsync(Ct);
        var waiter = new SessionWaiter();
        using var subscription = await client.OpenAsync(asking, waiter.Add, Ct);

        var creating = client.CreateAsync(new NewSessionRequest { Worktree = true, AskIn = asking, WorktreeName = "feature" }, Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>();

        // The card is the client's to answer; until then nothing is made.
        Assert.False(creating.IsCompleted);
        Assert.DoesNotContain("corvid/feature", Git(work, "branch", "--list"), StringComparison.Ordinal);
        await client.AnswerApprovalAsync(asking, card.ApprovalId, new ApprovalAnswer(ApprovalChoice.Once), Ct);
        var id = await creating.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var summary = (await client.ListAsync(Ct)).Single(item => item.Id == id);
        Assert.Equal("corvid/feature", summary.Branch);
        Assert.True(File.Exists(Path.Combine(summary.Workspace, "README.md")));
    }
}
