using System.Diagnostics;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Review;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Review;
using Corvid.App.Engine.Rewind;
using Corvid.App.Engine.Sessions;
using ApprovalAnswer = Corvid.App.Core.Sessions.ApprovalAnswer;
using ApprovalOutcome = Corvid.App.Core.Sessions.ApprovalOutcome;
using ApprovalReasons = Corvid.Approvals.ApprovalReasons;
using ApprovalRequested = Corvid.App.Core.Sessions.ApprovalRequested;
using ApprovalResolved = Corvid.App.Core.Sessions.ApprovalResolved;
using AssistantMessage = Corvid.Abstractions.Events.AssistantMessage;
using ShellAction = Corvid.Approvals.ShellAction;
using ToolResult = Corvid.Abstractions.Events.ToolResult;
using UserMessage = Corvid.Abstractions.Events.UserMessage;
using Corvid.Host.Composition;
using Corvid.Host.Tests;
using AppToolCall = Corvid.App.Core.Sessions.ToolCall;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A6 (G-4): a workspace per session in the window. A session the window starts elsewhere is listed with its workspace,
/// its calls' paths are shown in it, Review and Rewind read its own files and checkpoints, and a new git worktree is made
/// only after the person approves it on the asking session's card, also outside a turn (the app's /workspace new).
/// </summary>
public sealed class EngineWorkspaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Builtin = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Sessions:WorktreesDirectory"] = "{root}/worktrees",
    };

    private static string Other(EngineFixture fixture)
    {
        var other = Path.Combine(fixture.Host.Root, "other");
        Directory.CreateDirectory(other);
        return other;
    }

    private static void Append(CorvidRuntime runtime, string sessionId, params SessionEvent[] events) =>
        runtime.Log.Append(sessionId, runtime.Log.GetHead(sessionId, Ct), [.. events.Select(EventData.Create)], Ct);

    /// <summary>One turn writing <paramref name="path"/> in the session's own workspace, recorded as the turn loop records it.</summary>
    private static async Task WriteTurnAsync(CorvidRuntime runtime, string sessionId, string callId, string path, string content)
    {
        var workspace = runtime.WorkspaceServicesOf(sessionId);
        var full = Path.Combine(workspace.Root, path);
        Append(runtime, sessionId, new UserMessage("write " + path),
            new ToolCallIssued(callId, "write_file", JsonSerializer.SerializeToElement(new { path = full, content })));
        var analysis = workspace.Approvals.Analyze(new ShellAction("rm " + full));
        var before = await workspace.Checkpoints!.BeforeAsync(callId, analysis);
        Append(runtime, sessionId, before);
        await File.WriteAllTextAsync(full, content, Ct);
        Append(runtime, sessionId, await workspace.Checkpoints.AfterAsync(callId, before, analysis),
            new ToolResult(callId, JsonSerializer.SerializeToElement(new { path = full })), new AssistantMessage("Written."));
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

    private static string Repository(EngineFixture fixture)
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "git"))))
            Assert.Skip("git is not installed");
        var work = fixture.Runtime.DefaultWorkspace.Root;
        Git(work, "init", "-q");
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Git(work, "add", "README.md");
        Git(work, "commit", "-q", "-m", "first");
        return work;
    }

    [Fact]
    public async Task ANewSessionInAnotherWorkspaceIsListedThereAndTheConfiguredOneRecordsNothing()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        var other = Other(fixture);
        using var service = fixture.Sessions();
        Assert.True(service.CanChooseWorkspace);

        var elsewhere = await service.CreateAsync(new NewSessionRequest(other), Ct);
        var home = await service.CreateAsync(new NewSessionRequest(fixture.Runtime.DefaultWorkspace.Root + "/"), Ct);
        await service.IdleAsync();

        var listed = await service.ListAsync(Ct);
        Assert.Equal(other, listed.Single(summary => summary.Id == elsewhere).Workspace);
        Assert.Equal(fixture.Runtime.DefaultWorkspace.Root, listed.Single(summary => summary.Id == home).Workspace);
        Assert.Equal(other, fixture.Runtime.WorkspaceOf(elsewhere).Path);
        // The window's own workspace is the default: nothing is recorded for it, as before A6.
        Assert.True(fixture.Runtime.WorkspaceOf(home).IsDefault);
        Assert.DoesNotContain(fixture.Runtime.Log.Read(home, 0, long.MaxValue, 64, Ct), stored => stored.Payload is WorkspaceBound);
    }

    [Fact]
    public async Task AFolderCorvidWillNotWorkInIsRefusedWithWhy()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        using var service = fixture.Sessions();

        var missing = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            service.CreateAsync(new NewSessionRequest(Path.Combine(fixture.Host.Root, "nowhere")), Ct));
        Assert.Contains("not a directory that exists", missing.Message, StringComparison.Ordinal);
        var relative = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.CreateAsync(new NewSessionRequest("src"), Ct));
        Assert.Contains("absolute path", relative.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionsCallsAreShownInItsOwnWorkspaceAndItsHeaderNamesIt()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        var other = Other(fixture);
        var id = fixture.Runtime.OpenSession(surface: "app", settings: new SessionSettings { Workspace = other }).Id;
        await WriteTurnAsync(fixture.Runtime, id, "call-1", "notes.txt", "there\n");
        await fixture.Runtime.ReleaseDriverAsync(id);
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();

        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        var call = subscription.History.OfType<AppToolCall>().Single(update => update.CallId == "call-1");
        // Relative to the session's workspace, not the window's (where it would stay absolute).
        Assert.Equal(["notes.txt"], call.Locations);
        var status = subscription.History.OfType<EngineStatus>().Last();
        Assert.Equal("other", status.Workspace);
    }

    [Fact]
    public async Task ReviewAndRewindReadTheSessionsOwnFilesAndCheckpoints()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        var other = Other(fixture);
        var runtime = fixture.Runtime;
        // The same path in both workspaces, written by a session in each.
        var home = runtime.OpenSession(surface: "app").Id;
        await WriteTurnAsync(runtime, home, "call-h", "story.txt", "home\n");
        var elsewhere = runtime.OpenSession(surface: "app", settings: new SessionSettings { Workspace = other }).Id;
        await WriteTurnAsync(runtime, elsewhere, "call-o", "story.txt", "there\n");
        using var service = fixture.Sessions();
        using var signals = new Corvid.App.Engine.History.HistorySignals(service, new Corvid.App.Core.Mvvm.InlineDispatcher());
        using var review = new EngineReviewService(fixture.Engine, service, fixture.Time, signals, null, null);
        using var rewind = new EngineRewindService(fixture.Engine, fixture.Time, signals);

        var changes = await review.GetChangesAsync(elsewhere, ReviewScope.ThisSession, Ct);
        var story = Assert.Single(changes.Files);
        Assert.Equal("story.txt", story.Path);
        Assert.Equal((null, "there\n"), (story.OldText, story.NewText));
        Assert.Equal("home\n", (await review.GetChangesAsync(home, ReviewScope.ThisSession, Ct)).Files.Single().NewText);

        var timeline = await rewind.GetTimelineAsync(elsewhere, Ct);
        var entry = Assert.Single(timeline.Entries);
        var restore = await rewind.RestoreAsync(elsewhere, entry.CheckpointId, RewindScope.FilesOnly, Ct);
        Assert.NotNull(restore);
        // The session's own file went back; the window's workspace kept its own.
        Assert.False(File.Exists(Path.Combine(other, "story.txt")));
        Assert.Equal("home\n", await File.ReadAllTextAsync(Path.Combine(runtime.DefaultWorkspace.Root, "story.txt"), Ct));
    }

    [Fact]
    public async Task ANewWorktreeIsMadeOnlyAfterThePersonApprovesItOnTheAskingSessionsCard()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        var work = Repository(fixture);
        using var service = fixture.Sessions();
        var asking = await service.CreateAsync(Ct);
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(asking, waiter.Add, Ct);

        var creating = service.CreateAsync(new NewSessionRequest { Worktree = true, AskIn = asking, WorktreeName = "feature" }, Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>();

        // Blocked on the card, outside any turn: nothing is made yet.
        Assert.False(creating.IsCompleted);
        Assert.Contains(card.Reasons, reason => reason.Code == ApprovalReasons.Required);
        Assert.DoesNotContain("corvid/feature", Git(work, "branch", "--list"), StringComparison.Ordinal);
        await service.AnswerApprovalAsync(asking, card.ApprovalId, ApprovalAnswer.Once, Ct);
        var id = await creating.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await service.IdleAsync();

        var summary = (await service.ListAsync(Ct)).Single(item => item.Id == id);
        Assert.Equal("corvid/feature", summary.Branch);
        Assert.EndsWith("/feature", summary.Workspace, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(summary.Workspace, "README.md")));
        Assert.Contains(waiter.Updates.OfType<ApprovalResolved>(), resolved => resolved.ApprovalId == card.ApprovalId && resolved.Outcome == ApprovalOutcome.Approved);
        // The main checkout's branch is untouched.
        Assert.Equal("main", Git(work, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task ADeniedWorktreeIsRefusedAndNothingIsMade()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        var work = Repository(fixture);
        using var service = fixture.Sessions();
        var asking = await service.CreateAsync(Ct);
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(asking, waiter.Add, Ct);

        var creating = service.CreateAsync(new NewSessionRequest { Worktree = true, AskIn = asking, WorktreeName = "nope" }, Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>();
        await service.AnswerApprovalAsync(asking, card.ApprovalId, ApprovalAnswer.Deny, Ct);

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => creating.WaitAsync(TimeSpan.FromSeconds(60), Ct));
        Assert.Contains("not approved", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("corvid/nope", Git(work, "branch", "--list"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Host.Root, "worktrees")) && Directory.EnumerateDirectories(Path.Combine(fixture.Host.Root, "worktrees"), "nope", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task WorkspaceNewTypedInTheComposerAsksOnTheSessionsCard()
    {
        await using var fixture = await EngineFixture.StartAsync(null, Builtin);
        var work = Repository(fixture);
        using var service = fixture.Sessions();
        var asking = await service.CreateAsync(Ct);
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(asking, waiter.Add, Ct);

        var running = service.RunCommandAsync(asking, "/workspace new typed", Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>();
        await service.AnswerApprovalAsync(asking, card.ApprovalId, ApprovalAnswer.Once, Ct);
        await running.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var result = await waiter.WaitAsync<CommandResult>();
        Assert.Equal(Corvid.App.Core.Sessions.OutputTone.Success, result.Tone);
        Assert.Contains("corvid/typed", Git(work, "branch", "--list"), StringComparison.Ordinal);
        // Once the command is done the session's approvals have no card again: a later ask outside a turn is refused, not shown.
        var after = await fixture.Runtime.CreateWorktreeAsync(asking, "later", cancellationToken: Ct).ContinueWith(task => task.Exception?.InnerException, Ct);
        Assert.IsType<WorktreeRefusedException>(after);
    }
}
